using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Mirror.Relay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mirror.Examples.RelayLobby.Tests
{
    public sealed class RelayLobbyRuntimeTests
    {
        RelayLobbyNetworkManager manager;
        [UnitySetUp]
        public IEnumerator LoadActualExample()
        {
            yield return SceneManager.LoadSceneAsync("RelayLobby");
            yield return null;
            manager = UnityEngine.Object.FindFirstObjectByType<RelayLobbyNetworkManager>();
            Assert.IsNotNull(manager);
            // Integration tests use the standalone server started by Test-RelayLobby.ps1.
            manager.ConfigureLobbyApi("http://localhost:9001");
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (manager != null)
            {
                Task leave = manager.LeaveRoomAsync();
                yield return Wait(leave);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator WaitingStartMovementAndLeaveUseRealScene()
        {
            Task open = manager.StartLobbyHostAsync(new RelayCreateRoomRequest { name = "PlayMode", displayName = "TestHost" });
            yield return Wait(open);
            Assert.IsTrue(manager.relayTransport.RelayReady);
            var player = NetworkClient.localPlayer.GetComponent<RelayLobbyPlayer>();
            player.TestInput = Vector2.right;
            Vector3 waitingPosition = player.transform.position;
            yield return new WaitForSeconds(.4f);
            Assert.IsFalse(player.roomStarted);
            Assert.AreEqual(0, player.score);
            Assert.AreEqual(waitingPosition, player.transform.position);
            yield return Wait(manager.StartRoomAsync());
            yield return new WaitForSeconds(.5f);
            Assert.IsTrue(player.roomStarted);
            Assert.Greater(player.score, 0);
            Assert.Greater(player.transform.position.x, waitingPosition.x);
            // Stop input transmission while the server continues running.
            NetworkClient.ready = false;
            yield return new WaitForSeconds(.5f);
            Vector3 expiredPosition = player.transform.position;
            int expiredScore = player.score;
            yield return new WaitForSeconds(.2f);
            Assert.AreEqual(expiredPosition, player.transform.position, "Expired input still moved the player.");
            Assert.AreEqual(expiredScore, player.score, "Expired input still scored.");
            NetworkClient.ready = true;
            yield return Wait(manager.LeaveRoomAsync());
            Assert.IsNull(manager.CurrentRoom);
            Assert.IsNull(manager.CurrentRoomCredentials);
            Assert.IsFalse(NetworkClient.active);
            Assert.IsFalse(NetworkServer.active);
            Assert.IsTrue(string.IsNullOrEmpty(manager.relayTransport.peerToken));
        }

        [UnityTest]
        public IEnumerator HttpFailureRollsBackAndCanRetry()
        {
            manager.ConfigureLobbyApi("http://localhost:1");
            Task failed = manager.StartLobbyHostAsync(new RelayCreateRoomRequest { name = "Failed" });
            yield return Wait(failed, true);
            Assert.IsTrue(failed.IsFaulted);
            Assert.IsNull(manager.CurrentRoomCredentials);
            Assert.IsFalse(NetworkClient.active);
            Assert.IsFalse(NetworkServer.active);
            manager.uiController.SetMessage("Manual refresh pending");
            manager.uiController.refreshButton.onClick.Invoke();
            float refreshDeadline = Time.realtimeSinceStartup + 12;
            while (manager.uiController.messageText.text == "Manual refresh pending" &&
                Time.realtimeSinceStartup < refreshDeadline) yield return null;
            string refreshError = manager.uiController.messageText.text;
            StringAssert.StartsWith("HTTP ", refreshError, "Failed manual refresh displayed a success message.");
            yield return new WaitForSeconds(2.2f);
            Assert.AreEqual(refreshError, manager.uiController.messageText.text, "Polling overwrote the operation error.");
            manager.ConfigureLobbyApi("http://localhost:9001");
            yield return Wait(manager.StartLobbyHostAsync(new RelayCreateRoomRequest { name = "Retry" }));
            Assert.IsTrue(manager.relayTransport.RelayReady);
        }

        [UnityTest]
        public IEnumerator ExitCancelsPendingAllocationWithoutRestoringOldState()
        {
            Task old = manager.StartLobbyHostAsync(new RelayCreateRoomRequest { name = "Cancelled" });
            yield return Wait(manager.LeaveRoomAsync());
            yield return Wait(old, true);
            yield return new WaitForSeconds(.3f);
            Assert.IsTrue(old.IsCanceled || old.IsFaulted);
            Assert.IsNull(manager.CurrentRoom);
            Assert.IsNull(manager.CurrentRoomCredentials);
            Assert.IsFalse(NetworkClient.active);
            yield return Wait(manager.StartLobbyHostAsync(new RelayCreateRoomRequest { name = "After cancellation" }));
            Assert.AreEqual("After cancellation", manager.CurrentRoom.room.name);
        }

        [UnityTest]
        public IEnumerator LegacyControlSocketSupportsCancellationAndRetry()
        {
            using var blackhole = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            blackhole.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            manager.relayPort = (ushort)((IPEndPoint)blackhole.LocalEndPoint).Port;
            Task old = manager.StartRelayHostAsync();
            yield return new WaitForSeconds(.1f);
            float before = Time.realtimeSinceStartup;
            yield return Wait(manager.LeaveRoomAsync());
            yield return Wait(old, true);
            Assert.Less(Time.realtimeSinceStartup - before, 2, "Canceled UDP receive kept the operation queue blocked.");
            Assert.IsNull(manager.CurrentCredentials);
            Assert.IsFalse(NetworkServer.active);
            manager.relayPort = 9000;
            yield return Wait(manager.StartRelayHostAsync());
            Assert.IsTrue(manager.relayTransport.RelayReady);
            Assert.IsNotEmpty(manager.LastInviteCode);
        }

        static IEnumerator Wait(Task task, bool allowFailure = false)
        {
            float deadline = Time.realtimeSinceStartup + 20;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "Async operation exceeded 20 seconds.");
            if (!allowFailure && task.IsFaulted) throw task.Exception.GetBaseException();
            if (!allowFailure) Assert.IsFalse(task.IsCanceled);
        }
    }
}
