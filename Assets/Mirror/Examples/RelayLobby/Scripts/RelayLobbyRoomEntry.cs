using System;
using Mirror.Relay;
using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyRoomEntry : MonoBehaviour
    {
        public Text roomName;
        public Text playerCount;
        public Text tags;
        public Button joinButton;

        RelayRoomBrief room;
        Action<RelayRoomBrief> onJoin;

        public void SetRoom(RelayRoomBrief value, Action<RelayRoomBrief> join)
        {
            room = value;
            onJoin = join;
            if (roomName != null)
                roomName.text = value.name;
            if (playerCount != null)
                playerCount.text = $"{value.playerCount}/{value.capacity}";
            if (tags != null)
                tags.text = value.tags == null ? string.Empty : string.Join(", ", value.tags);
            if (joinButton != null)
            {
                joinButton.interactable = value.joinable && !value.started && value.AvailableSlots > 0;
                joinButton.onClick.RemoveAllListeners();
                joinButton.onClick.AddListener(() => onJoin?.Invoke(room));
            }
        }
    }
}
