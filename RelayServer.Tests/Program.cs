using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mirror.Relay;
using Mirror.Relay.Server;

static RelayPacket Receive(Socket socket)
{
    byte[] buffer = new byte[RelayProtocol.DefaultMaxPacketSize];
    int count = socket.Receive(buffer);
    if (!RelayProtocol.TryRead(buffer, count, out RelayPacket packet))
        throw new InvalidOperationException("Received invalid relay packet.");
    return packet;
}

static void Send(Socket socket, RelayMessageType type, Guid sessionId, Guid token, int peerId, int targetPeerId, ArraySegment<byte> payload = default)
{
    byte[] buffer = new byte[RelayProtocol.HeaderSize + payload.Count];
    int count = RelayProtocol.Write(buffer, type, sessionId, token, peerId, targetPeerId, payload);
    socket.Send(buffer, count, SocketFlags.None);
}

int port = 19000 + Random.Shared.Next(1000);
using RelayUdpServer server = new RelayUdpServer(port, peerTimeout: TimeSpan.FromSeconds(5));
server.Start();

using Socket hostControl = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
using Socket clientControl = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
using Socket hostData = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
using Socket clientData = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
using Socket secondClientControl = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
using Socket secondClientData = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
foreach (Socket socket in new[] { hostControl, clientControl, hostData, clientData, secondClientControl, secondClientData })
{
    socket.Connect(IPAddress.Loopback, port);
    socket.ReceiveTimeout = 1000;
}

Send(hostControl, RelayMessageType.CreateRoom, default, default, 0, 0);
RelayPacket created = Receive(hostControl);
if (created.messageType != RelayMessageType.RoomCreated || created.peerId != 1)
    throw new Exception("Room creation failed.");

Send(clientControl, RelayMessageType.JoinRoom, created.sessionId, created.token, 0, 0);
RelayPacket joined = Receive(clientControl);
if (joined.messageType != RelayMessageType.JoinedRoom || joined.peerId <= 1)
    throw new Exception("Room join failed.");

byte[] hostPayload = { 1, 2, 3, 4 };
Send(clientData, RelayMessageType.Ping, joined.sessionId, joined.token, joined.peerId, 1);
Assert(Receive(clientData).messageType == RelayMessageType.Ping, "Authenticated ping was not acknowledged.");
Send(hostData, RelayMessageType.Data, created.sessionId, created.token, 1, joined.peerId, hostPayload);
RelayPacket toClient = Receive(clientData);
if (toClient.messageType != RelayMessageType.Data ||
    toClient.peerId != 1 ||
    toClient.targetPeerId != joined.peerId ||
    !toClient.payload.AsSpan().SequenceEqual(hostPayload))
    throw new Exception("Host-to-client forwarding failed.");

byte[] clientPayload = { 9, 8, 7 };
Send(clientData, RelayMessageType.Data, joined.sessionId, joined.token, joined.peerId, 1, clientPayload);
RelayPacket toHost = Receive(hostData);
if (toHost.messageType != RelayMessageType.Data ||
    toHost.peerId != joined.peerId ||
    !toHost.payload.AsSpan().SequenceEqual(clientPayload))
    throw new Exception("Client-to-host forwarding failed.");

Send(clientData, RelayMessageType.Data, joined.sessionId, Guid.NewGuid(), joined.peerId, 1, clientPayload);
try
{
    Receive(hostData);
    throw new Exception("Invalid token was forwarded.");
}
catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
{
}

Send(clientData, RelayMessageType.Leave, joined.sessionId, joined.token, joined.peerId, 0);
RelayPacket peerLeft = Receive(hostData);
if (peerLeft.messageType != RelayMessageType.PeerLeft || peerLeft.peerId != joined.peerId)
    throw new Exception("Peer leave notification failed.");

Send(secondClientControl, RelayMessageType.JoinRoom, created.sessionId, created.token, 0, 0);
RelayPacket secondJoined = Receive(secondClientControl);
if (secondJoined.messageType != RelayMessageType.JoinedRoom || secondJoined.peerId <= 1)
    throw new Exception("Second room join failed.");
Send(secondClientData, RelayMessageType.Ping, secondJoined.sessionId, secondJoined.token, secondJoined.peerId, 1);
Assert(Receive(secondClientData).messageType == RelayMessageType.Ping, "Second peer ping failed.");
Send(hostData, RelayMessageType.Leave, created.sessionId, created.token, 1, 0);
RelayPacket hostLeft = Receive(secondClientData);
if (hostLeft.messageType != RelayMessageType.PeerLeft || hostLeft.peerId != 1)
    throw new Exception("Host leave notification failed.");

