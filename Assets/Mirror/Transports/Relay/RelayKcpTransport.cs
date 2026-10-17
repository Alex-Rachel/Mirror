using System;
using System.Net;
using System.Net.Sockets.Kcp;
using Mirror;
using UnityEngine;
using UnityEngine.Serialization;
using kcp2k;

namespace Mirror.Relay
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Network/Transport/Relay KCP Transport")]
    [HelpURL("https://github.com/MirrorNetworking/Mirror")]
    public sealed class RelayKcpTransport : KcpTransport
    {
        public new const string Scheme = "relaykcp";

        [Header("Relay")]
        public string relayAddress = "127.0.0.1";
        public ushort relayPort = 9000;
        public string roomId;
        public string peerToken;
        public int peerId = 1;

        public const int RelayHeaderSize = RelayProtocol.HeaderSize;
        public static int MaxPayload => RelayProtocol.MaxPayload();
        public bool RelayReady => server is RelayKcpServer host && host.IsActive()
            ? host.RelayReady
            : client is RelayKcpClient remote && remote.RelayReady;
        public event Action<string> RelayFailed;

        protected override void Awake()
        {
            if (debugLog)
                Log.Info = Debug.Log;
            else
                Log.Info = _ => {};
            Log.Warning = Debug.LogWarning;
            Log.Error = Debug.LogError;

            config = new KcpConfig(
                DualMode,
                RecvBufferSize,
                SendBufferSize,
                MaxPayload,
                NoDelay,
                Interval,
                FastResend,
                false,
                SendWindowSize,
                ReceiveWindowSize,
                Timeout,
                MaxRetransmit);

            client = new RelayKcpClient(
                () => OnClientConnected.Invoke(),
                (message, channel) => OnClientDataReceived.Invoke(message, FromKcpChannel(channel)),
                () => OnClientDisconnected?.Invoke(),
                (error, reason) => OnClientError?.Invoke(ToTransportError(error), reason),
                config);

            server = new RelayKcpServer(
                (connectionId, endPoint) => OnServerConnectedWithAddress.Invoke(connectionId, $"peer:{connectionId}"),
                (connectionId, message, channel) => OnServerDataReceived.Invoke(connectionId, message, FromKcpChannel(channel)),
                connectionId => OnServerDisconnected.Invoke(connectionId),
                (connectionId, error, reason) => OnServerError.Invoke(connectionId, ToTransportError(error), reason),
                config);
            ((RelayKcpServer)server).RelayFailed += reason => RelayFailed?.Invoke(reason);

            ReliableMaxMessageSize = KcpPeer.ReliableMaxMessageSize(MaxPayload, ReceiveWindowSize);
            UnreliableMaxMessageSize = KcpPeer.UnreliableMaxMessageSize(MaxPayload);
        }

        protected override void OnValidate()
        {
            ReliableMaxMessageSize = KcpPeer.ReliableMaxMessageSize(MaxPayload, ReceiveWindowSize);
            UnreliableMaxMessageSize = KcpPeer.UnreliableMaxMessageSize(MaxPayload);
        }

        public override bool Available() =>
#if UNITY_WEBGL
            false;
#else
            true;
#endif

        public void ApplyCredentials(RelayCredentials credentials)
        {
            relayAddress = credentials.relayAddress;
            relayPort = credentials.relayPort;
            roomId = credentials.sessionId.ToString("D");
            peerToken = credentials.token.ToString("D");
            peerId = credentials.peerId;
        }

        public override bool ClientConnected() => client.connected;

        public override void ClientConnect(string address)
        {
            if (!RelayConnectionInfo.TryParse(address, relayAddress, relayPort, out RelayConnectionInfo info))
            {
                if (!TryGetConfiguredInfo(out info))
                    throw new ArgumentException("Relay client address must be relaykcp://host:port?room=<guid>&token=<guid>.", nameof(address));
            }
            ConfigureClient(info);
            ((RelayKcpClient)client).Connect(info.relayAddress, info.relayPort, info.sessionId, info.token, info.peerId);
        }

        public override void ClientConnect(Uri uri)
        {
            if (!RelayConnectionInfo.TryParse(uri, relayAddress, relayPort, out RelayConnectionInfo info))
                throw new ArgumentException("Invalid relay URL or credentials.", nameof(uri));

            ConfigureClient(info);
            ((RelayKcpClient)client).Connect(info.relayAddress, info.relayPort, info.sessionId, info.token, info.peerId);
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId)
        {
            client.Send(segment, ToKcpChannel(channelId));
            OnClientDataSent?.Invoke(segment, channelId);
        }

        public override void ClientDisconnect() => client.Disconnect();
        public override void ClientEarlyUpdate()
        {
            if (enabled)
                client.TickIncoming();
        }
        public override void ClientLateUpdate() => client.TickOutgoing();

        public override Uri ServerUri()
        {
            if (Guid.TryParse(roomId, out Guid sessionId))
            {
                UriBuilder builder = new UriBuilder(Scheme, relayAddress, relayPort);
                builder.Query = $"room={sessionId:D}";
                return builder.Uri;
            }
            return new UriBuilder(Scheme, relayAddress, relayPort).Uri;
        }

        public override bool ServerActive() => server.IsActive();

        public override void ServerStart()
        {
            if (!TryGetConfiguredInfo(out RelayConnectionInfo info) ||
                info.token == default ||
                info.sessionId == default)
                throw new InvalidOperationException("RelayKcpTransport requires roomId and peerToken before StartHost.");

            ((RelayKcpServer)server).Start(
                info.relayAddress,
                info.relayPort,
                info.sessionId,
                info.token,
                info.peerId <= 0 ? 1 : info.peerId);
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId)
        {
            server.Send(connectionId, segment, ToKcpChannel(channelId));
            OnServerDataSent?.Invoke(connectionId, segment, channelId);
        }

        public override void ServerDisconnect(int connectionId) => server.Disconnect(connectionId);

        public override string ServerGetClientAddress(int connectionId) => $"peer:{connectionId}";

        public override void ServerStop() => server.Stop();
        public override void ServerEarlyUpdate()
        {
            if (enabled)
                server.TickIncoming();
        }
        public override void ServerLateUpdate() => server.TickOutgoing();

        public override void Shutdown()
        {
            client?.Disconnect();
            server?.Stop();
        }

        public override int GetMaxPacketSize(int channelId = Channels.Reliable)
        {
            switch (channelId)
            {
                case Channels.Unreliable:
                    return KcpPeer.UnreliableMaxMessageSize(config.Mtu);
                default:
                    return KcpPeer.ReliableMaxMessageSize(config.Mtu, ReceiveWindowSize);
            }
        }

        public override int GetBatchThreshold(int channelId) =>
            KcpPeer.UnreliableMaxMessageSize(config.Mtu);

        public override string ToString() => $"Relay KCP [{relayAddress}:{relayPort}]";

        void ConfigureClient(RelayConnectionInfo info)
        {
            relayAddress = info.relayAddress;
            relayPort = info.relayPort;
            roomId = info.sessionId.ToString("D");
            peerToken = info.token.ToString("D");
            peerId = info.peerId;
            ((RelayKcpClient)client).Configure(info.sessionId, info.token, info.peerId);
        }

        bool TryGetConfiguredInfo(out RelayConnectionInfo info)
        {
            info = default;
            if (!Guid.TryParse(roomId, out Guid sessionId) ||
                !Guid.TryParse(peerToken, out Guid token))
                return false;

            info = new RelayConnectionInfo
            {
                relayAddress = relayAddress,
                relayPort = relayPort,
                sessionId = sessionId,
                token = token,
                peerId = peerId
            };
            return true;
        }
    }
}
