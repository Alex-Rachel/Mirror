using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mirror.Relay;
using UnityEngine;

namespace Mirror.Examples.RelayLobby
{
    // Enabled only with explicit -relayRole. Reports contain state and public IDs only.
    public sealed class RelayLobbyTestBootstrap : MonoBehaviour
    {
        public RelayLobbyNetworkManager manager;
        public string outputPath;
        public string defaultRoomId, defaultInviteCode;
        CancellationTokenSource lifetime = new CancellationTokenSource();
        string commandPath, lastCommand, role, error, phase = "idle";
        int completedCommand;
        bool working, active;
        float nextReport, startedAt;
        string screenshot;
        float screenshotAt;
        readonly List<StageReport> stages = new List<StageReport>();
        [Serializable] public class StageReport
        {
            public string name;
            public float elapsedSeconds;
        }
        [Serializable] public class Command
        {
            public int id;
            public string action, roomId, memberId, inviteCode, http, path;
            public bool roomControls;
        }
        [Serializable] public class PlayerReport
        {
            public uint netId;
            public string name, color;
            public int score;
            public bool started, local;
            public Vector3 position;
        }
        [Serializable] public class Report
        {
            public string role, phase, error, roomId, memberId, state;
            public bool relayReady, mirrorConnected, playerSpawned, started, credentialsCleared;
            public int commandId;
            public float elapsedSeconds;
            public PlayerReport[] players;
            public StageReport[] stages;
        }

        async void Start()
        {
            role = GetArgument("-relayRole");
            if (string.IsNullOrEmpty(role)) return;
            active = true;
            startedAt = Time.realtimeSinceStartup;
            outputPath = GetArgument("-relayOutput") ?? outputPath;
            commandPath = GetArgument("-relayCommands");
            screenshot = GetArgument("-relayScreenshot");
            screenshotAt = Time.unscaledTime + 4;
            string http = GetArgument("-relayHttp");
            if (!string.IsNullOrEmpty(http))
            {
                manager.ConfigureLobbyApi(http);
                manager.uiController.httpEndpoint.SetTextWithoutNotify(manager.lobbyApiUrl);
            }
            try
            {
                working = true;
                phase = "http-allocation";
                if (role.Equals("host", StringComparison.OrdinalIgnoreCase))
                    await Create();
                else if (role.Equals("client", StringComparison.OrdinalIgnoreCase))
                    await Join(GetArgument("-relayRoom") ?? defaultRoomId, GetArgument("-relayInvite") ?? defaultInviteCode);
                phase = "state-synchronized";
            }
            catch (Exception exception)
            {
                Fail(exception);
                if (GetArgument("-relayKeepAlive") != "true") Application.Quit(2);
            }
            finally { working = false; WriteReport(); }
        }
        async Task Create() => await manager.StartLobbyHostAsync(new RelayCreateRoomRequest
        {
            name = "Process Test Room", displayName = GetArgument("-relayName") ?? "Host",
            capacity = 8, tags = new[] { "process-test" }
        }, lifetime.Token);
        async Task Join(string room, string invite) =>
            await manager.StartLobbyClientAsync(room, invite, GetArgument("-relayName") ?? "Client", lifetime.Token);

