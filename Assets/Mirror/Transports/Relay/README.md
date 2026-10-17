# Relay KCP Transport

`RelayKcpTransport` keeps Mirror's normal host mode behavior:

- the local host player uses Mirror's `LocalConnection`;
- remote players connect to the same authoritative Mirror server through UDP;
- the relay only authenticates and forwards KCP datagrams.

## Unity setup

1. Add `RelayKcpTransport` to the object that owns `RelayNetworkManager`.
2. Assign it to `NetworkManager.transport`.
3. Configure `RelayNetworkManager.lobbyApiUrl` (default `http://localhost:9001`).
   HTTP allocations supply the authoritative UDP address/port. Configure the
   manager's relay address/port only when using the legacy UDP control path.
4. Create or join through the HTTP lobby API:
   `StartLobbyHostAsync`, `StartLobbyClientAsync`, `ListRoomsAsync`,
   `RefreshRoomAsync`, `UpdateRoomAsync`, `StartRoomAsync`,
   `LeaveRoomAsync`, `CloseRoomAsync`, and `KickMemberAsync`.
5. The manager applies the returned relay credentials before calling Mirror's
   `StartHost()` or `StartClient()`. The local host player still uses
   Mirror's `LocalConnection`.

The client connects with a URI shaped like:

```text
relaykcp://relay.example.com:9000?room=<room-guid>&token=<peer-token>&peer=<peer-id>
```

The original UDP `RelaySession.Create/Join` methods remain for compatibility.
Their control sockets may differ from the KCP data socket.
All asynchronous API/manager methods accept an optional `CancellationToken`;
HTTP and startup default to ten-second timeouts. Session generations prevent
late responses from restoring a room after exit. `RelayLobbyClient` delegates
to the manager's single session state.

The relay datagram budget is 1200 bytes, including a 48-byte header; KCP has
1152 bytes. Clients may send only to the host. `RelayReady` means an authenticated
Ping acknowledgment has arrived, independently of `NetworkClient.isConnected`.
Ping runs every 0.5 seconds and relay liveness expires after 15 seconds.
Leave/kick of another member must not disconnect surviving clients.

For the complete editable scene and standalone acceptance scripts, see
`Assets/Mirror/Examples/RelayLobby/README.md`.

New applications should use the HTTP control plane and keep it behind HTTPS.
The relay token is authentication, not payload encryption. Add an encryption
layer when transport confidentiality is required.
