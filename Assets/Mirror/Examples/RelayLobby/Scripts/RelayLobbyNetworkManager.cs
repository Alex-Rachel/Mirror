using System;
using Mirror.Relay;
using UnityEngine;

namespace Mirror.Examples.RelayLobby
{
    [AddComponentMenu("Network/Examples/Relay Lobby Network Manager")]
    public sealed class RelayLobbyNetworkManager : RelayNetworkManager
    {
        public RelayLobbyUiController uiController;

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            base.OnServerAddPlayer(conn);
            AssignPlayers();
            RefreshNames();
        }

        protected override void ApplyRoom(RelayRoomDetails room)
        {
            base.ApplyRoom(room);
            if (NetworkServer.active) AssignPlayers();
        }

        void AssignPlayers()
        {
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                if (conn.identity == null) continue;
                RelayLobbyPlayer player = conn.identity.GetComponent<RelayLobbyPlayer>();
                if (player == null) continue;
                player.roomStarted = CurrentRoom?.room.started == true;
                int peer = conn.connectionId == 0 ? 1 : conn.connectionId;
                foreach (RelayRoomMember member in CurrentRoom?.members ?? Array.Empty<RelayRoomMember>())
                    if (member.peerId == peer) player.SetServerDisplayName(member.displayName);
            }
        }

        async void RefreshNames()
        {
            try { await RefreshRoomAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { uiController?.SetMessage(exception.Message); }
        }
    }
}
