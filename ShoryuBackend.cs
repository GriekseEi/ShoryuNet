using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;

namespace ShoryuNet
{
    public class ShoryuBackend
    {
        private UdpClient _local;
        private IPEndPoint _remote;

        // The queues in which packets are queued to be sent, and received packets are queued to be read
        private ConcurrentQueue<NetworkMessage> _incomingMessages = new ConcurrentQueue<NetworkMessage>();
        private ConcurrentQueue<Tuple<Packet, IPEndPoint>> _outgoingMessages = new ConcurrentQueue<Tuple<Packet, IPEndPoint>>();
        private ConcurrentDictionary<byte, PlayerInfo> _players = new ConcurrentDictionary<byte, PlayerInfo>();

        private DateTime _lastPacketSentTime = DateTime.MinValue;
        private DateTime _gameStartTime = DateTime.MinValue;

        private Thread _networkThread;
        private Thread _readPacketThread;

        private ThreadSafe<bool> _running = new ThreadSafe<bool>(false);

        // Delayed packet sending actions are stored in this list
        private List<System.Threading.Timer> _testTimers = new List<System.Threading.Timer>();

        private int _inputSize;
        private int _frameCount;
        private int _simulatedPing;
        private int _simulatedPacketLoss;
        private int _packetCounter;
        private int _rollbackPoint;

        private readonly Mutex _rollbackMutex;

        private Callbacks _cb;

        public ClientState State { 
            get { return _state; } 
            set { 
                _state = value;
                // Let client know each time when the State has been changed
                _cb.EventCallback(value);
            } 
        }
        private ClientState _state = ClientState.NotConnected;

        private readonly byte LocalId;

        /// <summary>
        /// Handles P2P connections with other players and applies rollback where necessary
        /// </summary>
        /// <param name="localPort">The local port from which we should send and receive packets</param>
        /// <param name="inputSize">The size in bytes of the GameState packets we will be exchanging. This should be the same for all peers</param>
        /// <param name="cb">The struct for all the callbacks to the host program</param>
        /// <param name="type">Defines whether the local host is a player or a spectator</param>
        /// <param name="localId">The id of the local host in the peernet</param>
        /// <param name="simulatedPing">Optional: The amount of time in milliseconds we want to delay sending GameState packets to other peers</param>
        /// <param name="simulatedPacketLoss">Optional: Defines </param>
        public ShoryuBackend(int localPort, int inputSize, Callbacks cb, PlayerType type, byte localId, int simulatedPing = 0, int simulatedPacketLoss = 0)
        {
            // Check if the given parameters are valid
            if (type == PlayerType.Remote)              throw new Exception("Can't initialize ShoryuBackend as a remote player");
            if (inputSize < 1)                          throw new Exception("Input size cannot be smaller than one");
            if (simulatedPing < 0)                      throw new Exception("Simulated ping delay cannot be negative");
            if (simulatedPacketLoss < 0)                throw new Exception("Simulated packet loss cannot be negative");
            if (localPort < 1024 && localPort > 65535)  throw new Exception("Invalid port number: local port must be or be between 1024 and 65535");

            // Setup local variables
            _frameCount = 0;
            _packetCounter = 0;
            _rollbackPoint = -1;

            _local = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.1"), localPort));
            _cb = cb;
            _inputSize = inputSize;
            _simulatedPing = simulatedPing;
            _simulatedPacketLoss = simulatedPacketLoss;
            _rollbackMutex = new Mutex();
            LocalId = localId;

            // Add local player to player list
            PlayerInfo local = new PlayerInfo(type, (IPEndPoint)_local.Client.LocalEndPoint, LocalId);
            bool add = _players.TryAdd(localId, local);
            if (!add) 
                throw new Exception("Somehow failed to add local player on initialization, this really shouldn't be possible");
            else 
                cb.DebugOutput($"Local player is set up @ {_local.Client.LocalEndPoint}");

            _running.Value = true;
            State = ClientState.WaitingForGameStart;

            // Start the networking threads
            _networkThread = new Thread(new ThreadStart(_networkRun));
            _readPacketThread = new Thread(new ThreadStart(_packetRead));
            _networkThread.Start();
            _readPacketThread.Start();
        }

