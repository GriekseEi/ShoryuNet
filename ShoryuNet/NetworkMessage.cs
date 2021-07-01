using System;
using System.Net;

namespace ShoryuNet
{
    /// <summary>
    /// A wrapper class for received packets which contains additional data
    /// </summary>
    public class NetworkMessage
    {
        // Tells us where the Packet came from
        public IPEndPoint Sender { get; set; }
        // Tells us when we received the Packet
        public DateTime ReceiveTime { get; set; }
        // The Packet itself
        public Packet Packet { get; set; }
        // Tells us what the travel in time in milliseconds was for this Packet
        public int Ping { get; set; }
    }
}
