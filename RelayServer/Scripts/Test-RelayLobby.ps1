param(
    [string]$Unity = 'C:/Program Files/Unity/Hub/Editor/6000.3.24f1/Editor/Unity.exe',
    [string]$Project = '',
    [string]$Player = '',
    [switch]$SkipUnityTests,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Project) { $Project = $repo }
$Project = [IO.Path]::GetFullPath($Project)
if (!$Player) { $Player = "$repo/Builds/RelayLobby/RelayLobby.exe" }
$logs = "$repo/Artifacts/RelayLobby"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
dotnet run --project "$repo/RelayServer.Tests/RelayServer.Tests.csproj" *> "$logs/server-tests.log"
if ($LASTEXITCODE -ne 0) { throw "Server tests failed: $logs/server-tests.log" }
if (!$SkipUnityTests) {
    $server = & "$PSScriptRoot/Start-RelayServer.ps1" -Background -LogDirectory "$logs/unity-server" | Select-Object -Last 1
    try {
        foreach ($platform in @('EditMode','PlayMode')) {
            $process = Start-Process $Unity -WindowStyle Hidden -PassThru -Wait -ArgumentList `
                "-batchmode -nographics -projectPath `"$Project`" -runTests -testPlatform $platform -testFilter Mirror.Examples.RelayLobby.Tests -testResults `"$logs/$platform.xml`" -logFile `"$logs/$platform.log`""
            if ($process.ExitCode -ne 0) { throw "$platform failed: see $logs/$platform.xml and .log" }
            [xml]$result = Get-Content "$logs/$platform.xml"
            if ($result.'test-run'.failed -ne '0' -or [int]$result.'test-run'.total -eq 0) { throw "$platform test result failed or empty." }
        }
    } finally { if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id -Force } }
}
if (!$SkipBuild) { & "$PSScriptRoot/Build-RelayLobby.ps1" -Unity $Unity -Project $Project -Output $Player }
& "$PSScriptRoot/Test-RelayLobbyProcesses.ps1" -Player $Player
