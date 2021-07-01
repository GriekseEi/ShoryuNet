using System;
using System.Linq;
using System.Net;

namespace ShoryuNet
{
    public struct InputState
    {
        public int Frame;
        public byte[] State;
    }

    /// <summary>
    /// Contains the connection information and inputs of a given player in the peernet
    /// </summary>
    class PlayerInfo
    {
        public IPEndPoint Endpoint { get; set; }
        public int Ping { get; set; }
        public bool Ready { get; set; }
        public PlayerType Type { get; set; }
        public byte Id { get; set; }
        public int LastAddedFrame { get; set; }

        private RingBuffer<InputState> _inputHistory;

        /// <summary>
        /// PlayerInfo constructor from manual data
        /// </summary>
        /// <param name="type">The type of this Player</param>
        /// <param name="ep">The Endpoint of this player</param>
        /// <param name="id">The ID of this player</param>
        public PlayerInfo(PlayerType type, IPEndPoint ep, byte id) 
        {
            Ping = 0;
            Ready = false;
            _inputHistory = new RingBuffer<InputState>(Constants.ROLLBACK_WINDOW);
            LastAddedFrame = 0;

            Id = id;
            Type = type;
            Endpoint = ep;

            // If the input delay is higher than zero, then we need to fill up the inputHistory with empty states for the first few frames
            for (int i = 0; i < Constants.INPUT_DELAY; i++)
            {
                _inputHistory[i] = new InputState { Frame = i, State = new byte[3] };
            }
        }

        /// <summary>
        /// PlayerInfo constructor from a foreign bytestream
        /// </summary>
        /// <param name="source">The payload from the foreign player</param>
        public PlayerInfo(byte[] source)
        {
            /*
             * A PlayerInfo byte array will look like this:
             * #0       Id
             * #1       Type
             * #2-6     Port
             * #6-...   IP Address
             * 
             * The bytes signifying the length of a PlayerInfo byte sequence should be stripped out beforehand.
             */
            Ping = 0;
            Ready = false;
            _inputHistory = new RingBuffer<InputState>(Constants.ROLLBACK_WINDOW);
            LastAddedFrame = 0;

            Id = source[0];
            Type = (PlayerType)source[1];
            if (Type == PlayerType.Local)
                Type = PlayerType.Remote;

            int port = BitConverter.ToInt32(source, 2);
            IPAddress address = new IPAddress(source.Skip(6).ToArray());
            Endpoint = new IPEndPoint(address, port);

            for (int i = 0; i < Constants.INPUT_DELAY; i++)
            {
                _inputHistory[i] = new InputState { Frame = i, State = new byte[3] };
            }
        }

        /// <summary>
        /// Convert this PlayerInfo to a byte array
        /// </summary>
        /// <returns>This PlayerInfo in the form of a byte array</returns>
        public byte[] GetBytes()
        {
            byte[] address = Endpoint.Address.GetAddressBytes();
            byte[] port = BitConverter.GetBytes(Endpoint.Port);
            byte[] payload = new byte[sizeof(int) + (sizeof(byte) * 2) + address.Length + port.Length];

            // Converting an IPEndPoint to bytes does not have a fixed length, so we need to append the length of the entire payload before it
            BitConverter.GetBytes(payload.Length - sizeof(int)).CopyTo(payload, 0);
            payload[4] = Id;
            payload[5] = (byte)Type;
            port.CopyTo(payload, 6);
            address.CopyTo(payload, 10);

            return payload;
        }

        /// <summary>
        /// Get an input from the input history
        /// </summary>
        /// <param name="frame"></param>
        /// <returns>The inputs of this player for a given frame</returns>
        public byte[] GetInput(int frame)
        {
            InputState input = _inputHistory[frame];
            return input.State;
        }

        /// <summary>
        /// Add an input of a frame to the input history
        /// </summary>
        /// <param name="frame">The frame of these inputs</param>
        /// <param name="state">The inputs themselves</param>
        /// <returns>True if adding it was a success, False otherwise</returns>
        public bool AddInput(int frame, byte[] state)
        {
            if (frame <= LastAddedFrame) return false;

            _inputHistory[frame] = new InputState { Frame = frame, State = state };
            return true;
        }

        public override string ToString()
        {
            return $"PLAYER #{Id}@{Endpoint} / {Type}";
        }
    }
}
