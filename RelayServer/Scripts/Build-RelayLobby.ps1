param(
    [string]$Unity = 'C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe',
    [string]$Project = '',
    [string]$Output = '',
    [switch]$RegenerateAssets
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Project) { $Project = $repo }
$Project = [IO.Path]::GetFullPath($Project)
if (!$Output) { $Output = "$repo/Builds/RelayLobby/RelayLobby.exe" }
$logs = "$repo/Artifacts/RelayLobby"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
if ($RegenerateAssets) {
    $process = Start-Process $Unity -WindowStyle Hidden -PassThru -Wait -ArgumentList `
        "-batchmode -nographics -quit -projectPath `"$Project`" -executeMethod Mirror.Examples.RelayLobby.Editor.RelayLobbyAssets.Generate -logFile `"$logs/generate.log`""
    if ($process.ExitCode -ne 0) { throw "Asset generation failed. See $logs/generate.log" }
}
$process = Start-Process $Unity -WindowStyle Hidden -PassThru -Wait -ArgumentList `
    "-batchmode -nographics -quit -projectPath `"$Project`" -executeMethod Mirror.Examples.RelayLobby.Editor.RelayLobbyAssets.Build -relayBuildOutput `"$Output`" -logFile `"$logs/build.log`""
if ($process.ExitCode -ne 0) { throw "Example build failed. See $logs/build.log" }
Write-Output $Output
