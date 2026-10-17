param(
    [string]$Player = '',
    [int]$UdpPort = 19000,
    [int]$HttpPort = 19001,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Player) { $Player = "$repo/Builds/RelayLobby/RelayLobby.exe" }
if (!$Output) { $Output = "$repo/Artifacts/RelayLobby/processes" }
$Player = [IO.Path]::GetFullPath($Player)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$owned = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()
$commands = @{}
$control = "$Output/server-control.txt"
$http = "http://localhost:$HttpPort"
$server = $null
function Read-Report($name) {
    try { Get-Content "$Output/$name.json" -Raw | ConvertFrom-Json } catch { $null }
}
function Wait-Report($name, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $report = Read-Report $name
        if ($report -and (& $Predicate $report)) { return $report }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$name timed out; state: $($report | ConvertTo-Json -Depth 5 -Compress)"
}
function Pass($name) {
    Write-Host "PASS $name"
    $results.Add(@{ test = $name; passed = $true; utc = [DateTime]::UtcNow.ToString('O') })
}
function Launch($name, $role, $room = '') {
    foreach ($suffix in @('.json', '-command.json', '.json.tmp')) {
        $oldReport = Join-Path $Output ($name + $suffix)
        if (Test-Path -LiteralPath $oldReport) { Remove-Item -LiteralPath $oldReport }
    }
    $args = "-batchmode -nographics -relayRole=$role -relayName=$name -relayHttp=$http -relayKeepAlive=true -relayAutoMove=true -relayOutput=`"$Output/$name.json`" -relayCommands=`"$Output/$name-command.json`" -logFile `"$Output/$name.log`""
    # Exercise the space-separated form as well as the equals form.
    if ($room) { $args += " -relayRoom $room" }
    $process = Start-Process $Player -ArgumentList $args -WindowStyle Hidden -PassThru
    $owned.Add($process)
    return $process
}
function Command($name, $action, $extra = @{}, [switch]$AllowError) {
    $commands[$name] = 1 + [int]$commands[$name]
    $body = @{ id = $commands[$name]; action = $action }
    foreach ($key in $extra.Keys) { $body[$key] = $extra[$key] }
    $temp = "$Output/$name-command.tmp"
    $body | ConvertTo-Json -Compress | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination "$Output/$name-command.json" -Force
    $expected = $commands[$name]
    $report = Wait-Report $name { param($r) $r.commandId -eq $expected }
    if (!$AllowError -and $report.phase -eq 'failed') { throw "$name $action failed: $($report.error)" }
    return $report
}
function Start-ServiceProcess {
    'http-on' | Set-Content -LiteralPath $control
    $dll = "$repo/RelayServer/bin/Debug/net8.0/Mirror.RelayServer.dll"
    $index = $owned.Count
    $process = Start-Process dotnet -WindowStyle Hidden -PassThru `
        -ArgumentList "`"$dll`" --udp $UdpPort --http $HttpPort --control-file `"$control`"" `
        -RedirectStandardOutput "$Output/server-$index.log" -RedirectStandardError "$Output/server-$index.err"
    $owned.Add($process)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try { if ((Invoke-RestMethod "http://127.0.0.1:$HttpPort/health" -TimeoutSec 1).status -eq 'ok') { return $process } } catch { }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Server startup failed.'
}
function Fault($value) {
    Set-Content -LiteralPath $control -Value $value -Encoding utf8
    Start-Sleep -Milliseconds 1200
}
try {
    dotnet build "$repo/RelayServer/RelayServer.csproj" -v:q *> "$Output/server-build.log"
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
    if (!(Test-Path -LiteralPath $Player)) { throw "Missing player: $Player" }
    $server = Start-ServiceProcess
    $hostProcess = Launch 'Host' 'host'
    $hostReport = Wait-Report 'Host' { param($r) $r.relayReady -and $r.mirrorConnected -and $r.playerSpawned }
    $room = $hostReport.roomId
    $firstProcess = Launch 'Alice' 'client' $room
    $secondProcess = Launch 'Bob' 'client' $room
    foreach ($name in @('Host','Alice','Bob')) {
        $r = Wait-Report $name { param($r) $r.players.Count -eq 3 -and ($r.players.name -contains 'Host') -and ($r.players.name -contains 'Alice') -and ($r.players.name -contains 'Bob') }
        if (($r.players | Where-Object { $_.score -ne 0 -or $_.started }).Count -ne 0) { throw 'Waiting room allowed movement or scoring.' }
        if (($r.players.color | Select-Object -Unique).Count -ne 3) { throw 'Player colors were not distinct.' }
    }
    Pass 'three independent processes: spawn, names, colors, waiting gate'
    $null = Command 'Alice' 'leave'
    $null = Wait-Report 'Alice' { param($r) $r.credentialsCleared -and !$r.mirrorConnected }
    $null = Wait-Report 'Bob' { param($r) $r.mirrorConnected -and $r.players.Count -eq 2 }
    Pass 'one client leaves; other client remains connected'
    $null = Command 'Alice' 'join' @{ roomId = $room }
    $null = Wait-Report 'Bob' { param($r) $r.players.Count -eq 3 }
    Pass 'clean rejoin'
    $alice = Read-Report 'Alice'
    $null = Command 'Host' 'kick' @{ memberId = $alice.memberId }
    $null = Wait-Report 'Alice' { param($r) $r.credentialsCleared -and !$r.mirrorConnected }
    $null = Wait-Report 'Bob' { param($r) $r.mirrorConnected -and $r.players.Count -eq 2 }
    Pass 'kick isolation and revoked member cleanup'
    $null = Command 'Alice' 'join' @{ roomId = $room }
    $null = Wait-Report 'Host' { param($r) $r.players.Count -eq 3 }
    $before = Read-Report 'Bob'
    $null = Command 'Host' 'start'
    foreach ($name in @('Host','Alice','Bob')) {
        $null = Wait-Report $name { param($r) $r.players.Count -eq 3 -and ($r.players | Where-Object { !$_.started -or $_.score -lt 5 }).Count -eq 0 }
    }
    $after = Read-Report 'Bob'
    $localBefore = $before.players | Where-Object local
    $localAfter = $after.players | Where-Object local
    if ($localBefore.position.x -eq $localAfter.position.x -and $localBefore.position.z -eq $localAfter.position.z) { throw 'Movement did not synchronize.' }
    Pass 'durable start state, authoritative movement and synchronized scoring'
    $lateProcess = Launch 'Late' 'client' $room
    $late = Wait-Report 'Late' { param($r) $r.phase -eq 'failed' -and $r.credentialsCleared }
    if ($late.error -notmatch '409') { throw "Late join failed for wrong reason: $($late.error)" }
    Pass 'started room rejects later joins with HTTP 409'
    $null = Command 'Host' 'close'
    foreach ($name in @('Host','Alice','Bob')) { $null = Wait-Report $name { param($r) $r.credentialsCleared -and !$r.mirrorConnected } }
    Pass 'close room cleans every process'
    $newHost = Command 'Host' 'create'
    $room = $newHost.roomId
    $null = Command 'Alice' 'join' @{ roomId = $room }
    $null = Command 'Bob' 'join' @{ roomId = $room }
    Fault 'http-off'
    $null = Command 'Alice' 'leave' -AllowError
    $null = Wait-Report 'Alice' { param($r) $r.credentialsCleared -and !$r.mirrorConnected }
    $failure = Command 'Alice' 'join' @{ roomId = $room } -AllowError
    if ($failure.phase -ne 'failed' -or !$failure.credentialsCleared) { throw 'HTTP outage did not rollback join.' }
    $null = Wait-Report 'Bob' { param($r) $r.mirrorConnected -and $r.relayReady }
    Fault 'http-on'
    $null = Command 'Alice' 'join' @{ roomId = $room }
    Pass 'HTTP-only outage: immediate local leave, join failure, surviving UDP session and retry'
    Fault 'udp-off'
    foreach ($name in @('Host','Alice','Bob')) { $null = Wait-Report $name { param($r) $r.credentialsCleared -and !$r.mirrorConnected } }
    $udpFailure = Command 'Alice' 'create' -AllowError
    if ($udpFailure.phase -ne 'failed' -or !$udpFailure.credentialsCleared) { throw 'Unavailable UDP startup did not rollback.' }
    Fault 'udp-on'
    $null = Command 'Host' 'create'
    $room = (Read-Report 'Host').roomId
    $null = Command 'Alice' 'join' @{ roomId = $room }
    $null = Command 'Bob' 'join' @{ roomId = $room }
    Pass 'UDP-only outage and failed startup cleanup; retry succeeds'
    Stop-Process -Id $hostProcess.Id -Force
    foreach ($name in @('Alice','Bob')) { $null = Wait-Report $name { param($r) $r.credentialsCleared -and !$r.mirrorConnected } }
    Pass 'killed host terminates both clients'
    $null = Command 'Alice' 'create'
    $room = (Read-Report 'Alice').roomId
    $null = Command 'Bob' 'join' @{ roomId = $room }
    Stop-Process -Id $server.Id -Force
    foreach ($name in @('Alice','Bob')) { $null = Wait-Report $name { param($r) $r.credentialsCleared -and !$r.mirrorConnected } }
    Pass 'stopped relay service terminates host and client'
    $server = Start-ServiceProcess
    $null = Command 'Alice' 'create'
    $room = (Read-Report 'Alice').roomId
    $null = Command 'Bob' 'join' @{ roomId = $room }
    $null = Command 'Alice' 'close'
    Pass 'retry after service restart'
} catch {
    $results.Add(@{ test = 'acceptance'; passed = $false; error = $_.Exception.Message })
    throw
} finally {
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$Output/results.json" -Encoding utf8
    foreach ($process in $owned) {
        $process.Refresh()
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}
