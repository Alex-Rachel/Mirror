using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mirror.Relay
{
    [AddComponentMenu("Network/Relay Network Manager")]
    public class RelayNetworkManager : NetworkManager
    {
        [Header("Relay Session")]
        public RelayKcpTransport relayTransport;
        public string relayAddress = "127.0.0.1";
        public ushort relayPort = 9000;
        public int relayRequestTimeoutMilliseconds = 10000;
        [Header("Lobby Control Plane")]
        public string lobbyApiUrl = "http://localhost:9001";
        public string defaultDisplayName = "Player";

        public RelayCredentials? CurrentCredentials { get; private set; }
        public RelayRoomCredentials CurrentRoomCredentials { get; private set; }
        public RelayRoomDetails CurrentRoom { get; private set; }
        public string LastInviteCode { get; private set; }
        public RelayLobbyApi LobbyApi { get; private set; }
        public RelayLobbyClient LobbyClient { get; private set; }
        public RelayRoomEvents RoomEvents { get; } = new RelayRoomEvents();
        public RelayRoomState RoomState { get; private set; }
        public string LastSessionError { get; private set; }
        public bool IsBusy => operations.CurrentCount == 0;

        readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        CancellationTokenSource session = new CancellationTokenSource();
        int sessionVersion;
        bool stopping;
        bool destroyed;

        public override void Awake()
        {
            base.Awake();
            EnsureRelayTransport();
            ConfigureLobbyApi(lobbyApiUrl);
            relayTransport.RelayFailed += OnRelayFailed;
        }

        public void ConfigureLobbyApi(string baseUrl)
        {
            if (CurrentCredentials.HasValue || IsBusy)
                throw new InvalidOperationException("Leave the session before changing the lobby endpoint.");
            lobbyApiUrl = string.IsNullOrWhiteSpace(baseUrl) ? lobbyApiUrl : baseUrl.TrimEnd('/');
            LobbyApi = new RelayLobbyApi(lobbyApiUrl);
            LobbyClient = new RelayLobbyClient(this);
        }

        public Task<RelayRoomPage> ListRoomsAsync(int pageSize = 50, string cursor = null,
            string tag = null, string state = null, CancellationToken cancellationToken = default) =>
            LobbyApi.ListRoomsAsync(pageSize, cursor, tag, state, cancellationToken);
        public Task<RelayRoomCredentials> CreateRoomAsync(RelayCreateRoomRequest request, CancellationToken cancellationToken = default) =>
            OpenLobbyAsync(request, null, false, cancellationToken);
        public Task<RelayRoomCredentials> JoinRoomAsync(RelayJoinRoomRequest request, CancellationToken cancellationToken = default) =>
            OpenLobbyAsync(null, request, false, cancellationToken);
        public Task<RelayRoomCredentials> StartLobbyHostAsync(RelayCreateRoomRequest request, CancellationToken cancellationToken = default) =>
            OpenLobbyAsync(request, null, true, cancellationToken);
        public Task<RelayRoomCredentials> StartLobbyClientAsync(string roomId, string inviteCode = null,
            string displayName = null, CancellationToken cancellationToken = default) =>
            OpenLobbyAsync(null, new RelayJoinRoomRequest
            {
                roomId = roomId, inviteCode = inviteCode,
                displayName = string.IsNullOrWhiteSpace(displayName) ? defaultDisplayName : displayName
            }, true, cancellationToken);

        async Task<RelayRoomCredentials> OpenLobbyAsync(RelayCreateRoomRequest create, RelayJoinRoomRequest join,
            bool startNetwork, CancellationToken cancellationToken)
        {
            int queuedVersion = sessionVersion;
            await operations.WaitAsync(cancellationToken);
            bool opening = false;
            int version = sessionVersion;
            try
            {
                if (queuedVersion != sessionVersion) throw new OperationCanceledException();
                EnsureIdle();
                NewSession();
                version = sessionVersion;
                opening = true;
                LastSessionError = null;
                RoomState = RelayRoomState.Connecting;
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
                RelayRoomCredentials credentials;
                RelayRoomDetails room;
                if (create != null)
                {
                    RelayCreateRoomResponse response = await LobbyApi.CreateRoomAsync(create, linked.Token);
                    credentials = response.credentials;
                    room = response.room;
                }
                else
                {
                    RelayJoinRoomResponse response = await LobbyApi.JoinRoomAsync(join, linked.Token);
                    credentials = response.credentials;
                    room = response.room;
                }
                if (linked.IsCancellationRequested || version != sessionVersion)
                {
                    await ReleaseAllocationAsync(credentials);
                    throw new OperationCanceledException(linked.Token);
                }
                CurrentRoomCredentials = credentials;
                if (credentials == null || !credentials.IsValid)
                    throw new InvalidOperationException("Lobby returned invalid relay credentials.");
                ApplyCredentials(credentials.ToTransportCredentials());
                ApplyRoom(room);
                if (startNetwork)
                {
                    RoomState = RelayRoomState.Connecting;
                    if (create != null) StartHost();
                    else StartClient(credentials.ToUri());
                    await WaitForNetworkAsync(create != null, linked.Token);
                    RoomState = CurrentRoom?.room.started == true ? RelayRoomState.Started : RelayRoomState.Connected;
                }
                return credentials;
            }
            catch (Exception exception)
            {
                if (opening && version == sessionVersion)
                {
                    LastSessionError = exception.Message;
                    await CleanupAsync("startup failed", CancellationToken.None);
                    RoomState = RelayRoomState.Error;
                    RoomEvents.RaiseError(exception);
                }
                throw;
            }
            finally { operations.Release(); }
        }

        async Task WaitForNetworkAsync(bool host, CancellationToken cancellationToken)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1, relayRequestTimeoutMilliseconds / 1000f);
            while (!(relayTransport.RelayReady && NetworkClient.isConnected && NetworkClient.localPlayer != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (destroyed || (!NetworkClient.active && !NetworkServer.active))
                    throw new InvalidOperationException(LastSessionError ?? "Mirror connection ended during startup.");
                if (Time.realtimeSinceStartup >= deadline)
                    throw new TimeoutException(host ? "Relay host did not become ready." : "Relay/Mirror connection timed out.");
                await Task.Delay(20, cancellationToken);
            }
        }

        public Task<RelayRoomDetails> GetRoomAsync(CancellationToken cancellationToken = default)
        {
            RelayRoomCredentials credentials = RequireCredentials();
            return LobbyApi.GetRoomAsync(credentials.roomId, credentials.memberId,
                IsOwner(credentials) ? credentials.ownerToken : credentials.memberToken, cancellationToken);
        }
        public Task<RelayRoomDetails> RefreshRoomAsync(CancellationToken cancellationToken = default) =>
            ChangeRoomAsync((credentials, token) => LobbyApi.GetRoomAsync(credentials.roomId, credentials.memberId,
                IsOwner(credentials) ? credentials.ownerToken : credentials.memberToken, token), false, cancellationToken);
        public async Task UpdateRoomAsync(RelayRoomUpdateRequest request, CancellationToken cancellationToken = default) =>
            await ChangeRoomAsync((credentials, token) => LobbyApi.UpdateRoomAsync(credentials.roomId, request, credentials.ownerToken, token), true, cancellationToken);
        public virtual async Task StartRoomAsync(CancellationToken cancellationToken = default) =>
            await ChangeRoomAsync((credentials, token) => LobbyApi.StartRoomAsync(credentials.roomId, credentials.ownerToken, token), true, cancellationToken);
        public async Task KickMemberAsync(string memberId, CancellationToken cancellationToken = default) =>
            await ChangeRoomAsync((credentials, token) => LobbyApi.KickMemberAsync(credentials.roomId, memberId, credentials.ownerToken, token), true, cancellationToken);

        async Task<RelayRoomDetails> ChangeRoomAsync(Func<RelayRoomCredentials, CancellationToken, Task<RelayRoomDetails>> request,
            bool owner, CancellationToken cancellationToken)
        {
            int version = sessionVersion;
            await operations.WaitAsync(cancellationToken);
            try
            {
                if (version != sessionVersion) throw new OperationCanceledException();
                RelayRoomCredentials credentials = RequireCredentials();
                if (owner && !IsOwner(credentials)) throw new InvalidOperationException("Room owner required.");
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
                RelayRoomDetails room = await request(credentials, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (version != sessionVersion) throw new OperationCanceledException();
                ApplyRoom(room);
                return room;
            }
            catch (RelayApiException exception) when (exception.StatusCode == 403 || exception.StatusCode == 404)
            {
                if (version == sessionVersion) await CleanupAsync("membership ended", CancellationToken.None);
                throw;
            }
            finally { operations.Release(); }
        }

        public Task LeaveRoomAsync(CancellationToken cancellationToken = default) => CleanupAsync("left", cancellationToken);
        public Task CloseRoomAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOwner(RequireCredentials())) throw new InvalidOperationException("Room owner required.");
            return CleanupAsync("closed", cancellationToken);
        }

        async Task CleanupAsync(string reason, CancellationToken cancellationToken)
        {
            RelayRoomCredentials credentials = CurrentRoomCredentials;
            RelayCredentials? relay = CurrentCredentials;
            bool hadSession = credentials != null || relay.HasValue || RoomState == RelayRoomState.Connecting;
            NewSession();
            CurrentRoomCredentials = null;
            CurrentCredentials = null;
            CurrentRoom = null;
            LastInviteCode = null;
            relayTransport.roomId = null;
            relayTransport.peerToken = null;
            RoomState = RelayRoomState.Offline;
            if (!stopping)
            {
                stopping = true;
                try
                {
                    if (NetworkServer.active && NetworkClient.active) StopHost();
                    else if (NetworkServer.active) StopServer();
                    else if (NetworkClient.active) StopClient();
                }
                finally { stopping = false; }
            }
            if (relay.HasValue) RelaySession.Leave(relay.Value);
            if (hadSession) RoomEvents.RaiseRoomClosed(reason);
            if (credentials == null) return;
            try
            {
                if (IsOwner(credentials))
                    await LobbyApi.DeleteRoomAsync(credentials.roomId, credentials.ownerToken, cancellationToken);
                else
                    await LobbyApi.LeaveRoomAsync(credentials.roomId, credentials.memberId, credentials.memberToken, cancellationToken);
            }
            catch (RelayApiException exception) when (exception.StatusCode == 403 || exception.StatusCode == 404)
            {
                // UDP Leave may already have removed the allocation.
            }
            catch (Exception exception)
            {
                if (!destroyed) RoomEvents.RaiseError(exception);
            }
        }

        async Task ReleaseAllocationAsync(RelayRoomCredentials credentials)
        {
            if (credentials == null || !credentials.IsValid) return;
            RelaySession.Leave(credentials.ToTransportCredentials());
            try
            {
                if (IsOwner(credentials))
                    await LobbyApi.DeleteRoomAsync(credentials.roomId, credentials.ownerToken);
                else
                    await LobbyApi.LeaveRoomAsync(credentials.roomId, credentials.memberId, credentials.memberToken);
            }
            catch { /* A stale allocation may already have been revoked by UDP Leave. */ }
        }

        public async void StartRelayHost()
        {
            try { await StartRelayHostAsync(); }
            catch (Exception exception) { Debug.LogError(exception.Message); }
        }
        public async void StartRelayClient(string inviteCode)
        {
            try { await StartRelayClientAsync(inviteCode); }
            catch (Exception exception) { Debug.LogError(exception.Message); }
        }
        public Task<RelayCredentials> StartRelayHostAsync(CancellationToken cancellationToken = default) =>
            OpenLegacyAsync(null, cancellationToken);
        public Task<RelayCredentials> StartRelayClientAsync(string inviteCode, CancellationToken cancellationToken = default) =>
            OpenLegacyAsync(inviteCode, cancellationToken);

        async Task<RelayCredentials> OpenLegacyAsync(string invite, CancellationToken cancellationToken)
        {
            int queuedVersion = sessionVersion;
            await operations.WaitAsync(cancellationToken);
            bool opening = false;
            int version = sessionVersion;
            try
            {
                if (queuedVersion != sessionVersion) throw new OperationCanceledException();
                EnsureIdle();
                NewSession();
                version = sessionVersion;
                opening = true;
                LastSessionError = null;
                RoomState = RelayRoomState.Connecting;
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
                RelayCredentials credentials;
                if (invite == null)
                    credentials = await RelaySession.CreateAsync(relayAddress, relayPort, relayRequestTimeoutMilliseconds, linked.Token);
                else
                {
                    if (!RelayCredentials.TryParseInvite(invite, out Guid room, out Guid token))
                        throw new ArgumentException("Invite must be '<room-guid>|<host-token-guid>'.", nameof(invite));
                    credentials = await RelaySession.JoinAsync(relayAddress, relayPort, room, token, relayRequestTimeoutMilliseconds, linked.Token);
                }
                if (linked.IsCancellationRequested || version != sessionVersion)
                {
                    RelaySession.Leave(credentials);
                    throw new OperationCanceledException(linked.Token);
                }
                ApplyCredentials(credentials);
                linked.Token.ThrowIfCancellationRequested();
                LastInviteCode = invite == null ? credentials.ToInviteCode() : null;
                if (invite == null) StartHost();
                else StartClient(credentials.ToUri());
                await WaitForNetworkAsync(invite == null, linked.Token);
                RoomState = RelayRoomState.Connected;
                return credentials;
            }
            catch (Exception exception)
            {
                if (opening && version == sessionVersion)
                {
                    LastSessionError = exception.Message;
                    await CleanupAsync("startup failed", CancellationToken.None);
                    RoomState = RelayRoomState.Error;
                }
                throw;
            }
            finally { operations.Release(); }
        }

        protected virtual void ApplyRoom(RelayRoomDetails room)
        {
            string previousSnapshot = CurrentRoom == null ? null : JsonUtility.ToJson(CurrentRoom);
            RelayRoomMember[] previous = CurrentRoom?.members ?? Array.Empty<RelayRoomMember>();
            CurrentRoom = room;
            if (CurrentRoomCredentials != null)
            {
                LastInviteCode = IsOwner(CurrentRoomCredentials) ? room?.inviteCode : null;
                RoomState = room?.room.started == true ? RelayRoomState.Started
                    : NetworkClient.isConnected ? RelayRoomState.Connected : RelayRoomState.Lobby;
            }
            RelayRoomMember[] current = room?.members ?? Array.Empty<RelayRoomMember>();
            foreach (RelayRoomMember member in current)
                if (!previous.Any(old => old.memberId == member.memberId)) RoomEvents.RaiseMemberJoined(member);
            foreach (RelayRoomMember member in previous)
                if (!current.Any(value => value.memberId == member.memberId)) RoomEvents.RaiseMemberLeft(member);
            if (previousSnapshot != (room == null ? null : JsonUtility.ToJson(room))) RoomEvents.RaiseRoomChanged(room);
        }
        void ApplyCredentials(RelayCredentials credentials)
        {
            CurrentCredentials = credentials;
            relayTransport.ApplyCredentials(credentials);
            transport = relayTransport;
        }
        void NewSession()
        {
            sessionVersion++;
            session.Cancel();
            session.Dispose();
            session = new CancellationTokenSource();
        }
        static bool IsOwner(RelayRoomCredentials credentials) => !string.IsNullOrEmpty(credentials?.ownerToken);
        RelayRoomCredentials RequireCredentials() => CurrentRoomCredentials ?? throw new InvalidOperationException("No lobby room is active.");
        void EnsureIdle()
        {
            if (destroyed) throw new ObjectDisposedException(nameof(RelayNetworkManager));
            if (CurrentCredentials.HasValue || NetworkClient.active || NetworkServer.active)
                throw new InvalidOperationException("Leave the room before creating or joining another.");
        }
        void EnsureRelayTransport()
        {
            if (relayTransport == null) relayTransport = transport as RelayKcpTransport;
            if (relayTransport == null) throw new InvalidOperationException("RelayKcpTransport required.");
        }
        async void ObserveCleanup(string reason)
        {
            try { await CleanupAsync(reason, CancellationToken.None); }
            catch (Exception exception) { if (!destroyed) RoomEvents.RaiseError(exception); }
        }
        void OnRelayFailed(string reason)
        {
            LastSessionError = reason;
            RoomEvents.RaiseError(new TimeoutException(reason));
            ObserveCleanup("relay unavailable");
        }
        public override void OnStopHost()
        {
            base.OnStopHost();
            CleanupFromStopCallback("host stopped");
        }
        public override void OnStopServer()
        {
            base.OnStopServer();
            CleanupFromStopCallback("server stopped");
        }
        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!NetworkServer.active) CleanupFromStopCallback("disconnected");
        }
        void CleanupFromStopCallback(string reason)
        {
            if (stopping) return;
            // Mirror is already tearing down the network in these callbacks.
            stopping = true;
            try { ObserveCleanup(reason); }
            finally { stopping = false; }
        }
        public override void OnClientError(TransportError error, string reason)
        {
            LastSessionError = reason;
            RoomEvents.RaiseError(new InvalidOperationException(reason));
        }
        public override void OnDestroy()
        {
            destroyed = true;
            if (relayTransport != null) relayTransport.RelayFailed -= OnRelayFailed;
            ObserveCleanup("destroyed");
            session.Cancel();
            session.Dispose();
            base.OnDestroy();
        }
        protected virtual void OnDisable()
        {
            if (!destroyed && (CurrentCredentials.HasValue || RoomState == RelayRoomState.Connecting))
                ObserveCleanup("disabled");
        }
    }
}
