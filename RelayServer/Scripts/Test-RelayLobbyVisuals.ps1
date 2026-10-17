param(
    [string]$Player = '',
    [string]$Output = '',
    [int]$UdpPort = 20000,
    [int]$HttpPort = 20001
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Player) { $Player = "$repo/Builds/RelayLobby/RelayLobby.exe" }
if (!$Output) { $Output = "$repo/Artifacts/RelayLobby/visuals" }
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $Output | Out-Null
$owned = [Collections.Generic.List[object]]::new()
function Wait-Json($path, [scriptblock]$predicate) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        try {
            $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if (& $predicate $report) { return $report }
        } catch { }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for visual player report: $path"
}
function Send-Command($prefix, $id, $action, $extra = @{}) {
    $body = @{id=$id;action=$action}
    foreach ($key in $extra.Keys) { $body[$key] = $extra[$key] }
    $body | ConvertTo-Json -Compress | Set-Content -LiteralPath "$prefix-command.tmp" -Encoding utf8
    Move-Item -LiteralPath "$prefix-command.tmp" -Destination "$prefix-command.json" -Force
    $report = Wait-Json "$prefix.json" {param($r) $r.commandId -eq $id}
    if ($report.phase -eq 'failed') { throw $report.error }
}
try {
    $server = & "$PSScriptRoot/Start-RelayServer.ps1" -Background -UdpPort $UdpPort -HttpPort $HttpPort -LogDirectory "$Output/server" | Select-Object -Last 1
    $owned.Add($server)
    foreach ($size in @(@(1280,720),@(1600,900),@(1920,1080))) {
        $name = "$($size[0])x$($size[1])"
        $prefix = "$Output/$name"
        foreach ($suffix in @('.json','-command.json')) {
            if (Test-Path -LiteralPath "$prefix$suffix") { Remove-Item -LiteralPath "$prefix$suffix" }
        }
        $arguments = "-batchmode -relayRole=host -relayName=VisualHost -relayHttp=http://localhost:$HttpPort -relayCaptureOffscreen=true -relayAutoMove=true -relayOutput=`"$prefix.json`" -relayCommands=`"$prefix-command.json`" -screen-width $($size[0]) -screen-height $($size[1]) -screen-fullscreen 0 -logFile `"$prefix.log`""
        $hostPlayer = Start-Process $Player -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $owned.Add($hostPlayer)
        $report = Wait-Json "$prefix.json" {param($r) $r.playerSpawned -and $r.relayReady}
        $clients = @()
        foreach ($clientName in @('Alice','Bob')) {
            $client = Start-Process $Player -WindowStyle Hidden -PassThru -ArgumentList "-batchmode -nographics -relayRole=client -relayName=$clientName -relayHttp=http://localhost:$HttpPort -relayRoom=$($report.roomId) -relayAutoMove=true -logFile `"$prefix-$clientName.log`""
            $owned.Add($client)
            $clients += $client
        }
        $null = Wait-Json "$prefix.json" {param($r) $r.players.Count -eq 3}
        Send-Command $prefix 1 'screenshot' @{path="$prefix-waiting.png"}
        Send-Command $prefix 2 'screenshot' @{path="$prefix-waiting-controls.png";roomControls=$true}
        Send-Command $prefix 3 'start'
        $null = Wait-Json "$prefix.json" {param($r) $r.players.Count -eq 3 -and ($r.players | Where-Object { !$_.started -or $_.score -lt 5 }).Count -eq 0}
        Send-Command $prefix 4 'screenshot' @{path="$prefix-playing.png";roomControls=$true}
        foreach ($state in @('waiting','waiting-controls','playing')) {
            $png = "$prefix-$state.png"
            if (!(Test-Path -LiteralPath $png) -or (Get-Item -LiteralPath $png).Length -lt 10000) {
                throw "Missing or empty visual capture: $png"
            }
        }
        Send-Command $prefix 5 'close'
        foreach ($process in @($hostPlayer) + $clients) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        Write-Host "Captured waiting, owner controls and playing: $name"
    }
} finally {
    foreach ($process in $owned) {
        $process.Refresh()
        if (!$process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}
