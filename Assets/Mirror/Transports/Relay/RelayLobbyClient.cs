using System.Threading;
using System.Threading.Tasks;

namespace Mirror.Relay
{
    // The manager is the single owner of the session, including transport lifetime.
    public sealed class RelayLobbyClient
    {
        readonly RelayNetworkManager manager;
        public RelayLobbyApi Api => manager.LobbyApi;
        public RelayRoomCredentials Credentials => manager.CurrentRoomCredentials;
        public RelayRoomDetails Room => manager.CurrentRoom;
        public RelayRoomState State => manager.RoomState;

        public RelayLobbyClient(RelayNetworkManager manager) => this.manager = manager;
        public Task<RelayRoomCredentials> CreateRoomAsync(RelayCreateRoomRequest request, CancellationToken cancellationToken = default) =>
            manager.CreateRoomAsync(request, cancellationToken);
        public Task<RelayRoomCredentials> JoinRoomAsync(RelayJoinRoomRequest request, CancellationToken cancellationToken = default) =>
            manager.JoinRoomAsync(request, cancellationToken);
        public Task<RelayRoomDetails> RefreshAsync(CancellationToken cancellationToken = default) =>
            manager.RefreshRoomAsync(cancellationToken);
        public Task LeaveAsync(CancellationToken cancellationToken = default) => manager.LeaveRoomAsync(cancellationToken);
    }
}
