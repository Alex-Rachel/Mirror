# Mirror Relay Server

`Mirror.RelayServer` is a standalone .NET 8 process that combines:

- a UDP data plane for `RelayKcpTransport`;
- an ASP.NET Core Minimal API for room/lobby control.

It does not load Unity, Mirror gameplay assemblies, or a game scene. The server
stores room state in memory; restarting the process invalidates all rooms and
credentials.

## Run

```powershell
dotnet run --project .\RelayServer\RelayServer.csproj -- `
  --udp 9000 `
  --http 9001 `
  --relay-address 127.0.0.1
```

Defaults:

- UDP relay: `0.0.0.0:9000`
- HTTP lobby: `http://0.0.0.0:9001`
- advertised relay address: `127.0.0.1`

Use a public DNS name or IP for `--relay-address` when clients are outside the
host machine. Put the HTTP endpoint behind HTTPS and an authentication layer
before exposing it to the Internet.

## Lobby API

All JSON uses camelCase. Owner operations use
`Authorization: Bearer <ownerToken>`. Member detail operations use a member
token in the same header plus the `memberId` query parameter where shown.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Process, room, and member counts |
| `GET` | `/api/v1/rooms?pageSize=50&cursor=0&tag=demo&state=open` | Public, joinable room list |
| `POST` | `/api/v1/rooms` | Create a public or private room |
| `GET` | `/api/v1/rooms/{roomId}` | Safe room details; full members require auth |
| `PATCH` | `/api/v1/rooms/{roomId}` | Owner changes name, capacity, visibility, joinability, tags |
| `DELETE` | `/api/v1/rooms/{roomId}` | Owner closes the room and disconnects members |
| `GET` | `/api/v1/rooms/{roomId}/members?memberId=...` | Authorized member list |
| `POST` | `/api/v1/rooms:join` | Join by room ID, or room ID plus private invite code |
| `POST` | `/api/v1/rooms:leave` | Leave with `roomId`, `memberId`, and `memberToken` |
| `POST` | `/api/v1/rooms:start` | Owner starts the room and blocks new joins |
| `POST` | `/api/v1/rooms/{roomId}/members/{memberId}:kick` | Owner kicks a member |

`GET /rooms` never returns private rooms, tokens, endpoints, or full member
records. `POST /rooms` and `POST /rooms:join` return transport credentials plus
a room snapshot. Private rooms return a ten-character hexadecimal invite code.

Only owner-authorized room details include the optional `inviteCode`.
Changing public visibility to private generates a fresh invitation. Member and
public snapshots omit invitations and all administrative/relay credentials.

The registry keeps separate owner, member, and relay credentials. Token
comparisons are constant-time and the registry stores SHA-256 token hashes for
authorization. Relay credentials are short-lived in practice because members
expire after the UDP peer timeout (15 seconds by default), and a kick or room
close immediately removes the member from the registry.

## UDP compatibility path

The original UDP control messages remain available for older clients:

- `RelaySession.Create(...)` creates a legacy private room;
- `RelaySession.Join(...)` joins with the legacy host token;
- `RelaySession.Leave(...)` performs best-effort cleanup.

New clients should create and join rooms through the HTTP API, then apply the
returned credentials to `RelayKcpTransport` before calling `StartHost()` or
`StartClient()`.

## Limits and behavior

- UDP datagrams are limited to 1200 bytes: 48-byte relay header plus at most
  1152 bytes of KCP packet. Oversize and malformed packets are ignored.
- Per-endpoint packet rate limiting protects the UDP receive loop.
- Room and member limits are enforced by `RelayRoomRegistry`.
- Authenticated UDP traffic refreshes a member's data endpoint. Legacy control
  and game-data sockets may differ; authentication does not pin the control socket.
- Clients may send game data only to peer 1 (the host); the host sends to members.
- Authenticated Ping acknowledgments confirm readiness and liveness, including
  a host with no remote clients. Heartbeat interval is 0.5 seconds.
- A periodic maintenance task expires inactive members and notifies peers.
- Member leave/kick notifies all remaining peers while keeping their sessions
  alive. Self-kick, host departure, close or relay failure ends the session.
- Kick revokes membership before any notification is sent.
- UDP payloads are authenticated for relay membership but are not encrypted.
  Add an application-level encryption layer when confidentiality is required.

## Test

```powershell
dotnet build .\RelayServer\RelayServer.csproj
dotnet run --project .\RelayServer.Tests\RelayServer.Tests.csproj
```

The tests cover the MTU boundary, wrong room/peer/token/target, forbidden
client-to-client forwarding, authenticated Ping, legacy independent sockets,
HTTP lifecycle, private invitations, capacity, three-member leave/kick isolation,
revoked tokens, owner close and peer expiration.

Full Unity and process acceptance:

```powershell
./RelayServer/Scripts/Test-RelayLobby.ps1
./RelayServer/Scripts/Test-RelayLobbyVisuals.ps1
```

See `Assets/Mirror/Examples/RelayLobby/README.md` and
`Assets/Mirror/Examples/RelayLobby/VERIFICATION.md` for
the editor scene, build parameters and actual acceptance evidence. The optional
`--control-file <path>` switch enables local acceptance fault injection:
`http-off`, `http-on`, `udp-off`, `udp-on`. Normal server runs do not need it.