Console.WriteLine("Relay server smoke test passed.");
await RunHttpTests();
Console.WriteLine("Relay lobby HTTP test passed.");
RunBoundaryTests();
Console.WriteLine("Relay MTU, topology, revocation, isolation and timeout tests passed.");

static void RunBoundaryTests()
{
    int port = GetFreePort();
    RelayRoomRegistry registry = new("127.0.0.1", (ushort)port);
    using RelayUdpServer server = new(port, registry: registry);
    server.Start();
    var created = registry.CreateRoom(new CreateRoomRequest { name = "Boundaries", displayName = "Host", capacity = 4 });
    var host = created.Credentials!;
    var first = registry.JoinRoom(new JoinRoomRequest { roomId = host.RoomId, displayName = "First" }).Credentials!;
    var second = registry.JoinRoom(new JoinRoomRequest { roomId = host.RoomId, displayName = "Second" }).Credentials!;
    using Socket h = DataSocket(port);
    using Socket a = DataSocket(port);
    using Socket b = DataSocket(port);
    Ping(h, host);
    Ping(a, first);
    Ping(b, second);
    Assert(RelayProtocol.MaxPayload() == 1152 && RelayProtocol.DefaultMaxPacketSize == 1200, "MTU budget mismatch.");
    byte[] limit = Enumerable.Range(0, 1152).Select(i => (byte)i).ToArray();
    Send(a, RelayMessageType.Data, host.RoomId, first.RelayToken, first.PeerId, 1, limit);
    Assert(Receive(h).payload.AsSpan().SequenceEqual(limit), "MTU boundary payload was lost.");
    bool rejected = false;
    try { Send(a, RelayMessageType.Data, host.RoomId, first.RelayToken, first.PeerId, 1, new byte[1153]); }
    catch (ArgumentException) { rejected = true; }
    Assert(rejected, "Oversize write was accepted.");
    byte[] oversized = new byte[1201];
    RelayProtocol.Write(oversized, RelayMessageType.Data, host.RoomId, first.RelayToken, first.PeerId, 1, limit);
    a.Send(oversized);
    Silence(h, "Oversize datagram was forwarded.");
    Send(a, RelayMessageType.Data, host.RoomId, first.RelayToken, first.PeerId, second.PeerId, limit);
    Silence(b, "Client-to-client forwarding was accepted.");
    Send(a, RelayMessageType.Data, host.RoomId, first.RelayToken, second.PeerId, 1, limit);
    Send(a, RelayMessageType.Data, Guid.NewGuid(), first.RelayToken, first.PeerId, 1, limit);
    Send(a, RelayMessageType.Data, host.RoomId, Guid.NewGuid(), first.PeerId, 1, limit);
    Send(a, RelayMessageType.Data, host.RoomId, first.RelayToken, first.PeerId, -1, limit);
    Silence(h, "Invalid room, peer, credential or target was accepted.");
    Send(a, RelayMessageType.Leave, host.RoomId, first.RelayToken, first.PeerId, 0);
    Assert(Receive(h).peerId == first.PeerId && Receive(b).peerId == first.PeerId, "Three-member leave notification mismatch.");
    Send(b, RelayMessageType.Data, host.RoomId, second.RelayToken, second.PeerId, 1, limit);
    Assert(Receive(h).messageType == RelayMessageType.Data, "Surviving member stopped forwarding after leave.");
    var rejoined = registry.JoinRoom(new JoinRoomRequest { roomId = host.RoomId, displayName = "Rejoined" }).Credentials!;
    Ping(a, rejoined);
    registry.TryGetPeer(host.RoomId, rejoined.PeerId, out EndPoint? endpoint, out Guid token, out _);
    Assert(registry.KickMember(host.RoomId, rejoined.MemberId, host.OwnerToken.ToString(), out _, out _).Success, "Kick failed.");
    server.NotifyPeerKicked(host.RoomId, rejoined.PeerId, endpoint, token);
    Assert(Receive(a).peerId == rejoined.PeerId, "Kicked member was not notified.");
    Assert(Receive(h).peerId == rejoined.PeerId && Receive(b).peerId == rejoined.PeerId, "Kick notification mismatch.");
    Send(a, RelayMessageType.Data, host.RoomId, rejoined.RelayToken, rejoined.PeerId, 1, limit);
    Silence(h, "Revoked kick credential still forwarded.");
    Send(b, RelayMessageType.Data, host.RoomId, second.RelayToken, second.PeerId, 1, limit);
    Assert(Receive(h).messageType == RelayMessageType.Data, "Surviving member stopped forwarding after kick.");
    Send(h, RelayMessageType.Leave, host.RoomId, host.RelayToken, 1, 0);
    Assert(Receive(b).peerId == 1 && registry.RoomCount == 0, "Host leave did not close room.");

    var visibility = registry.CreateRoom(new CreateRoomRequest { name = "Visibility", capacity = 3 }).Credentials!;
    var changed = registry.UpdateRoom(visibility.RoomId, visibility.OwnerToken.ToString(),
        new RoomUpdateRequest { name = "Private", capacity = 3, visibility = RelayRoomVisibility.Private, joinable = true });
    Assert(changed.Success && changed.Room!.inviteCode?.Length == 10, "Public-to-private invite was not generated.");
    Assert(registry.GetRoom(visibility.RoomId)!.inviteCode == null, "Ordinary room details leaked invitation.");
    Assert(registry.GetRoom(visibility.RoomId, true)!.inviteCode == changed.Room!.inviteCode, "Owner invitation unavailable.");
    Assert(registry.JoinRoom(new JoinRoomRequest { roomId = visibility.RoomId, inviteCode = changed.Room!.inviteCode }).Success, "Generated invitation failed.");
    Assert(registry.ListRooms(50, null, null, null).rooms.All(room => room.roomId != visibility.RoomId), "Private room listed.");

    // Expire using a future cutoff: deterministic maintenance logic, no sleeps.
    var expired = registry.ExpirePeers(TimeSpan.FromSeconds(-1));
    Assert(expired.Any(peer => peer.WasHost) && registry.RoomCount == 0, "Host expiration did not close room.");

    var timeoutHost = registry.CreateRoom(new CreateRoomRequest { name = "Member timeout", capacity = 3 }).Credentials!;
    var timeoutClient = registry.JoinRoom(new JoinRoomRequest { roomId = timeoutHost.RoomId }).Credentials!;
    var survivor = registry.JoinRoom(new JoinRoomRequest { roomId = timeoutHost.RoomId }).Credentials!;
    Thread.Sleep(1100);
    Ping(h, timeoutHost);
    Ping(b, survivor);
    expired = registry.ExpirePeers(TimeSpan.FromSeconds(1));
    Assert(expired.Count == 1 && expired[0].PeerId == timeoutClient.PeerId && !expired[0].WasHost &&
        registry.RoomCount == 1 && registry.MemberCount == 2, "Member timeout closed an active surviving room.");
    Send(b, RelayMessageType.Data, timeoutHost.RoomId, survivor.RelayToken, survivor.PeerId, 1, limit);
    Assert(Receive(h).messageType == RelayMessageType.Data, "Member timeout broke survivor forwarding.");
}

