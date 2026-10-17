using System;
using System.Threading;
using System.Threading.Tasks;
using Mirror.Relay;
using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyUiController : MonoBehaviour
    {
        public RelayLobbyNetworkManager manager;
        public RelayLobbyRoomListView roomList;
        public RelayLobbyMemberListView memberList;
        public RelayLobbyCreatePanel createPanel;
        public RelayLobbyRoomPanel roomPanel;
        public Text statusText, messageText, scoreboard;
        public InputField joinRoomId, joinInviteCode, httpEndpoint, searchTag;
        public Button refreshButton, joinButton, endpointButton;
        public CanvasGroup submitGroup;
        [Min(1)] public float refreshSeconds = 2;
        CancellationTokenSource lifetime;
        bool busy, polling, operationError;
        string httpState = "checking";

        void Start()
        {
            manager.RoomEvents.RoomChanged += RenderRoom;
            manager.RoomEvents.RoomClosed += RoomClosed;
            manager.RoomEvents.Error += OnError;
            createPanel.createButton.onClick.AddListener(CreateRoom);
            joinButton.onClick.AddListener(JoinSelectedRoom);
            refreshButton.onClick.AddListener(RefreshRooms);
            endpointButton.onClick.AddListener(ApplyEndpoint);
            roomPanel.updateButton.onClick.AddListener(UpdateRoom);
            roomPanel.startButton.onClick.AddListener(StartRoom);
            roomPanel.closeButton.onClick.AddListener(CloseRoom);
            roomPanel.leaveButton.onClick.AddListener(LeaveRoom);
            roomPanel.copyButton.onClick.AddListener(CopyInfo);
            httpEndpoint.text = manager.lobbyApiUrl;
            RenderRoom(null);
        }
        async void OnEnable()
        {
            lifetime = new CancellationTokenSource();
            CancellationToken token = lifetime.Token;
            try
            {
                await Task.Yield();
                while (!token.IsCancellationRequested)
                {
                    if (!busy) await Poll(token);
                    await Task.Delay((int)(refreshSeconds * 1000), token);
                }
            }
            catch (OperationCanceledException) { }
        }
        void OnDisable() { lifetime?.Cancel(); }
        void OnDestroy()
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            if (manager == null) return;
            manager.RoomEvents.RoomChanged -= RenderRoom;
            manager.RoomEvents.RoomClosed -= RoomClosed;
            manager.RoomEvents.Error -= OnError;
        }
        void Update()
        {
            statusText.text = $"HTTP: {httpState}    Relay: {(manager.relayTransport.RelayReady ? "ready" : manager.CurrentCredentials.HasValue ? "connecting" : "idle")}\n" +
                $"Mirror: {(NetworkClient.isConnected ? NetworkServer.active ? "host connected" : "client connected" : NetworkClient.active ? "connecting" : "offline")}    Session: {manager.RoomState}";
            scoreboard.text = "ARENA  /  WASD or arrow keys\n" +
                (NetworkClient.localPlayer != null &&
                NetworkClient.localPlayer.GetComponent<RelayLobbyPlayer>().roomStarted ? "Move to score" : "Waiting: movement and scoring locked");
            foreach (RelayLobbyPlayer player in FindObjectsByType<RelayLobbyPlayer>(FindObjectsSortMode.None))
                scoreboard.text += $"\n<color=#{ColorUtility.ToHtmlStringRGB(player.playerColor)}>{player.displayName}</color>  {player.score} pts";
            bool idle = manager.CurrentRoomCredentials == null;
            createPanel.createButton.interactable = !busy && idle;
            joinButton.interactable = !busy && idle;
            endpointButton.interactable = !busy && idle;
        }
        public void SetMessage(string message) { if (messageText != null) messageText.text = message; }
        void OnError(Exception exception) { operationError = true; SetMessage(exception.Message); }
        void RoomClosed(string reason) { RenderRoom(null); SetMessage($"Session ended: {reason}"); }
        void RenderRoom(RelayRoomDetails room)
        {
            bool owner = !string.IsNullOrEmpty(manager.CurrentRoomCredentials?.ownerToken);
            roomPanel.Render(room, manager.LastInviteCode, owner);
            memberList.SetMembers(room?.members, owner, KickMember);
        }
        async Task Poll(CancellationToken token, bool manual = false)
        {
            while (polling)
            {
                if (!manual) return;
                await Task.Delay(20, token);
            }
            polling = true;
            try
            {
                try { await manager.LobbyApi.GetHealthAsync(token); httpState = "online"; }
                catch (OperationCanceledException) { throw; }
                catch { httpState = "unavailable"; }
                if (busy && !manual) return;
                RelayRoomPage page = await manager.ListRoomsAsync(tag: searchTag.text, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                roomList.SetRooms(page.rooms, room => joinRoomId.text = room.roomId);
                if ((!busy || manual) && manager.CurrentRoomCredentials != null) await manager.RefreshRoomAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (manual) throw;
                // Poll success never clears a prior operation error.
                if (!operationError) SetMessage($"Refresh failed: {exception.Message}");
            }
            finally { polling = false; }
        }
        async void Run(Func<CancellationToken, Task> action, string success)
        {
            if (busy) return;
            busy = true;
            submitGroup.interactable = false;
            try { await action(lifetime.Token); operationError = false; SetMessage(success); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { operationError = true; SetMessage(exception.Message); }
            finally
            {
                busy = false;
                if (this != null) { submitGroup.interactable = true; RenderRoom(manager.CurrentRoom); }
            }
        }
        public void CreateRoom() => Run(async token => { await manager.StartLobbyHostAsync(createPanel.ToRequest(), token); }, "Host ready. Waiting for players.");
        public void JoinSelectedRoom() => Run(async token => { await manager.StartLobbyClientAsync(joinRoomId.text, joinInviteCode.text, createPanel.displayName.text, token); }, "Connected. Waiting for host.");
        public void StartRoom() => Run(manager.StartRoomAsync, "Started. New joins are blocked.");
        public void UpdateRoom() => Run(async token => { await manager.UpdateRoomAsync(roomPanel.ToUpdateRequest(), token); roomPanel.ResetEdit(); }, "Room settings saved.");
        public void KickMember(string id) => Run(token => manager.KickMemberAsync(id, token), "Member removed.");
        public void CloseRoom() => Run(manager.CloseRoomAsync, "Room closed.");
        public void LeaveRoom() => Run(manager.LeaveRoomAsync, "Left room.");
        public void RefreshRooms() => Run(token => Poll(token, true), "Room list refreshed.");
        public void ApplyEndpoint() => Run(token => { manager.ConfigureLobbyApi(httpEndpoint.text); return Task.CompletedTask; }, "HTTP endpoint applied.");
        public void CopyInfo()
        {
            GUIUtility.systemCopyBuffer = manager.CurrentRoom?.room.roomId +
                (string.IsNullOrEmpty(manager.LastInviteCode) ? "" : "\n" + manager.LastInviteCode);
            SetMessage("Room ID and invitation copied.");
        }
    }
}