        /// <summary>
        /// Connects local player to a remote peer
        /// </summary>
        /// <param name="remoteHost">The endpoint of the remote peer</param>
        public void Connect(IPEndPoint remoteHost)
        {
            _remote = remoteHost;
            State = ClientState.EstablishingConnection;
        }

        /// <summary>
        /// Initializes the game for all peers
        /// </summary>
        public void Start()
        {
            // If the local player has connected to another and there's more than one non-spectator peer in the peernet, begin the game
            if (State == ClientState.WaitingForGameStart && _getPlayerCount() > 1)
            {
                // Schedule the game to start two seconds from now instead of as soon as one receives the GameStart packet, in order to avoid desyncs
                _gameStartTime = DateTime.UtcNow.Add(TimeSpan.FromSeconds(Constants.GAME_START_TIME));

                // Game start schedule times are sent with UTC as a timezone, which after being received are then converted to the local timezone of each peer,
                // to avoid timezone issues
                _scheduleGameStart(_gameStartTime.ToLocalTime());

                _cb.DebugOutput($"Beginning game at {_gameStartTime.ToLocalTime()}");
                State = ClientState.BeginningGame;
            }
            else
            {
                _cb.DebugOutput("Can't start game unless we have more than one non-spectator player");
            }
        }

        /// <summary>
        /// Closes connections with other peers and frees up local resources
        /// </summary>
        public void Close()
        {
            _cb.DebugOutput("Shutting down client...");

            // Shut down the network threads
            _running.Value = false;
            State = ClientState.GameOver;
            _networkThread?.Join(TimeSpan.FromSeconds(5));
            _readPacketThread?.Join(TimeSpan.FromSeconds(5));

            // Send a disconnect packet manually to all foreign peers
            ByePacket bp = new ByePacket();
            bp.PlayerId = LocalId;
            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                if (player.Id != LocalId) bp.Send(_local, player.Endpoint);
            }
            
            _local.Close();
            _cb.DebugOutput("Shutdown complete");
        }

        /// <summary>
        /// Takes the input for this frame of the local player, stores it, and sends it to all other peers
        /// </summary>
        /// <param name="input">The inputs to store and send</param>
        public void AddInput(byte[] input)
        {
            // Cannot call this function if you're not in game or if you're a spectator
            if (State == ClientState.InGame && _players[LocalId].Type != PlayerType.Spectator)
            {
                // Check if given input is equal to the input size passed to the constructor of ShoryuBackend
                if (input.Length != _inputSize) throw new Exception("Length of passed input was not equal to stated input-size");

                // If true, LastAddedFrame for the local player will be incremented by one
                bool inputAccepted = _players[LocalId].AddInput(_frameCount + Constants.INPUT_DELAY, input);

                if (inputAccepted)
                {
                    // Copy the inputs for the current frame and the previous MAX_RESERVE_PAYLOADS amount of frames into the payload
                    byte[] payload = new byte[_inputSize * (Constants.MAX_RESERVE_PAYLOADS + 1)];
                    for (int i = 0; i < Constants.MAX_RESERVE_PAYLOADS + 1; i++)
                    {
                        int destIndex = _frameCount + Constants.INPUT_DELAY - i;

                        _players[LocalId].GetInput(destIndex).CopyTo(payload, i * _inputSize);
                    }

                    // If the input delay is f.e. 2, then that means that the inputs we pressed at frame 1 will only be executed for all peers at frame 3.
                    // An input delay gives ShoryuNet more time to send and receive packets without having to roll back.
                    InputPacket inp = new InputPacket();
                    inp.Frame = _frameCount + Constants.INPUT_DELAY;
                    inp.PlayerId = LocalId;
                    inp.Payload = payload;

                    _testTimers.Add(new System.Threading.Timer(x =>
                    {
                        _sendPacketToAllPlayers(inp);
                    }, null, _simulatedPing, Timeout.Infinite));

                    _players[LocalId].LastAddedFrame = _frameCount;
                } else
                {
                    _cb.DebugOutput($"AddInput Error: Inputs on frame {_frameCount} already exist for local player");
                }
            } else
            {
                _cb.DebugOutput("Can't call AddInput until the client is in a game");
            }
        }

