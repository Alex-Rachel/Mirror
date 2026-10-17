using System;
using Mirror;
using Mirror.Relay;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby.Tests
{
    public sealed class RelayLobbyEditorTests
    {
        [Test]
        public void AddingNetworkTransformInitializesTargetBeforeResetState()
        {
            var root = new GameObject("Fresh player", typeof(NetworkIdentity));
            try
            {
                var sync = root.AddComponent<NetworkTransformReliable>();
                Assert.AreEqual(root.transform, sync.target);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void CredentialsRoundTripToTransportUri()
        {
            RelayRoomCredentials credentials = new RelayRoomCredentials
            {
                roomId = Guid.NewGuid().ToString("D"),
                memberId = Guid.NewGuid().ToString("D"),
                peerId = 2,
                relayAddress = "127.0.0.1",
                relayPort = 9000,
                relayToken = Guid.NewGuid().ToString("D"),
                memberToken = Guid.NewGuid().ToString("D")
            };

            Assert.IsTrue(credentials.IsValid);
            Assert.AreEqual("relaykcp", credentials.ToUri().Scheme);
            Assert.That(credentials.ToUri().Query, Does.Contain("peer=2"));
        }

        [Test]
        public void RoomBriefCalculatesAvailableSlots()
        {
            RelayRoomBrief room = new RelayRoomBrief { capacity = 4, playerCount = 3 };
            Assert.AreEqual(1, room.AvailableSlots);
            room.playerCount = 9;
            Assert.AreEqual(0, room.AvailableSlots);
        }

        [Test]
        public void ExamplePlayerPrefabHasNetworkIdentity()
        {
            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Mirror/Examples/RelayLobby/Prefabs/RelayLobbyPlayer.prefab");
            Assert.IsNotNull(player);
            Assert.IsNotNull(player.GetComponent<NetworkIdentity>());
            Assert.IsNotNull(player.GetComponent<RelayLobbyPlayer>());
            Assert.AreEqual(SyncDirection.ServerToClient, player.GetComponent<NetworkTransformReliable>().syncDirection);
            Assert.IsNotNull(player.GetComponent<RelayLobbyPlayer>().visual);
            Assert.IsNotNull(player.GetComponent<RelayLobbyPlayer>().label);
        }

        [Test]
        public void SavedSceneHasCompleteEditableUiAndArena()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Mirror/Examples/RelayLobby/Scenes/RelayLobby.unity");
            var manager = UnityEngine.Object.FindFirstObjectByType<RelayLobbyNetworkManager>();
            Assert.IsNotNull(manager);
            Assert.AreEqual(HeadlessStartOptions.DoNothing, manager.headlessStartMode);
            Assert.AreEqual(PlayerSpawnMethod.RoundRobin, manager.playerSpawnMethod);
            Assert.IsNotNull(manager.GetComponent<RelayLobbyTestBootstrap>());
            Assert.IsNotNull(manager.playerPrefab);
            Assert.IsNotNull(UnityEngine.Object.FindFirstObjectByType<EventSystem>());
            Assert.GreaterOrEqual(UnityEngine.Object.FindObjectsByType<NetworkStartPosition>(FindObjectsSortMode.None).Length, 3);
            var ui = manager.uiController;
            Assert.IsNotNull(ui);
            Assert.AreEqual(manager, ui.manager);
            Assert.IsNotNull(ui.roomList.entryPrefab.joinButton);
            Assert.IsNotNull(ui.memberList.entryPrefab.kickButton);
            Assert.GreaterOrEqual(ui.GetComponentsInChildren<ScrollRect>(true).Length, 3);
            foreach (var layout in ui.GetComponentsInChildren<HorizontalOrVerticalLayoutGroup>(true))
            {
                Assert.IsTrue(layout.childControlWidth, layout.name + " must distribute available width.");
                Assert.IsTrue(layout.childControlHeight, layout.name + " must constrain row height.");
            }
            foreach (var scroll in ui.GetComponentsInChildren<ScrollRect>(true))
                Assert.IsNotNull(scroll.verticalScrollbar, scroll.name + " needs a visible scroll affordance.");
            foreach (MonoBehaviour component in ui.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) Assert.Fail("Missing UI script.");
                if (!component.GetType().Namespace.StartsWith("Mirror.Examples.RelayLobby")) continue;
                foreach (var field in component.GetType().GetFields())
                    if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                        Assert.IsNotNull(field.GetValue(component), component.GetType().Name + "." + field.Name);
            }
        }

        [Test]
        public void RoomPollingPreservesUnsubmittedEdits()
        {
            var root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Mirror/Examples/RelayLobby/Prefabs/RelayLobbyUI.prefab"));
            try
            {
                var panel = root.GetComponent<RelayLobbyUiController>().roomPanel;
                var details = new RelayRoomDetails { room = new RelayRoomBrief
                    { roomId = "one", name = "Original", capacity = 8 } };
                panel.Render(details, null, true);
                panel.updateName.text = "Unsaved";
                panel.updatePrivateRoom.isOn = true;
                panel.Render(details, null, true);
                Assert.AreEqual("Unsaved", panel.updateName.text);
                Assert.IsTrue(panel.updatePrivateRoom.isOn);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