static Socket DataSocket(int port)
{
    Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Connect(IPAddress.Loopback, port);
    socket.ReceiveTimeout = 250;
    return socket;
}
static void Ping(Socket socket, RelayRoomCredentialsInternal credentials)
{
    Send(socket, RelayMessageType.Ping, credentials.RoomId, credentials.RelayToken, credentials.PeerId, 0);
    RelayPacket ack = Receive(socket);
    Assert(ack.messageType == RelayMessageType.Ping && ack.peerId == 0 &&
        ack.token == credentials.RelayToken && ack.targetPeerId == credentials.PeerId, "Ping authentication mismatch.");
}
static void Silence(Socket socket, string message)
{
    try { Receive(socket); throw new Exception(message); }
    catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut) { }
}

static async Task RunHttpTests()
{
    int udpPort = GetFreePort();
    int httpPort = GetFreePort();
    RelayRoomRegistry registry = new("127.0.0.1", checked((ushort)udpPort), maxRooms: 20, maxMembersPerRoom: 4);
    using RelayUdpServer udp = new(udpPort, registry: registry);
    using RelayHttpServer http = new(registry, udp, $"http://127.0.0.1:{httpPort}");
    udp.Start();
    http.Start();

    using HttpClient client = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{httpPort}")
    };
    await WaitForHealth(client);

    HttpResponseMessage createResponse = await client.PostAsJsonAsync(
        "/api/v1/rooms",
        new CreateRoomRequest
        {
            name = "HTTP Room",
            displayName = "Host",
            capacity = 2,
            visibility = RelayRoomVisibility.Public,
            tags = new[] { "test" }
        });
    createResponse.EnsureSuccessStatusCode();
    CreateRoomResponse created = await ReadJson<CreateRoomResponse>(createResponse);
    Assert(!string.IsNullOrWhiteSpace(created.credentials.ownerToken), "Owner token missing.");
    Assert(created.room.room.playerCount == 1, "Host was not added.");

    RoomPage page = await client.GetFromJsonAsync<RoomPage>("/api/v1/rooms?tag=test")
        ?? throw new Exception("Room list was empty.");
    Assert(page.rooms.Length == 1 && page.rooms[0].roomId == created.room.room.roomId, "Room list mismatch.");

    HttpResponseMessage joinResponse = await client.PostAsJsonAsync(
        "/api/v1/rooms:join",
        new JoinRoomRequest
        {
            roomId = created.room.room.roomId,
            displayName = "Client"
        });
    joinResponse.EnsureSuccessStatusCode();
    JoinRoomResponse joined = await ReadJson<JoinRoomResponse>(joinResponse);
    Assert(joined.room.room.playerCount == 2, "Client was not added.");

    HttpResponseMessage fullResponse = await client.PostAsJsonAsync(
        "/api/v1/rooms:join",
        new JoinRoomRequest { roomId = created.room.room.roomId, displayName = "TooMany" });
    Assert((int)fullResponse.StatusCode == 409, "Capacity limit was not enforced.");

    using (HttpRequestMessage invalidUpdate = new(HttpMethod.Patch, $"/api/v1/rooms/{created.room.room.roomId}"))
    {
        invalidUpdate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.credentials.ownerToken);
        invalidUpdate.Content = JsonContent.Create(new RoomUpdateRequest { capacity = 1 });
        HttpResponseMessage response = await client.SendAsync(invalidUpdate);
        Assert((int)response.StatusCode == 400, "Invalid capacity update was accepted.");
    }

    using (HttpRequestMessage update = new(HttpMethod.Patch, $"/api/v1/rooms/{created.room.room.roomId}"))
    {
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.credentials.ownerToken);
        update.Content = JsonContent.Create(new RoomUpdateRequest
        {
            name = "Updated HTTP Room",
            capacity = 3,
            visibility = RelayRoomVisibility.Public,
            joinable = true,
            tags = new[] { "updated" }
        });
        HttpResponseMessage response = await client.SendAsync(update);
        response.EnsureSuccessStatusCode();
        RoomDetails updated = await ReadJson<RoomDetails>(response);
        Assert(updated.room.name == "Updated HTTP Room" && updated.room.capacity == 3, "Owner room update failed.");
    }

    RoomDetails details = await GetAuthorizedRoom(client, created.room.room.roomId, joined.credentials.memberId, joined.credentials.memberToken);
    Assert(details.members.Length == 2, "Member list authorization failed.");

    using (HttpRequestMessage start = new(HttpMethod.Post, "/api/v1/rooms:start"))
    {
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.credentials.ownerToken);
        start.Content = JsonContent.Create(new RoomActionRequest { roomId = created.room.room.roomId });
        HttpResponseMessage response = await client.SendAsync(start);
        response.EnsureSuccessStatusCode();
    }

    HttpResponseMessage lateJoin = await client.PostAsJsonAsync(
        "/api/v1/rooms:join",
        new JoinRoomRequest { roomId = created.room.room.roomId, displayName = "Late" });
    Assert((int)lateJoin.StatusCode == 409, "Started room accepted a new member.");

    // A private room verifies invite-code enforcement and owner-only lifecycle actions.
    HttpResponseMessage privateCreateResponse = await client.PostAsJsonAsync(
        "/api/v1/rooms",
        new CreateRoomRequest
        {
            name = "Private Room",
            displayName = "PrivateHost",
            capacity = 3,
            visibility = RelayRoomVisibility.Private
        });
    privateCreateResponse.EnsureSuccessStatusCode();
    CreateRoomResponse privateRoom = await ReadJson<CreateRoomResponse>(privateCreateResponse);
    Assert(privateRoom.credentials.inviteCode.Length == 10, "Invite code is not fixed length.");
    RoomPage publicPage = await client.GetFromJsonAsync<RoomPage>("/api/v1/rooms")
        ?? throw new Exception("Public room list was empty.");
    Assert(publicPage.rooms.All(room => room.roomId != privateRoom.room.room.roomId), "Private room leaked into public list.");

    HttpResponseMessage badInvite = await client.PostAsJsonAsync(
        "/api/v1/rooms:join",
        new JoinRoomRequest { roomId = privateRoom.room.room.roomId, inviteCode = "WRONG", displayName = "Nope" });
    Assert((int)badInvite.StatusCode == 403, "Invalid invite was accepted.");

    HttpResponseMessage privateJoin = await client.PostAsJsonAsync(
        "/api/v1/rooms:join",
        new JoinRoomRequest
        {
            roomId = privateRoom.room.room.roomId,
            inviteCode = privateRoom.credentials.inviteCode,
            displayName = "PrivateClient"
        });
    privateJoin.EnsureSuccessStatusCode();
    JoinRoomResponse privateClient = await ReadJson<JoinRoomResponse>(privateJoin);
    Assert(privateClient.room.inviteCode == null && string.IsNullOrEmpty(privateClient.credentials.ownerToken) &&
        string.IsNullOrEmpty(privateClient.credentials.inviteCode), "Join response exposed owner invitation or credentials.");
    var ordinaryDetails = await GetAuthorizedRoom(client, privateRoom.credentials.roomId,
        privateClient.credentials.memberId, privateClient.credentials.memberToken);
    Assert(ordinaryDetails.inviteCode == null, "Member details exposed private invitation.");
    var ownerDetails = await GetAuthorizedRoom(client, privateRoom.credentials.roomId,
        privateRoom.credentials.memberId, privateRoom.credentials.ownerToken);
    Assert(ownerDetails.inviteCode == privateRoom.credentials.inviteCode, "Owner could not recover invitation.");
    string publicJson = await client.GetStringAsync("/api/v1/rooms");
    foreach (string secret in new[] { privateRoom.credentials.ownerToken, privateRoom.credentials.memberToken,
        privateRoom.credentials.relayToken, privateRoom.credentials.inviteCode, privateClient.credentials.memberToken,
        privateClient.credentials.relayToken })
        Assert(!publicJson.Contains(secret, StringComparison.Ordinal), "Public list exposed credentials.");

    using (HttpRequestMessage kick = new(
               HttpMethod.Post,
               $"/api/v1/rooms/{privateRoom.room.room.roomId}/members/{privateClient.credentials.memberId}:kick"))
    {
        kick.Headers.Authorization = new AuthenticationHeaderValue("Bearer", privateRoom.credentials.ownerToken);
        HttpResponseMessage response = await client.SendAsync(kick);
        response.EnsureSuccessStatusCode();
    }

    using (HttpRequestMessage delete = new(HttpMethod.Delete, $"/api/v1/rooms/{privateRoom.room.room.roomId}"))
    {
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", privateRoom.credentials.ownerToken);
        HttpResponseMessage response = await client.SendAsync(delete);
        Assert(response.StatusCode == HttpStatusCode.NoContent, "Owner close failed.");
    }

    udp.Stop();
    http.Stop();
}

static async Task<RoomDetails> GetAuthorizedRoom(
    HttpClient client,
    Guid roomId,
    Guid memberId,
    string memberToken)
{
    using HttpRequestMessage request = new(
        HttpMethod.Get,
        $"/api/v1/rooms/{roomId:D}?memberId={memberId:D}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
    HttpResponseMessage response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await ReadJson<RoomDetails>(response);
}

static async Task<T> ReadJson<T>(HttpResponseMessage response)
{
    string body = await response.Content.ReadAsStringAsync();
    return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new Exception($"Could not deserialize {typeof(T).Name}.");
}

static async Task WaitForHealth(HttpClient client)
{
    for (int attempt = 0; attempt < 50; attempt++)
    {
        try
        {
            HttpResponseMessage response = await client.GetAsync("/health");
            if (response.IsSuccessStatusCode)
                return;
        }
        catch (HttpRequestException)
        {
        }
        await Task.Delay(50);
    }
    throw new TimeoutException("Relay HTTP server did not become ready.");
}

static int GetFreePort()
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
