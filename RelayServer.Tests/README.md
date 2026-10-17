# Relay server tests

```powershell
dotnet run --project ./RelayServer.Tests/RelayServer.Tests.csproj
```

Tests use local in-process HTTP/UDP servers on dynamically selected ports and
exit nonzero on an assertion failure. No Unity installation is required.

Coverage: legacy separate control/data sockets, bidirectional forwarding,
authenticated heartbeat acknowledgment, 1200-byte datagram / 1152-byte payload
boundary, oversize rejection, wrong credentials/room/sender/target, forbidden
client-to-client forwarding, three-member leave/kick isolation, revoked tokens,
host leave/expiration, HTTP room lifecycle, capacity, private invitations and
public-to-private conversion, member authorization, and late-join rejection.

This is one layer of validation. The Unity and independent player acceptance
entry is `RelayServer/Scripts/Test-RelayLobby.ps1`; visual capture is
`RelayServer/Scripts/Test-RelayLobbyVisuals.ps1`.
