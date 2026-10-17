using System;
using System.Net;
using System.Net.Sockets;
using Mirror;
using kcp2k;

namespace Mirror.Relay
{
    public sealed class RelayKcpServer : KcpServer
    {
        readonly byte[] relayReceiveBuffer;
        readonly byte[] relaySendBuffer;

        Socket relaySocket;
        EndPoint relayEndPoint;
        bool relayActive;
        double lastRelayPingTime;
        double lastRelayReplyTime;
        public bool RelayReady { get; private set; }
        public event Action<string> RelayFailed;

        public Guid SessionId { get; private set; }
        public Guid Token { get; private set; }
        public int PeerId { get; private set; }

        public RelayKcpServer(
            Action<int, IPEndPoint> onConnected,
            Action<int, ArraySegment<byte>, KcpChannel> onData,
            Action<int> onDisconnected,
            Action<int, ErrorCode, string> onError,
            KcpConfig config)
            : base(onConnected, onData, onDisconnected, onError, config)
        {
            relayReceiveBuffer = new byte[config.Mtu + RelayProtocol.HeaderSize];
            relaySendBuffer = new byte[config.Mtu + RelayProtocol.HeaderSize];
        }

        public override bool IsActive() => relayActive;

        public void Start(string relayAddress, ushort relayPort, Guid sessionId, Guid token, int peerId = 1)
        {
            if (relayActive)
                return;

            if (!Common.ResolveHostname(relayAddress, out IPAddress[] addresses))
            {
                OnError(0, ErrorCode.DnsResolve, $"Failed to resolve relay host: {relayAddress}");
                return;
            }

            SessionId = sessionId;
            Token = token;
            PeerId = peerId;
            relayEndPoint = new IPEndPoint(addresses[0], relayPort);
            relaySocket = new Socket(relayEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false
            };
            Common.ConfigureSocketBuffers(relaySocket, config.RecvBufferSize, config.SendBufferSize);
            relaySocket.Connect(relayEndPoint);
            relayActive = true;
            RelayReady = false;
            lastRelayPingTime = double.NegativeInfinity;
            lastRelayReplyTime = NetworkTime.localTime;
        }

        public override void Stop()
        {
            relayActive = false;
            RelayReady = false;
            relaySocket?.Close();
            relaySocket = null;
            relayEndPoint = null;
            base.Stop();
            SessionId = default;
            Token = default;
            PeerId = 0;
        }

        protected override bool RawReceiveFrom(out ArraySegment<byte> segment, out int connectionId)
        {
            segment = default;
            connectionId = 0;
            if (!relayActive || relaySocket == null)
                return false;

            try
            {
                while (relaySocket.ReceiveNonBlocking(relayReceiveBuffer, out ArraySegment<byte> content))
                {
                    if (!RelayProtocol.TryRead(content.Array, content.Count, out RelayPacket packet))
                        continue;

                    if (packet.sessionId != SessionId || packet.token != Token || packet.targetPeerId != PeerId)
                        continue;

                    if (packet.messageType == RelayMessageType.Ping)
                    {
                        if (packet.peerId == 0)
                        {
                            RelayReady = true;
                            lastRelayReplyTime = NetworkTime.localTime;
                        }
                        continue;
                    }

                    if (packet.messageType == RelayMessageType.PeerLeft ||
                        packet.messageType == RelayMessageType.Leave)
                    {
                        if (packet.peerId > 1)
                            Disconnect(packet.peerId);
                        continue;
                    }

                    if (packet.messageType != RelayMessageType.Data || packet.peerId <= 1)
                        continue;

                    connectionId = packet.peerId;
                    segment = packet.payload;
                    return true;
                }
            }
            catch (SocketException)
            {
                // KCP will detect the timeout and raise the transport error.
            }

            return false;
        }

        protected override void RawSend(int connectionId, ArraySegment<byte> data)
        {
            if (!relayActive || relaySocket == null)
                return;

            int count = RelayProtocol.Write(
                relaySendBuffer,
                RelayMessageType.Data,
                SessionId,
                Token,
                PeerId,
                connectionId,
                data);

            try
            {
                relaySocket.SendNonBlocking(new ArraySegment<byte>(relaySendBuffer, 0, count));
            }
            catch (SocketException)
            {
                // KCP will report the timeout if the relay is unavailable.
            }
        }

        void SendRelayPing()
        {
            if (!relayActive || relaySocket == null)
                return;

            int count = RelayProtocol.Write(
                relaySendBuffer,
                RelayMessageType.Ping,
                SessionId,
                Token,
                PeerId,
                0,
                default);

            try
            {
                relaySocket.SendNonBlocking(new ArraySegment<byte>(relaySendBuffer, 0, count));
            }
            catch (SocketException)
            {
                // KCP will report the timeout if the relay is unavailable.
            }
        }

        public override void TickOutgoing()
        {
            base.TickOutgoing();
            if (NetworkTime.localTime >= lastRelayPingTime + RelayServerOptions.PingIntervalSeconds)
            {
                SendRelayPing();
                lastRelayPingTime = NetworkTime.localTime;
            }
            if (relayActive && NetworkTime.localTime - lastRelayReplyTime > 15)
            {
                RelayReady = false;
                // The manager owns shutdown, including a host without remote KCP peers.
                RelayFailed?.Invoke("Relay heartbeat timed out.");
                lastRelayReplyTime = NetworkTime.localTime;
            }
        }
    }
}