        /// <summary>
        /// Checks to see if all peers are in sync and returns their latest inputs for this frame
        /// </summary>
        /// <returns>The inputs of each player for a given frame</returns>
        public byte[] SyncInput()
        {
            if (State == ClientState.InGame)
            {
                // Set the mutex so that a recently received packet from the networking threads doesn't scramble up things here
                _rollbackMutex.WaitOne();

                // If _rollbackPoint isn't -1, that means a rollback is necessary
                if (_rollbackPoint != -1)
                {
                    // Roll back the amount of necessary frames with the updated inputs, except for the current frame
                    for (int i = _rollbackPoint; i < _frameCount; i++)
                    {
                        byte[] output = _getAllInputsByFrame(i);
                        _cb.DebugOutput($"ROLLBACK: Rolling back frame: {i} | Target: {_frameCount} | Output: {PrintSyncInputPayload(output)}");
                        _cb.LoadState(i, output);
                    }
                    _rollbackPoint = -1;
                }

                // Get the inputs for the current frame
                byte[] inputs = _getAllInputsByFrame(_frameCount);
                _cb.DebugOutput(PrintSyncInputPayload(inputs));
                // Increment the local frame counter
                _frameCount++;

                _rollbackMutex.ReleaseMutex();

                return inputs;

            } else
            {
                throw new Exception("Can't call SyncInput when not in-game");
            }
        }

