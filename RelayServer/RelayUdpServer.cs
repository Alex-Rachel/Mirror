using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Mirror.Relay;

namespace Mirror.Relay.Server;

public sealed class RelayUdpServer : IDisposable
{
    readonly int port;
    readonly TimeSpan peerTimeout;
    readonly RelayRoomRegistry registry;
    readonly int maxPacketsPerSecondPerEndpoint;
    readonly object socketGate = new();
    readonly ConcurrentDictionary<string, RateWindow> rateWindows = new(StringComparer.Ordinal);

    Socket? socket;
    CancellationTokenSource? cancellation;
    Task? loopTask;
    Task? maintenanceTask;

    public RelayUdpServer(
        int port,
        int maxRooms = 10000,
        int maxPeersPerRoom = 32,
        TimeSpan? peerTimeout = null,
        RelayRoomRegistry? registry = null,
        int maxPacketsPerSecondPerEndpoint = 1000)
    {
        this.port = port;
        this.peerTimeout = peerTimeout ?? TimeSpan.FromSeconds(15);
        this.registry = registry ?? new RelayRoomRegistry("127.0.0.1", checked((ushort)port), maxRooms, maxPeersPerRoom);
        this.maxPacketsPerSecondPerEndpoint = Math.Max(10, maxPacketsPerSecondPerEndpoint);
    }

    public int Port => port;
    public int RoomCount => registry.RoomCount;
    public int MemberCount => registry.MemberCount;
    public RelayRoomRegistry Registry => registry;

    public void Start()
    {
        lock (socketGate)
        {
            if (loopTask != null)
                return;

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = true,
                ReceiveTimeout = 500
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            cancellation = new CancellationTokenSource();
            loopTask = Task.Run(() => Run(cancellation.Token));
            maintenanceTask = Task.Run(() => Maintain(cancellation.Token));
        }
    }

    public void Stop()
    {
        Task? receiveTask;
        Task? cleanupTask;
        lock (socketGate)
        {
            cancellation?.Cancel();
            socket?.Close();
            receiveTask = loopTask;
            cleanupTask = maintenanceTask;
            loopTask = null;
            maintenanceTask = null;
            cancellation?.Dispose();
            cancellation = null;
            socket = null;
        }

        try { receiveTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { cleanupTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        rateWindows.Clear();
    }

    public void Dispose() => Stop();

    public bool DisconnectMember(Guid roomId, int peerId)
    {
        if (!registry.TryGetPeer(roomId, peerId, out EndPoint? endpoint, out Guid relayToken, out _))
            return false;

        bool removed = registry.RemovePeer(
            roomId,
            peerId,
            out bool wasHost,
            out List<int> remaining,
            out List<RelayPeerEndpoint> remainingPeers);
        if (!removed)
            return false;

        if (endpoint != null)
            Send(endpoint, RelayMessageType.PeerLeft, roomId, relayToken, peerId, peerId, default);
        if (wasHost)
            NotifyPeerLeft(remainingPeers, peerId, roomId);
        else
            NotifyPeerLeft(roomId, peerId, remaining);
        return true;
    }

    public void NotifyPeerKicked(Guid roomId, int peerId, EndPoint? endpoint, Guid relayToken)
    {
        RoomDetails? room = registry.GetRoom(roomId);
        if (room == null)
            return;

        if (endpoint != null)
            Send(endpoint, RelayMessageType.PeerLeft, roomId, relayToken, peerId, peerId, default);
        NotifyPeerLeft(roomId, peerId, room.members.Select(member => member.peerId).Where(id => id != peerId));
    }

    public bool LeaveMember(Guid roomId, Guid memberId, string memberToken)
    {
        RoomOperationResult result = registry.LeaveRoom(
            roomId,
            memberId,
            memberToken,
            out int peerId,
            out List<int> remainingPeerIds,
            out List<RelayPeerEndpoint> remainingPeers);
        if (!result.Success)
            return false;

        NotifyPeerLeft(remainingPeers, peerId, roomId);
        return true;
    }

    public bool CloseRoom(Guid roomId)
    {
        RoomDetails? room = registry.GetRoom(roomId);
        if (room == null)
            return false;

        foreach (RoomMember member in room.members.Where(member => member.peerId > 1))
        {
            if (registry.TryGetPeer(roomId, member.peerId, out EndPoint? endpoint, out Guid relayToken, out _) &&
                endpoint != null)
                Send(endpoint, RelayMessageType.PeerLeft, roomId, relayToken, 1, member.peerId, default);
        }

        List<int> peers = room.members.Select(member => member.peerId).Where(peerId => peerId > 0).ToList();
        registry.RemovePeer(roomId, 1, out _, out _);
        foreach (int peerId in peers.Where(peerId => peerId > 1))
            registry.RemovePeer(roomId, peerId, out _, out _);
        return true;
    }

    void Run(CancellationToken cancellationToken)
    {
        byte[] packetBuffer = new byte[65535];
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? currentSocket;
            lock (socketGate)
                currentSocket = socket;
            if (currentSocket == null)
                return;

            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                int count = currentSocket.ReceiveFrom(packetBuffer, ref remote);
                if (!Allow(remote))
                    continue;
                Handle(packetBuffer, count, remote);
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode is SocketError.TimedOut or SocketError.Interrupted or SocketError.OperationAborted)
            {
                // The maintenance task owns expiry. Receive timeouts only keep the loop responsive.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Relay packet error: {exception.Message}");
            }
        }
    }

