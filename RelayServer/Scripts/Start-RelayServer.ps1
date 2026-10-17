param(
    [int]$UdpPort = 9000,
    [int]$HttpPort = 9001,
    [string]$RelayAddress = '127.0.0.1',
    [switch]$Background,
    [string]$LogDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
dotnet build "$repo/RelayServer/RelayServer.csproj" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Relay server build failed.' }
$dll = "$repo/RelayServer/bin/Debug/net8.0/Mirror.RelayServer.dll"
if ($Background) {
    if (!$LogDirectory) { $LogDirectory = "$repo/Artifacts/RelayLobby/server" }
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    Start-Process dotnet -ArgumentList "`"$dll`" --udp $UdpPort --http $HttpPort --relay-address $RelayAddress" -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$LogDirectory/stdout.log" -RedirectStandardError "$LogDirectory/stderr.log"
} else {
    & dotnet $dll --udp $UdpPort --http $HttpPort --relay-address $RelayAddress
    exit $LASTEXITCODE
}