        void Update()
        {
            if (!active) return;
            if (NetworkClient.localPlayer != null && GetArgument("-relayAutoMove") == "true")
                NetworkClient.localPlayer.GetComponent<RelayLobbyPlayer>().TestInput =
                    new Vector2(Mathf.Sin(Time.unscaledTime), Mathf.Cos(Time.unscaledTime));
            if (!string.IsNullOrEmpty(screenshot) && Time.unscaledTime >= screenshotAt)
            {
                Capture(screenshot);
                screenshot = null;
            }
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + .25f;
            if (working)
                phase = !manager.CurrentCredentials.HasValue ? "http-allocation" : manager.relayTransport.RelayReady
                    ? NetworkClient.isConnected ? "player-spawn" : "mirror-connect" : "relay-ready";
            WriteReport();
            if (!working && !string.IsNullOrEmpty(commandPath) && File.Exists(commandPath))
            {
                try
                {
                    string json = File.ReadAllText(commandPath);
                    if (json != lastCommand)
                    {
                        Command command = JsonUtility.FromJson<Command>(json);
                        if (command != null && command.id > completedCommand)
                        {
                            lastCommand = json;
                            Execute(command);
                        }
                    }
                }
                catch (IOException) { } // A writer may be replacing the command atomically.
            }
        }
        async void Execute(Command command)
        {
            working = true;
            error = null;
            try
            {
                switch (command.action)
                {
                    case "start": await manager.StartRoomAsync(lifetime.Token); break;
                    case "kick": await manager.KickMemberAsync(command.memberId, lifetime.Token); break;
                    case "leave": await manager.LeaveRoomAsync(lifetime.Token); break;
                    case "close": await manager.CloseRoomAsync(lifetime.Token); break;
                    case "create": stages.Clear(); await Create(); break;
                    case "join": stages.Clear(); await Join(command.roomId, command.inviteCode); break;
                    case "endpoint": manager.ConfigureLobbyApi(command.http); break;
                    case "screenshot":
                        if (command.roomControls)
                        {
                            Canvas.ForceUpdateCanvases();
                            manager.uiController.roomPanel.GetComponentInParent<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition = 0;
                        }
                        Capture(command.path);
                        break;
                    case "quit": Application.Quit(0); break;
                    default: throw new ArgumentException("Unknown process test action.");
                }
                phase = "command-complete";
            }
            catch (Exception exception) { Fail(exception); }
            finally { completedCommand = command.id; working = false; WriteReport(); }
        }
        void Fail(Exception exception)
        {
            phase = "failed";
            error = exception is RelayApiException api
                ? api.Message
                : exception.GetType().Name + ": " + exception.Message;
            manager.uiController?.SetMessage(error);
        }
        void Capture(string path)
        {
            if (GetArgument("-relayCaptureOffscreen") != "true")
            {
                ScreenCapture.CaptureScreenshot(path);
                return;
            }
            // Hidden Windows players do not present their swap chain. Render the
            // actual scene and serialized Canvas into the same size target instead.
            Camera arena = Camera.main;
            Canvas canvas = manager.uiController.GetComponent<Canvas>();
            RenderTexture target = RenderTexture.GetTemporary(Screen.width, Screen.height, 24);
            RenderTexture oldActive = RenderTexture.active;
            RenderTexture oldTarget = arena.targetTexture;
            RenderMode oldMode = canvas.renderMode;
            Camera oldCamera = canvas.worldCamera;
            float oldDistance = canvas.planeDistance;
            var uiCameraObject = new GameObject("QA capture camera", typeof(Camera));
            var uiCamera = uiCameraObject.GetComponent<Camera>();
            uiCamera.enabled = false;
            uiCamera.clearFlags = CameraClearFlags.Depth;
            uiCamera.cullingMask = 1 << 5;
            uiCamera.targetTexture = target;
            Transform[] elements = canvas.GetComponentsInChildren<Transform>(true);
            int[] layers = elements.Select(element => element.gameObject.layer).ToArray();
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, new Color(.02f,.03f,.05f));
                foreach (Transform element in elements) element.gameObject.layer = 5;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = 1;
                Canvas.ForceUpdateCanvases();
                arena.targetTexture = target;
                arena.Render();
                uiCamera.Render();
                var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                try
                {
                    texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);
                    texture.Apply();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                }
                finally { Destroy(texture); }
            }
            finally
            {
                arena.targetTexture = oldTarget;
                canvas.renderMode = oldMode;
                canvas.worldCamera = oldCamera;
                canvas.planeDistance = oldDistance;
                for (int i = 0; i < elements.Length; i++) elements[i].gameObject.layer = layers[i];
                RenderTexture.active = oldActive;
                RenderTexture.ReleaseTemporary(target);
                Destroy(uiCameraObject);
            }
        }
        void WriteReport()
        {
            if (string.IsNullOrWhiteSpace(outputPath)) return;
            var players = FindObjectsByType<RelayLobbyPlayer>(FindObjectsSortMode.None);
            if (manager.CurrentRoomCredentials != null) MarkStage("http-allocation");
            if (manager.relayTransport.RelayReady) MarkStage("relay-ready");
            if (NetworkClient.isConnected) MarkStage("mirror-connected");
            if (NetworkClient.localPlayer != null) MarkStage("player-spawned");
            if (NetworkClient.localPlayer != null && manager.CurrentRoom != null &&
                players.All(player => !string.IsNullOrEmpty(player.displayName))) MarkStage("state-synchronized");
            Report report = new Report
            {
                role = role, phase = phase, error = error ?? manager.LastSessionError,
                roomId = manager.CurrentRoomCredentials?.roomId,
                memberId = manager.CurrentRoomCredentials?.memberId,
                state = manager.RoomState.ToString(), commandId = completedCommand,
                elapsedSeconds = Time.realtimeSinceStartup - startedAt,
                relayReady = manager.relayTransport.RelayReady, mirrorConnected = NetworkClient.isConnected,
                playerSpawned = NetworkClient.localPlayer != null,
                credentialsCleared = !manager.CurrentCredentials.HasValue && manager.CurrentRoomCredentials == null &&
                    string.IsNullOrEmpty(manager.relayTransport.peerToken),
                started = players.Any(player => player.roomStarted),
                stages = stages.ToArray(),
                players = players.Select(player => new PlayerReport
                {
                    netId = player.netId, name = player.displayName, color = ColorUtility.ToHtmlStringRGB(player.playerColor),
                    score = player.score, started = player.roomStarted, local = player.isLocalPlayer,
                    position = player.transform.position
                }).ToArray()
            };
            try
            {
                string path = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", JsonUtility.ToJson(report, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (IOException) { }
        }
        void MarkStage(string name)
        {
            if (!stages.Any(stage => stage.name == name))
                stages.Add(new StageReport { name = name, elapsedSeconds = Time.realtimeSinceStartup - startedAt });
        }
        void OnDestroy() { lifetime.Cancel(); lifetime.Dispose(); }
        public static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }
    }
}