    async Task Maintain(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (ExpiredPeer expired in registry.ExpirePeers(peerTimeout))
            {
                if (expired.WasHost)
                {
                    foreach (RelayPeerEndpoint peer in expired.RemainingPeers)
                    {
                        if (peer.Endpoint != null)
                            Send(peer.Endpoint, RelayMessageType.PeerLeft, expired.RoomId, peer.RelayToken, 1, peer.PeerId, default);
                    }
                }
                else
                {
                    NotifyPeerLeft(expired.RoomId, expired.PeerId, expired.RemainingPeers.Select(peer => peer.PeerId));
                }
            }
        }
    }

    void Handle(byte[] buffer, int count, EndPoint endpoint)
    {
        if (count > RelayProtocol.DefaultMaxPacketSize ||
            !RelayProtocol.TryRead(buffer, count, out RelayPacket packet))
            return;

        switch (packet.messageType)
        {
            case RelayMessageType.CreateRoom:
                CreateRoom(endpoint);
                break;
            case RelayMessageType.JoinRoom:
                JoinRoom(packet, endpoint);
                break;
            case RelayMessageType.Data:
            case RelayMessageType.Ping:
            case RelayMessageType.Leave:
                HandlePeerPacket(packet, endpoint);
                break;
        }
    }

    void CreateRoom(EndPoint endpoint)
    {
        RoomOperationResult result = registry.CreateLegacyRoom(endpoint);
        if (!result.Success || result.Credentials == null)
        {
            SendReject(endpoint, default, result.Message);
            return;
        }

        Send(endpoint, RelayMessageType.RoomCreated, result.Credentials.RoomId, result.Credentials.RelayToken, 1, 0, default);
    }

    void JoinRoom(RelayPacket packet, EndPoint endpoint)
    {
        RoomOperationResult result = registry.JoinLegacyRoom(packet.sessionId, packet.token, endpoint);
        if (!result.Success || result.Credentials == null)
        {
            SendReject(endpoint, packet.sessionId, result.Message);
            return;
        }

        Send(
            endpoint,
            RelayMessageType.JoinedRoom,
            result.Credentials.RoomId,
            result.Credentials.RelayToken,
            result.Credentials.PeerId,
            0,
            default);
    }

    void HandlePeerPacket(RelayPacket packet, EndPoint endpoint)
    {
        if (!registry.TryAuthenticateRelay(
                packet.sessionId,
                packet.peerId,
                packet.token,
                endpoint,
                out _,
                out bool isHost))
            return;

        if (packet.messageType == RelayMessageType.Ping)
        {
            Send(endpoint, RelayMessageType.Ping, packet.sessionId, packet.token, 0, packet.peerId, default);
            return;
        }

        if (packet.messageType == RelayMessageType.Leave)
        {
            if (registry.RemovePeer(
                    packet.sessionId,
                    packet.peerId,
                    out bool wasHost,
                    out List<int> remaining,
                    out List<RelayPeerEndpoint> remainingPeers))
            {
                if (wasHost)
                    NotifyPeerLeft(remainingPeers, packet.peerId, packet.sessionId);
                else
                    NotifyPeerLeft(packet.sessionId, packet.peerId, remaining);
            }
            return;
        }

        if ((isHost && packet.targetPeerId <= 1) || (!isHost && packet.targetPeerId != 1))
            return;
        if (packet.payload.Count > RelayProtocol.MaxPayload())
            return;
        if (!registry.TryGetPeer(packet.sessionId, packet.targetPeerId, out EndPoint? targetEndpoint, out Guid targetToken, out _) ||
            targetEndpoint == null)
            return;

        Send(
            targetEndpoint,
            RelayMessageType.Data,
            packet.sessionId,
            targetToken,
            packet.peerId,
            packet.targetPeerId,
            packet.payload);
    }

    void NotifyPeerLeft(Guid roomId, int peerId, IEnumerable<int> peerIds)
    {
        foreach (int targetPeerId in peerIds)
        {
            if (registry.TryGetPeer(roomId, targetPeerId, out EndPoint? endpoint, out Guid token, out _) &&
                endpoint != null)
                Send(endpoint, RelayMessageType.PeerLeft, roomId, token, peerId, targetPeerId, default);
        }
    }

    void NotifyPeerLeft(IEnumerable<RelayPeerEndpoint> peers, int peerId, Guid roomId)
    {
        foreach (RelayPeerEndpoint peer in peers)
        {
            if (peer.Endpoint != null)
                Send(peer.Endpoint, RelayMessageType.PeerLeft, roomId, peer.RelayToken, peerId, peer.PeerId, default);
        }
    }

    bool Allow(EndPoint endpoint)
    {
        string key = endpoint.ToString() ?? string.Empty;
        RateWindow window = rateWindows.GetOrAdd(key, _ => new RateWindow());
        lock (window)
        {
            DateTime now = DateTime.UtcNow;
            if (now - window.StartUtc >= TimeSpan.FromSeconds(1))
            {
                window.StartUtc = now;
                window.Count = 0;
            }

            window.Count++;
            return window.Count <= maxPacketsPerSecondPerEndpoint;
        }
    }

    void SendReject(EndPoint endpoint, Guid sessionId, string reason) =>
        Send(endpoint, RelayMessageType.Reject, sessionId, default, 0, 0, RelayProtocol.Utf8(reason));

    void Send(
        EndPoint endpoint,
        RelayMessageType type,
        Guid sessionId,
        Guid token,
        int peerId,
        int targetPeerId,
        ArraySegment<byte> payload)
    {
        Socket? currentSocket;
        lock (socketGate)
            currentSocket = socket;
        if (currentSocket == null)
            return;

        byte[] buffer = new byte[RelayProtocol.HeaderSize + payload.Count];
        int count = RelayProtocol.Write(buffer, type, sessionId, token, peerId, targetPeerId, payload);
        try
        {
            currentSocket.SendTo(buffer, 0, count, SocketFlags.None, endpoint);
        }
        catch (SocketException)
        {
            // UDP delivery is best effort. KCP performs recovery above this layer.
        }
    }

    sealed class RateWindow
    {
        public DateTime StartUtc = DateTime.UtcNow;
        public int Count;
    }
}