        /// <summary>
        /// Get the newest inputs from each player for a given frame
        /// </summary>
        /// <param name="frame">The frame from which we should get all inputs from</param>
        /// <returns>An array of inputs from each player</returns>
        private byte[] _getAllInputsByFrame(int frame)
        {
            // For each player, we add their id and the inputs as bytes for the given frame to the output array
            byte[] inputs = new byte[_getPlayerCount() * (1 + _inputSize)];
            int count = 0;

            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                if (player.Type != PlayerType.Spectator)
                {
                    inputs[count * (1 + _inputSize)] = player.Id;
                    byte[] playerInputs = player.GetInput(frame);
                    if (playerInputs == null)
                    {
                        // If no inputs are found for the given frame, then instead use the inputs of the last added frame of this player to the output array
                        player.GetInput(player.LastAddedFrame).CopyTo(inputs, count * (1 + _inputSize) + 1);
                        _cb.DebugOutput($"Player {player.Id} has no inputs for frame: {frame}, using last saved inputs from {player.LastAddedFrame}");
                    } else
                    {
                        playerInputs.CopyTo(inputs, count * (1 + _inputSize) + 1);
                    }
                    count++;
                }
            }
            return inputs;
        }

        /// <summary>
        /// Count how many non-spectator players we currently know about
        /// </summary>
        /// <returns>The amount of non-spectator players currently in the peernet</returns>
        private int _getPlayerCount()
        {
            int count = 0;
            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                if (player.Type != PlayerType.Spectator) count++;
            }
            return count;
        }

        /// <summary>
        /// The function responsible for handling the sending and receiving of packets
        /// </summary>
        private void _networkRun()
        {
            // Continue to run this in a loop until Close() is called
            while (_running.Value)
            {
                // Check if there's any data to read or send to begin with
                bool canRead = _local.Available > 0;
                int numToWrite = _outgoingMessages.Count;

                if (canRead)
                {
                    try
                    {
                        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _local.Receive(ref ep);

                        // Wrap the received data in a NetworkMessage
                        NetworkMessage nm = new NetworkMessage();
                        nm.Sender = ep;
                        nm.Packet = new Packet(data);
                        nm.ReceiveTime = DateTime.Now;
                        // Calculate the ping in milliseconds by checking the difference between its send and receive time
                        nm.Ping = Convert.ToInt32(new TimeSpan(nm.ReceiveTime.Ticks - nm.Packet.Timestamp).TotalMilliseconds);

                        // Enqueue this message to be later read by _packetRead()
                        _incomingMessages.Enqueue(nm);
                    } catch (SocketException e)
                    {
                        // Should a network-related exception happen for whatever reason, then close the local client
                        _cb.DebugOutput("Connection failure; terminating...");
                        Close();
                    }
                }

                // Send all packets currently queued in _outgoingMessages
                // Note: This needs to happen after receiving data, because we cannot send and receive simultaneously from the same port
                for (int i = 0; i < numToWrite; i++)
                {
                    Tuple<Packet, IPEndPoint> msg;
                    bool have = _outgoingMessages.TryDequeue(out msg);
                    if (have)
                    {
                        msg.Item1.Send(_local, msg.Item2);
                        _lastPacketSentTime = DateTime.Now;
                    }
                }

                // If there's nothing to send or receive, then sleep for a millisecond
                if (!canRead && (numToWrite == 0)) Thread.Sleep(1);
            }
        }

        /// <summary>
        /// The function responsible for reading packets, storing remote inputs, and handling P2P connections
        /// </summary>
        private void _packetRead()
        {
            while (_running.Value)
            {
                NetworkMessage message;
                bool haveMsg = _incomingMessages.TryDequeue(out message);

                // A ByePacket should be treated the same regardless of the current ClientState, and takes priority over everything else
                if (haveMsg && message.Packet.Type == PacketType.Bye)
                {
                    _rollbackMutex.WaitOne();

                    // Remove a player from the player list depending on the PlayerId in the ByePacket
                    PlayerInfo player;
                    bool removed = _players.TryRemove(message.Packet.PlayerId, out player);
                    if (removed)
                    {
                        _cb.DebugOutput($"Received goodbye from {player}");
                        // If there's only 1 player remaining, then close up shop here as well
                        if (_getPlayerCount() < 2)
                        {
                            _cb.DebugOutput("No remote players exist anymore, shutting down...");
                            Close();
                        }
                    }
                    else
                    {
                        _cb.DebugOutput($"Somehow failed to remove {player}");
                    }

                    _rollbackMutex.ReleaseMutex();
                }

                // How we handle packets depends on our current ClientState
                switch (State)
                {
                    // For when we're connecting to the peernet
                    case ClientState.EstablishingConnection:
                        // Send a RequestJoin every few seconds 
                        _sendRequestJoin(TimeSpan.FromSeconds(Constants.REQUEST_JOIN_RETRY_TIME));

                        // Handle the response from the foreign peer
                        if (haveMsg)
                        {
                            if (message.Packet.Type == PacketType.AcceptJoin)
                            {
                                _handleNewPeers(message.Packet.Payload);
                                _sendAcceptJoinAck(message.Packet.PlayerId);
                                State = ClientState.WaitingForGameStart;
                            }
                        }
                        break;

                    // For when we're waiting for the game to start
                    case ClientState.WaitingForGameStart:
                        if (haveMsg)
                        {
                            switch (message.Packet.Type)
                            {
                                case PacketType.RequestJoin:
                                    // Add new player to local list and respond with an AcceptJoin
                                    _addPlayer(message, PlayerType.Remote);

                                    // Send a NotifyJoin to other players about the new player
                                    _sendNotifyJoin(message.Packet.PlayerId);                  
                                    break;

                                case PacketType.RequestSpectate:
                                    // Add new player to local list and respond with an AcceptJoin
                                    _addPlayer(message, PlayerType.Spectator);

                                    // Send a NotifyJoin to other players about the new player
                                    _sendNotifyJoin(message.Packet.PlayerId);
                                    break;

                                case PacketType.NotifyJoin:
                                    // Skip the first four bytes since it signifies the length of the rest, which we don't really need because a NotifyJoin will always contain one PlayerInfo
                                    PlayerInfo newPlayer = new PlayerInfo(message.Packet.Payload.Skip(sizeof(int)).ToArray());
                                    bool success = _players.TryAdd(newPlayer.Id, newPlayer);
                                    if (success)
                                    {
                                        // On success, respond with a NotifyJoinAck
                                        _cb.DebugOutput($"Player #{message.Packet.PlayerId} notified us of new {newPlayer}");
                                        NotifyJoinAckPacket njap = new NotifyJoinAckPacket();
                                        njap.PlayerId = LocalId;
                                        _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(njap, _players[message.Packet.PlayerId].Endpoint));
                                    }
                                    else
                                    {
                                        // On failure, respond with a NotifyFailure
                                        _handlePlayerAddFailure(newPlayer, message.Packet.PlayerId);
                                    }
                                    break;

                                case PacketType.NotifyJoinAck:
                                    _cb.DebugOutput($"Received ACK on NotifyJoin from {_players[message.Packet.PlayerId]}");
                                    break;

                                case PacketType.NotifyFailure:
                                    _cb.DebugOutput($"Received Failure on NotifyJoin from {_players[message.Packet.PlayerId]}");
                                    break;

                                case PacketType.AcceptJoin:
                                    _cb.DebugOutput($"Successfully joined with {_players[message.Packet.PlayerId]}");
                                    _sendAcceptJoinAck(message.Packet.PlayerId);
                                    break;

                                case PacketType.AcceptJoinAck:
                                    _cb.DebugOutput($"Received AcceptJoinAck from {_players[message.Packet.PlayerId]}");
                                    break;

                                case PacketType.GameStart:
                                    _sendGameStartAck(message.Packet.PlayerId);

                                    // Get the game start time by converting the payload to a DateTime in the local timezone
                                    _gameStartTime = DateTime.FromBinary(BitConverter.ToInt64(message.Packet.Payload, 0)).ToLocalTime();
                                    State = ClientState.BeginningGame;
                                    _cb.DebugOutput($"Beginning game at {_gameStartTime}");

                                    _scheduleGameStart(_gameStartTime);
                                    break;

                                case PacketType.GameStartAck:
                                    _players[message.Packet.PlayerId].Ready = true;  // Set Ready to true so we can stop spamming this player with GameStart packets
                                    _cb.DebugOutput($"Received GameStartAck from {_players[message.Packet.PlayerId]}");
                                    break;
                            }
                        }
                        break;

                    // For when a peer in the peernet called Start()
                    case ClientState.BeginningGame:
                        // Send a GameStart packet to all peers
                        _sendGameStart(TimeSpan.FromSeconds(0.2));
                        break;

                    // For when the game has been started
                    case ClientState.InGame:
                        if (haveMsg)
                        {
                            switch (message.Packet.Type)
                            {
                                case PacketType.RequestSpectate:
                                    _addPlayer(message, PlayerType.Spectator);
                                    break;

                                case PacketType.AcceptJoinAck:
                                    _cb.DebugOutput($"Received AcceptJoinAck from {_players[message.Packet.PlayerId]}");
                                    break;

                                case PacketType.GameState:
                                    PlayerInfo player = _players[message.Packet.PlayerId];

                                    // Packet arrived too late as we already have a newer input, so discard it 
                                    if (message.Packet.Frame <= player.LastAddedFrame)
                                    {
                                        _cb.DebugOutput($"DISCARDED LATE PACKET FROM ID: {message.Packet.PlayerId} | FRAME: {message.Packet.Frame}");
                                        break;
                                    }
                                    else
                                    {
                                        // If simulated packet loss is enabled, then we do nothing with a read packet if _packetCounter >= _simulatedPacketLoss
                                        if (_simulatedPacketLoss == 0 || (_simulatedPacketLoss > 0 && _packetCounter < _simulatedPacketLoss))
                                        {
                                            _rollbackMutex.WaitOne();

                                            int newFrame = message.Packet.Frame;

                                            // Add newest inputs from the remote player to its input history
                                            player.AddInput(newFrame, message.Packet.Payload.Take(_inputSize).ToArray());
                                            _cb.DebugOutput($"RCV -- ID: {player.Id} | FRAME: {newFrame} | PAYLOAD: {PrintBytes(player.GetInput(newFrame))} | PING: {message.Ping}");

                                            // A packet that arrives with a framecount difference higher than 1 means that a packet with framecount+1 was lost or arrived too slow,
                                            // so repopulate the missing frames using the extra inputs of the previous frames in the payload
                                            if (message.Packet.Frame > (player.LastAddedFrame + 1))
                                            {
                                                int counter = 1;
                                                for (int i = newFrame - 1; i > player.LastAddedFrame; i--)
                                                {
                                                    player.AddInput(i, message.Packet.Payload.Skip(counter * _inputSize).Take(_inputSize).ToArray());
                                                    _cb.DebugOutput($"POPULATE -- ID: {player.Id} | FRAME: {i} | PAYLOAD: {PrintBytes(player.GetInput(i))}");
                                                    counter++;
                                                }
                                            }

                                            if (_frameCount - player.LastAddedFrame > 0)
                                            {
                                                // If the rollback point hasn't been set or if the rollback point has been set but this player's lastAddedFrame is smaller than it,
                                                // update the rollback point with this player's LastAddedFrame
                                                if ((_rollbackPoint != -1 && player.LastAddedFrame < _rollbackPoint) || _rollbackPoint == -1)
                                                {
                                                    _rollbackPoint = player.LastAddedFrame;
                                                    _cb.DebugOutput($"Set rollback point to {player.LastAddedFrame}");
                                                }
                                            }

                                            player.Ping = message.Ping;
                                            player.LastAddedFrame = newFrame;

                                            _rollbackMutex.ReleaseMutex();
                                        }

                                        // Reset _packetCounter or increment it depending on whether simulated packet loss is enabled
                                        if (_simulatedPacketLoss > 0 && _simulatedPacketLoss == _packetCounter) _packetCounter = 0;
                                        else if (_simulatedPacketLoss > 0) _packetCounter++;
                                    }
                                    break;
                            }
                        }
                        break;

                    case ClientState.GameOver:
                        break;
                        // purgatory
                }

                // If there's no messages to read, sleep for a millisecond
                if (!haveMsg) Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Handles RequestJoin/RequestSpectate packages and adds the requesting player to the local player list
        /// </summary>
        /// <param name="message">The NetworkMessage we received from the requesting player</param>
        /// <param name="type">The PlayerType the requesting player identified as</param>
        private void _addPlayer(NetworkMessage message, PlayerType type)
        {
            // Setup PlayerInfo for new player
            PlayerInfo player = new PlayerInfo(type, message.Sender, message.Packet.PlayerId);

            bool success = _players.TryAdd(player.Id, player);
            if (success)
            {
                // On success, respond with an AcceptJoin
                _sendAcceptJoin(message.Packet.PlayerId);
                if (player.Type == PlayerType.Local) _cb.DebugOutput($"Received RequestJoin: Added {player}");
                else _cb.DebugOutput($"Received RequestSpectate: Added {player}");
            }
            else
            {
                // On failure, respond with an NotifyFailure
                _handlePlayerAddFailure(player, message.Packet.PlayerId);
            }
        }

        /// <summary>
        /// Responds with a NotifyFailure to target player
        /// </summary>
        /// <param name="player">The player that failed to join</param>
        /// <param name="senderId">The id of the player sending the RequestJoin/Spectate</param>
        private void _handlePlayerAddFailure(PlayerInfo player, byte senderId)
        {
            _cb.DebugOutput($"This player already exists in the peernet: {player}");
            NotifyFailurePacket nfp = new NotifyFailurePacket();
            nfp.PlayerId = LocalId;
            _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(nfp, _players[senderId].Endpoint));
        }

        /// <summary>
        /// Schedules ShoryuNet to start the game
        /// </summary>
        /// <param name="scheduleDate">The date at which the game should start</param>
        private void _scheduleGameStart(DateTime scheduleDate)
        {
            DateTime current = DateTime.Now;
            TimeSpan timeToGo = scheduleDate - current;

            // If there's (less than) no time remaining, that means the GameStart packet arrived too late and we should discard it
            if (timeToGo < TimeSpan.Zero) return;

            // Schedule the game to begin at the given date
            _testTimers.Add(new System.Threading.Timer(x =>
            {
                _beginGame();
            }, null, timeToGo, Timeout.InfiniteTimeSpan));
        }

        /// <summary>
        /// Starts the game
        /// </summary>
        private void _beginGame()
        {
            _cb.DebugOutput($"GAME HAS STARTED AT: {DateTime.Now}");
            State = ClientState.InGame;
        }

        /// <summary>
        /// Sends a Packet to all players ShoryuNet knows about
        /// </summary>
        /// <param name="packet">The Packet that needs to be sent</param>
        private void _sendPacketToAllPlayers(Packet packet)
        {
            packet.PlayerId = LocalId;
            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                // We don't want to send a packet back to ourselves
                if (player.Id != LocalId)
                {
                    _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(packet, player.Endpoint));
                }
            }
        }

        /// <summary>
        /// Send a GameStartAck to target player
        /// </summary>
        /// <param name="targetId">The id of the target player</param>
        private void _sendGameStartAck(byte targetId)
        {
            _cb.DebugOutput($"SEND:: GameStartAck to {_players[targetId]}");
            GameStartAckPacket gsap = new GameStartAckPacket();
            gsap.PlayerId = LocalId;
            _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(gsap, _players[targetId].Endpoint));
        }

        /// <summary>
        /// Send a GameStart packet to all players
        /// </summary>
        /// <param name="retryTimeout">The time in seconds for how much time needs to pass since sending the last packet before we're allowed to send another GameStart packet</param>
        private void _sendGameStart(TimeSpan retryTimeout)
        {
            // Only send a GameStart (again) if enough time has passed since we last sent a packet
            if (DateTime.Now >= _lastPacketSentTime.Add(retryTimeout))
            {
                GameStartPacket gsp = new GameStartPacket();
                gsp.PlayerId = LocalId;

                // Embed the DateTime at which the game should start as a float in the payload
                gsp.Payload = BitConverter.GetBytes(_gameStartTime.Ticks);
                foreach (var key in _players)
                {
                    // Send a GameStart to every player that isn't us or hasn't responded with a GameStartAck
                    PlayerInfo player = key.Value;
                    if (player.Id != LocalId || player.Ready)
                    {
                        gsp.PlayerId = LocalId; // Set player ID of packet to local ID
                        _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(gsp, player.Endpoint));
                    }
                }
            }
        }

        /// <summary>
        /// Send a Request to signal we want to join a peernet
        /// </summary>
        /// <param name="retryTimeout">How many time in seconds needs to pass before we're allowed to send another packet</param>
        private void _sendRequestJoin(TimeSpan retryTimeout)
        {
            if (DateTime.Now >= _lastPacketSentTime.Add(retryTimeout))
            {
                if (_players[LocalId].Type == PlayerType.Local)
                {
                    RequestJoinPacket gsp = new RequestJoinPacket();
                    gsp.PlayerId = LocalId;
                    _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(gsp, _remote));
                } else
                {
                    RequestSpectatePacket rsp = new RequestSpectatePacket();
                    rsp.PlayerId = LocalId;
                    _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(rsp, _remote));
                }

                _cb.DebugOutput($"Sending join request to {_remote}");
                _lastPacketSentTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Send an AcceptJoin, which includes the info on all players in the peernet
        /// </summary>
        /// <param name="requesterId">The id of the player we want to send an AcceptJoin to</param>
        private void _sendAcceptJoin(byte requesterId)
        {
            // All byte arrays of PlayerInfos are first stored in a list with no fixed size, because IPEndPoints have no fixed length in bytes
            List<byte> payload = new List<byte>();
            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                if (player.Id != requesterId)  // We don't need to send the requester's PlayerInfo
                {
                    payload.AddRange(player.GetBytes());
                }
            }

            AcceptJoinPacket ajp = new AcceptJoinPacket();
            ajp.Payload = payload.ToArray();
            ajp.PlayerId = LocalId;

            _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(ajp, _players[requesterId].Endpoint));
            _cb.DebugOutput($"SEND: Accept Join to {_players[requesterId]}");
        }

        /// <summary>
        /// Send a Packet notifying other peers of a player that recently joined
        /// </summary>
        /// <param name="newPlayerId">The id of the new player</param>
        private void _sendNotifyJoin(byte newPlayerId)
        {
            // Get only the PlayerInfo of the newest player in bytes
            PlayerInfo newPlayer = _players[newPlayerId];
            byte[] playerData = newPlayer.GetBytes();

            foreach (var key in _players)
            {
                PlayerInfo player = key.Value;
                // Send the NotifyJoin to all other players that aren't us or the new player
                if (player.Id != newPlayerId && player.Id != LocalId)
                {
                    NotifyJoinPacket njp = new NotifyJoinPacket();
                    njp.Payload = playerData;
                    njp.PlayerId = LocalId;
                    _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(njp, player.Endpoint));
                    _cb.DebugOutput($"SEND: NotifyJoin about PLAYER #{newPlayerId} to {player}");
                }
            }
        }

        /// <summary>
        /// Send an AcceptJoinAck to the target player
        /// </summary>
        /// <param name="targetId">The id of the player to send a packet to</param>
        private void _sendAcceptJoinAck(byte targetId)
        {
            PlayerInfo targetPlayer = _players[targetId];
            _cb.DebugOutput($"SEND: Accept Join Ack to {targetPlayer}");

            AcceptJoinAckPacket ajap = new AcceptJoinAckPacket();
            ajap.PlayerId = LocalId;
            _outgoingMessages.Enqueue(new Tuple<Packet, IPEndPoint>(ajap, targetPlayer.Endpoint));
        }

        /// <summary>
        /// Adds all players in an AcceptJoin to the local player list
        /// </summary>
        /// <param name="payload">The byte array we have to parse PlayerInfo's from</param>
        private void _handleNewPeers(byte[] payload)
        {
            int index = 0;

            // Keep checking for new sequences in a payload depending on its length 
            while (index < payload.Length)
            {
                // The first four bytes in a sequence signify the length of the PlayerInfo data that comes after, which we want to parse out from the rest
                int length = BitConverter.ToInt32(payload, index);
                PlayerInfo player = new PlayerInfo(payload.Skip(index + sizeof(int)).Take(length).ToArray());
                bool success = _players.TryAdd(player.Id, player);
                if (success)
                {
                    _cb.DebugOutput($"Added {player}");
                } else
                {
                    _cb.DebugOutput($"Failed to add {player}");
                    break;
                }

                // Increment the counter so that it will point at the next sequence in the payload
                index += sizeof(int) + length;
            }
        }

        /// <summary>
        /// A helper function for printing the contents of a byte array to a string
        /// </summary>
        /// <param name="input">The byte array we want to convert to a string</param>
        /// <returns>The contents of a byte array as a string</returns>
        public string PrintBytes(byte[] input)
        {
            StringBuilder builder = new StringBuilder();
            foreach (byte sign in input) 
                builder.Append(sign.ToString() + " ");

            return builder.ToString().Trim();
        }

        /// <summary>
        /// A helper function for converting the byte array output of SyncInput to a string
        /// </summary>
        /// <param name="payload">The output of SyncInput</param>
        /// <returns>The output of SyncInput as a string</returns>
        public string PrintSyncInputPayload(byte[] payload)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append($"SYNCINPUT --  FRAME: {_frameCount} | ");

            int counts = payload.Length / (1 + _inputSize);
            for (int i = 0; i < counts; i++)
            {
                // LINQ does some weird things if you call Skip(0), so if i == 0 then we avoid calling Skip() altogether
                if (i == 0)
                {
                    byte[] part = payload.Take(1 + _inputSize).ToArray();
                    builder.Append($"PLAYER #{part[0]}: {PrintBytes(part.Skip(1).ToArray())} | ");
                } else
                {
                    byte[] part = payload.Skip(i * (1 + _inputSize)).Take(1 + _inputSize).ToArray();
                    builder.Append($"PLAYER #{part[0]}: {PrintBytes(part.Skip(1).ToArray())} | ");
                }
            }

            return builder.ToString().Trim();
        }
    }
}
