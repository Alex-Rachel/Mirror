using System;
using System.IO;
using Mirror.Relay;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby.Editor
{
    public static class RelayLobbyAssets
    {
        public const string Root = "Assets/Mirror/Examples/RelayLobby";
        public const string ScenePath = Root + "/Scenes/RelayLobby.unity";
        static Font Font => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        static readonly Color Ink = new Color(0.88f, 0.93f, 1);

        [MenuItem("Mirror/Relay Lobby/Rebuild example assets")]
        public static void Generate()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject player = new GameObject("RelayLobbyPlayer");
            player.AddComponent<NetworkIdentity>();
            NetworkTransformReliable nt = player.AddComponent<NetworkTransformReliable>();
            nt.target = player.transform;
            nt.syncDirection = SyncDirection.ServerToClient;
            RelayLobbyPlayer behaviour = player.AddComponent<RelayLobbyPlayer>();
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Body";
            capsule.transform.SetParent(player.transform, false);
            capsule.transform.localPosition = Vector3.up;
            capsule.transform.localScale = new Vector3(.7f, 1, .7f);
            Material material = SaveMaterial("Player", Color.white);
            behaviour.visual = capsule.GetComponent<Renderer>();
            behaviour.visual.sharedMaterial = material;
            GameObject label = new GameObject("Name and Score", typeof(TextMesh));
            label.transform.SetParent(player.transform, false);
            label.transform.localPosition = new Vector3(0, 2.8f, 0);
            behaviour.label = label.GetComponent<TextMesh>();
            behaviour.label.text = "Player\n0 pts | WAIT";
            behaviour.label.font = Font;
            behaviour.label.fontSize = 36;
            behaviour.label.characterSize = .18f;
            behaviour.label.anchor = TextAnchor.MiddleCenter;
            label.GetComponent<MeshRenderer>().sharedMaterial = Font.material;
            GameObject playerPrefab = PrefabUtility.SaveAsPrefabAsset(player, Root + "/Prefabs/RelayLobbyPlayer.prefab");
            UnityEngine.Object.DestroyImmediate(player);

            RelayLobbyRoomEntry roomEntry = Entry<RelayLobbyRoomEntry>("RoomEntry");
            roomEntry.roomName = Text(roomEntry.transform, "Room", 16, 2);
            roomEntry.playerCount = Text(roomEntry.transform, "1/8", 15, 1);
            roomEntry.tags = Text(roomEntry.transform, "tags", 14, 1);
            roomEntry.joinButton = Button(roomEntry.transform, "Select");
            var roomPrefab = PrefabUtility.SaveAsPrefabAsset(roomEntry.gameObject, Root + "/Prefabs/RelayLobbyRoomEntry.prefab").GetComponent<RelayLobbyRoomEntry>();
            UnityEngine.Object.DestroyImmediate(roomEntry.gameObject);
            RelayLobbyMemberEntry memberEntry = Entry<RelayLobbyMemberEntry>("MemberEntry");
            memberEntry.memberName = Text(memberEntry.transform, "Member", 16, 2);
            memberEntry.status = Text(memberEntry.transform, "Connected", 14, 1);
            memberEntry.kickButton = Button(memberEntry.transform, "Kick");
            var memberPrefab = PrefabUtility.SaveAsPrefabAsset(memberEntry.gameObject, Root + "/Prefabs/RelayLobbyMemberEntry.prefab").GetComponent<RelayLobbyMemberEntry>();
            UnityEngine.Object.DestroyImmediate(memberEntry.gameObject);

            GameObject canvasObject = new GameObject("RelayLobbyUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);
            scaler.matchWidthOrHeight = .5f;
            RelayLobbyUiController ui = canvasObject.AddComponent<RelayLobbyUiController>();
            RectTransform panel = Panel(canvas.transform, "Lobby", new Vector2(.012f,.015f), new Vector2(.44f,.985f));
            ui.submitGroup = panel.gameObject.AddComponent<CanvasGroup>();
            RectTransform content = Scroll(panel, "Lobby controls", 0, true);
            Text(content, "RELAY LOBBY", 26, height: 42);
            ui.statusText = Text(content, "HTTP: checking", 16, height: 58);
            ui.messageText = Text(content, "Create a host or select a room to join.", 16, height: 56);
            Transform row = Row(content, "HTTP endpoint");
            ui.httpEndpoint = Input(row, "HTTP endpoint", "http://localhost:9001", 3);
            ui.endpointButton = Button(row, "Apply");
            Text(content, "CREATE / PLAYER IDENTITY", 17, height: 28);
            ui.createPanel = content.gameObject.AddComponent<RelayLobbyCreatePanel>();
            row = Row(content, "Room and player names");
            ui.createPanel.roomName = Input(row, "Room name", "Relay Room");
            ui.createPanel.displayName = Input(row, "Player name", "Player");
            row = Row(content, "Capacity and tags");
            ui.createPanel.capacity = Input(row, "Capacity (1-32)", "8");
            ui.createPanel.capacity.contentType = InputField.ContentType.IntegerNumber;
            ui.createPanel.tags = Input(row, "Tags, comma separated", "demo");
            row = Row(content, "Create actions");
            ui.createPanel.privateRoom = Toggle(row, "Private", false);
            ui.createPanel.joinable = Toggle(row, "Joinable", true);
            ui.createPanel.createButton = Button(row, "Create Host");
            Text(content, "FIND / JOIN", 17, height: 28);
            row = Row(content, "Search");
            ui.searchTag = Input(row, "Filter tag", "");
            ui.refreshButton = Button(row, "Search");
            row = Row(content, "Room ID");
            ui.joinRoomId = Input(row, "Room ID (select below or paste)", "", 3);
            row = Row(content, "Invitation");
            ui.joinInviteCode = Input(row, "Private invitation", "", 2);
            ui.joinButton = Button(row, "Join Room");
            RectTransform list = Box(content, "Public rooms", 122);
            ui.roomList = list.gameObject.AddComponent<RelayLobbyRoomListView>();
            ui.roomList.content = Scroll(list, "Rooms", 0, true);
            ui.roomList.entryPrefab = roomPrefab;
            Text(content, "CURRENT ROOM", 17, height: 28);
            ui.roomPanel = content.gameObject.AddComponent<RelayLobbyRoomPanel>();
            ui.roomPanel.roomName = Text(content, "No active room", 19, height: 30);
            ui.roomPanel.roomState = Text(content, "Offline", 15, height: 28);
            ui.roomPanel.inviteCode = Text(content, "", 14, height: 52);
            Transform owner = Box(content, "Owner settings", 102);
            Vertical(owner);
            ui.roomPanel.ownerFields = owner.gameObject;
            row = Row(owner, "Edit fields");
            ui.roomPanel.updateName = Input(row, "Room name", "");
            ui.roomPanel.updateCapacity = Input(row, "Capacity", "8");
            ui.roomPanel.updateTags = Input(row, "Tags", "");
            row = Row(owner, "Visibility");
            ui.roomPanel.updatePrivateRoom = Toggle(row, "Private", false);
            ui.roomPanel.updateJoinable = Toggle(row, "Joinable", true);
            row = Row(content, "Session actions");
            ui.roomPanel.updateButton = Button(row, "Save");
            ui.roomPanel.startButton = Button(row, "Start");
            ui.roomPanel.closeButton = Button(row, "Close");
            ui.roomPanel.leaveButton = Button(row, "Leave");
            ui.roomPanel.copyButton = Button(row, "Copy ID");
            Text(content, "MEMBERS", 17, height: 28);
            list = Box(content, "Members", 132);
            ui.memberList = list.gameObject.AddComponent<RelayLobbyMemberListView>();
            ui.memberList.content = Scroll(list, "Member list", 0, true);
            ui.memberList.entryPrefab = memberPrefab;
            RectTransform score = Panel(canvas.transform, "Scoreboard", new Vector2(.46f,.73f), new Vector2(.99f,.985f));
            ui.scoreboard = Text(score, "ARENA / WASD or arrow keys", 21);
            Stretch((RectTransform)ui.scoreboard.transform, 16);
            ui.scoreboard.alignment = TextAnchor.UpperLeft;
            PrefabUtility.SaveAsPrefabAsset(canvasObject, Root + "/Prefabs/RelayLobbyUI.prefab");
            UnityEngine.Object.DestroyImmediate(canvasObject);
            canvasObject = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/RelayLobbyUI.prefab"));
            ui = canvasObject.GetComponent<RelayLobbyUiController>();
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            GameObject managerObject = new GameObject("RelayLobbyNetworkManager");
            RelayKcpTransport transport = managerObject.AddComponent<RelayKcpTransport>();
            RelayLobbyNetworkManager manager = managerObject.AddComponent<RelayLobbyNetworkManager>();
            manager.transport = transport;
            manager.relayTransport = transport;
            manager.playerPrefab = playerPrefab;
            manager.autoCreatePlayer = true;
            manager.dontDestroyOnLoad = false;
            manager.headlessStartMode = HeadlessStartOptions.DoNothing;
            manager.uiController = ui;
            manager.sendRate = 30;
            manager.playerSpawnMethod = PlayerSpawnMethod.RoundRobin;
            ui.manager = manager;
            managerObject.AddComponent<RelayLobbyTestBootstrap>().manager = manager;

            GameObject cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.rect = new Rect(.45f,0,.55f,.72f);
            camera.orthographic = true;
            camera.orthographicSize = 13;
            camera.backgroundColor = new Color(.04f,.07f,.13f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            cameraObject.transform.position = new Vector3(0,24,-16);
            cameraObject.transform.LookAt(Vector3.zero);
            GameObject light = new GameObject("Sun", typeof(Light));
            light.GetComponent<Light>().type = LightType.Directional;
            light.GetComponent<Light>().intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(55,-30,0);
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Arena floor";
            floor.transform.position = new Vector3(0,-.25f,0);
            floor.transform.localScale = new Vector3(22,.5f,22);
            Material floorMaterial = SaveMaterial("Arena", new Color(.13f,.22f,.29f));
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
            for (int i = 0; i < 8; i++)
            {
                GameObject spawn = new GameObject("Spawn " + (i + 1), typeof(NetworkStartPosition));
                spawn.transform.position = new Vector3((i % 4 - 1.5f) * 4.5f,0,(i / 4 - .5f)*6);
            }
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("Relay Lobby serialized scene and prefabs generated.");
        }

        [MenuItem("Mirror/Relay Lobby/Build Windows example")]
        public static void Build()
        {
            string output = Argument("-relayBuildOutput") ?? "Builds/RelayLobby/RelayLobby.exe";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            InsecureHttpOption old = PlayerSettings.insecureHttpOption;
            try
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, locationPathName = output,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new Exception("Relay Lobby build failed: " + report.summary.result);
            }
            finally { PlayerSettings.insecureHttpOption = old; }
        }
        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(key.Length + 1);
                if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }
        static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }
        static void Stretch(RectTransform rect, float padding = 0)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * padding; rect.offsetMax = Vector2.one * -padding;
        }
        static RectTransform Panel(Transform parent, string name, Vector2 min, Vector2 max)
        {
            var rect = Rect(parent, name);
            rect.anchorMin = min; rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.gameObject.AddComponent<Image>().color = new Color(.035f,.06f,.1f,.96f);
            return rect;
        }
        static void Vertical(Transform parent)
        {
            var layout = parent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10,10,8,8);
            layout.spacing = 5; layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
        }
        static RectTransform Box(Transform parent, string name, float height)
        {
            var rect = Rect(parent, name);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            return rect;
        }
        static RectTransform Row(Transform parent, string name)
        {
            var rect = Box(parent, name, 38);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.spacing = 6; layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            return rect;
        }
        static T Entry<T>(string name) where T : Component => Row(null, name).gameObject.AddComponent<T>();
        static Text Text(Transform parent, string value, int size, float width = 1, float height = 32)
        {
            var rect = Box(parent, value, height);
            rect.GetComponent<LayoutElement>().flexibleWidth = width;
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = Font; text.text = value; text.fontSize = size; text.color = Ink;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = true;
            return text;
        }
        static InputField Input(Transform parent, string hint, string value, float width = 1)
        {
            var rect = Rect(parent, hint);
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = width;
            Image image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.12f,.18f,.26f);
            InputField input = rect.gameObject.AddComponent<InputField>();
            Text text = Text(rect, "", 15); Stretch((RectTransform)text.transform, 7);
            text.supportRichText = false;
            Text placeholder = Text(rect, hint, 14); Stretch((RectTransform)placeholder.transform, 7);
            placeholder.color = new Color(.55f,.65f,.76f);
            placeholder.supportRichText = false;
            input.textComponent = text; input.placeholder = placeholder; input.targetGraphic = image;
            input.text = value;
            return input;
        }
        static Button Button(Transform parent, string caption)
        {
            var rect = Rect(parent, caption);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minWidth = 68; element.flexibleWidth = 1;
            Image image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.13f,.35f,.58f);
            Button button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            Text label = Text(rect, caption, 15); Stretch((RectTransform)label.transform, 3);
            label.alignment = TextAnchor.MiddleCenter;
            return button;
        }
        static Toggle Toggle(Transform parent, string caption, bool value)
        {
            var rect = Rect(parent, caption);
            var element = rect.gameObject.AddComponent<LayoutElement>(); element.minWidth = 110; element.flexibleWidth = 1;
            Toggle toggle = rect.gameObject.AddComponent<Toggle>();
            var box = Rect(rect, "Box"); box.anchorMin = box.anchorMax = new Vector2(0,.5f);
            box.pivot = new Vector2(0,.5f); box.sizeDelta = new Vector2(22,22);
            Image background = box.gameObject.AddComponent<Image>(); background.color = new Color(.2f,.3f,.4f);
            var check = Rect(box, "Check"); Stretch(check, 4);
            Image graphic = check.gameObject.AddComponent<Image>(); graphic.color = new Color(.35f,.9f,.7f);
            toggle.targetGraphic = background; toggle.graphic = graphic;
            Text label = Text(rect, caption, 15); Stretch((RectTransform)label.transform);
            ((RectTransform)label.transform).offsetMin = new Vector2(28,0);
            toggle.isOn = value;
            return toggle;
        }
        static RectTransform Scroll(Transform parent, string name, float height, bool vertical)
        {
            var root = Rect(parent, name); Stretch(root);
            root.gameObject.AddComponent<Image>().color = new Color(.07f,.1f,.16f,.8f);
            ScrollRect scroll = root.gameObject.AddComponent<ScrollRect>();
            var viewport = Rect(root, "Viewport"); Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect(viewport, "Content");
            content.anchorMin = new Vector2(0,1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f,1);
            content.sizeDelta = Vector2.zero;
            Vertical(content);
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content;
            scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 30;
            var track = Rect(root, "Scrollbar");
            track.anchorMin = new Vector2(1,0); track.anchorMax = Vector2.one;
            track.pivot = Vector2.one; track.sizeDelta = new Vector2(8,0);
            track.gameObject.AddComponent<Image>().color = new Color(.1f,.15f,.22f);
            var handle = Rect(track, "Handle"); Stretch(handle);
            var image = handle.gameObject.AddComponent<Image>(); image.color = new Color(.3f,.45f,.6f);
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle; bar.targetGraphic = image;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scroll.verticalScrollbarSpacing = 3;
            return content;
        }
        static Material SaveMaterial(string name, Color color)
        {
            string path = Root + "/Prefabs/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null
                ? Shader.Find("Standard")
                : Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else material.shader = shader;
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
