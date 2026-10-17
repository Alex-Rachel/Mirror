using System;
using System.Net;
using System.Net.Sockets;
using Mirror;
using kcp2k;

namespace Mirror.Relay
{
    public sealed class RelayKcpClient : KcpClient
    {
        readonly byte[] relayReceiveBuffer;
        readonly byte[] relaySendBuffer;
        double lastRelayPingTime;
        double lastRelayReplyTime;
        public bool RelayReady { get; private set; }

        public Guid SessionId { get; private set; }
        public Guid Token { get; private set; }
        public int PeerId { get; private set; }

        public RelayKcpClient(
            Action onConnected,
            Action<ArraySegment<byte>, KcpChannel> onData,
            Action onDisconnected,
            Action<ErrorCode, string> onError,
            KcpConfig config)
            : base(onConnected, onData, onDisconnected, onError, config)
        {
            relayReceiveBuffer = new byte[config.Mtu + RelayProtocol.HeaderSize];
            relaySendBuffer = new byte[config.Mtu + RelayProtocol.HeaderSize];
        }

        public void Configure(Guid sessionId, Guid token, int peerId)
        {
            SessionId = sessionId;
            Token = token;
            PeerId = peerId;
        }

        public void Connect(string relayAddress, ushort relayPort, Guid sessionId, Guid token, int peerId)
        {
            Configure(sessionId, token, peerId);
            RelayReady = false;
            lastRelayReplyTime = NetworkTime.localTime;
            lastRelayPingTime = double.NegativeInfinity;
            base.Connect(relayAddress, relayPort);
        }

        protected override bool RawReceive(out ArraySegment<byte> segment)
        {
            segment = default;
            if (socket == null)
                return false;

            try
            {
                while (socket.ReceiveNonBlocking(relayReceiveBuffer, out ArraySegment<byte> content))
                {
                    if (!RelayProtocol.TryRead(content.Array, content.Count, out RelayPacket packet))
                        continue;

                    if (packet.sessionId != SessionId || packet.token != Token)
                        continue;

                    if (packet.targetPeerId != PeerId)
                        continue;

                    switch (packet.messageType)
                    {
                        case RelayMessageType.Data:
                            if (packet.peerId != 1)
                                continue;
                            segment = packet.payload;
                            return true;

                        case RelayMessageType.Ping:
                            if (packet.peerId != 0)
                                continue;
                            RelayReady = true;
                            lastRelayReplyTime = NetworkTime.localTime;
                            continue;

                        case RelayMessageType.PeerLeft:
                        case RelayMessageType.Leave:
                            if (packet.peerId == 1 || packet.peerId == PeerId)
                            {
                                Disconnect();
                                return false;
                            }
                            continue;

                        case RelayMessageType.Reject:
                            string reason = "Relay rejected the connection.";
                            RelayProtocol.TryReadUtf8(packet.payload, out reason);
                            OnErrorCallback(ErrorCode.Unexpected, reason);
                            Disconnect();
                            return false;
                    }
                }
            }
            catch (SocketException)
            {
                Disconnect();
            }

            return false;
        }

        protected override void RawSend(ArraySegment<byte> data)
        {
            if (socket == null)
                return;

            int count = RelayProtocol.Write(
                relaySendBuffer,
                RelayMessageType.Data,
                SessionId,
                Token,
                PeerId,
                1,
                data);

            try
            {
                socket.SendNonBlocking(new ArraySegment<byte>(relaySendBuffer, 0, count));
            }
            catch (SocketException)
            {
                // KCP will report the timeout if the relay is unavailable.
            }
        }

        public void SendRelayPing()
        {
            if (socket == null)
                return;

            int count = RelayProtocol.Write(
                relaySendBuffer,
                RelayMessageType.Ping,
                SessionId,
                Token,
                PeerId,
                1,
                default);

            try
            {
                socket.SendNonBlocking(new ArraySegment<byte>(relaySendBuffer, 0, count));
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
            if (socket != null && NetworkTime.localTime - lastRelayReplyTime > 15)
            {
                RelayReady = false;
                OnErrorCallback(ErrorCode.Timeout, "Relay heartbeat timed out.");
                Disconnect();
            }
        }

        public override void Disconnect()
        {
            RelayReady = false;
            base.Disconnect();
            SessionId = default;
            Token = default;
            PeerId = 0;
        }
    }
}
