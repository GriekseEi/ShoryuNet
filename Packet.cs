using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;

namespace ShoryuNet
{
    public enum PacketType : byte
    {
        RequestJoin = 1,    // Client Request to join a game
        RequestSpectate,
        Discovery,
        AcceptJoin,         // Server accepts join
        AcceptJoinAck,      // Client acknowledges the AcceptJoin
        GameStart,          // Server tells Clients game is starting
        GameStartAck,       // Client acknowledges the GameStart
        GameState,          // Server tells Clients the gamestate
        Bye,                // Either Server or Client tells the other to end the connection
        NotifyJoin,
        NotifyJoinAck,
        NotifyFailure,
    }

    public class Packet
    {
        public PacketType Type;
        public byte PlayerId;
        public int Frame;
        public long Timestamp;
        public byte[] Payload = new byte[0];

        private const int HEADER_LENGTH = sizeof(PacketType) + sizeof(byte) + sizeof(int) + sizeof(long);

        #region Constructor
        public Packet(PacketType type)
        {
            this.Type = type;
            PlayerId = 0x00;
            Frame = -1;
            Timestamp = DateTime.Now.Ticks;
        }

        public Packet(byte[] bytes)
        {
            Type = (PacketType)bytes[0];
            PlayerId = bytes[1];
            Frame = BitConverter.ToInt32(bytes, 2);
            Timestamp = BitConverter.ToInt64(bytes, 6);
            Payload = bytes.Skip(HEADER_LENGTH).ToArray();
        }

        public byte[] GetBytes()
        {
            byte[] buffer = new byte[HEADER_LENGTH + Payload.Length];

            buffer[0] = (byte)Type;
            buffer[1] = PlayerId;
            BitConverter.GetBytes(Frame).CopyTo(buffer, 2);
            BitConverter.GetBytes(Timestamp).CopyTo(buffer, 6);
            Payload.CopyTo(buffer, HEADER_LENGTH);

            return buffer;
        }

        public override string ToString()
        {
            return string.Format("[Packet: {0}\n  timestamp={1}\n payload size={2}]", Type, new DateTime(Timestamp), Payload.Length);
        }

        public void Send(UdpClient client, IPEndPoint receiver)
        {
            byte[] bytes = GetBytes();
            client.Send(bytes, bytes.Length, receiver);
        }

        public void Send(UdpClient client)
        {
            byte[] bytes = GetBytes();
            client.Send(bytes, bytes.Length);
        }
    }
#endregion

    #region Specific Packets
    public class RequestJoinPacket : Packet
    {
        public RequestJoinPacket() : base(PacketType.RequestJoin) { }
    }

    public class RequestSpectatePacket : Packet
    {
        public RequestSpectatePacket() : base(PacketType.RequestSpectate) { }
    }

    public class AcceptJoinPacket : Packet
    {
        public AcceptJoinPacket() : base(PacketType.AcceptJoin)
        {

        }

        public AcceptJoinPacket(byte[] bytes) : base(bytes) { }
    }

    public class AcceptJoinAckPacket : Packet
    {
        public AcceptJoinAckPacket() : base(PacketType.AcceptJoinAck) { }
    }

    public class GameStartPacket : Packet
    {
        public GameStartPacket() : base(PacketType.GameStart) { }
    }

    public class GameStartAckPacket : Packet
    {
        public GameStartAckPacket() : base(PacketType.GameStartAck) { }
    }

    public class InputPacket : Packet
    {
        public InputPacket() : base(PacketType.GameState) { }
    }

    public class ByePacket : Packet
    {
        public ByePacket() : base(PacketType.Bye) { }
    }

    public class NotifyJoinPacket : Packet
    {
        public NotifyJoinPacket() : base(PacketType.NotifyJoin) { }
    }

    public class NotifyJoinAckPacket : Packet
    {
        public NotifyJoinAckPacket() : base(PacketType.NotifyJoinAck) { }
    }

    public class NotifyFailurePacket : Packet
    {
        public NotifyFailurePacket() : base(PacketType.NotifyFailure) { }
    }
    #endregion
}
