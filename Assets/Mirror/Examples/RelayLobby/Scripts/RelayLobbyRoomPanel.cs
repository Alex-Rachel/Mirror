using Mirror.Relay;
using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyRoomPanel : MonoBehaviour
    {
        public Text roomName, roomState, inviteCode;
        public InputField updateName, updateCapacity, updateTags;
        public Toggle updatePrivateRoom, updateJoinable;
        public Button updateButton, startButton, closeButton, leaveButton, copyButton;
        public GameObject ownerFields;
        string editingRoom;

        public void ResetEdit() => editingRoom = null;
        public void Render(RelayRoomDetails details, string code, bool owner)
        {
            RelayRoomBrief room = details?.room;
            roomName.text = room == null ? "No active room" : room.name;
            roomState.text = room == null ? "Create or join a room" :
                $"{room.playerCount}/{room.capacity}  |  {(room.started ? "PLAYING" : "WAITING FOR HOST")}";
            inviteCode.text = room == null ? "" : $"ID: {room.roomId}" +
                (string.IsNullOrEmpty(code) ? "" : $"\nInvite: {code}");
            // Keep all unsubmitted fields, including toggles, intact across polls.
            if (room == null) editingRoom = null;
            else if (editingRoom != room.roomId)
            {
                editingRoom = room.roomId;
                updateName.SetTextWithoutNotify(room.name);
                updateCapacity.SetTextWithoutNotify(room.capacity.ToString());
                updateTags.SetTextWithoutNotify(string.Join(",", room.tags ?? new string[0]));
                updatePrivateRoom.SetIsOnWithoutNotify(room.visibility == RelayRoomVisibility.Private);
                updateJoinable.SetIsOnWithoutNotify(room.joinable);
            }
            ownerFields.SetActive(owner && room != null && !room.started);
            updateButton.gameObject.SetActive(owner && room != null && !room.started);
            startButton.gameObject.SetActive(owner && room != null && !room.started);
            closeButton.gameObject.SetActive(owner && room != null);
            leaveButton.gameObject.SetActive(!owner && room != null);
            copyButton.interactable = room != null;
        }

        public RelayRoomUpdateRequest ToUpdateRequest()
        {
            int.TryParse(updateCapacity.text, out int capacity);
            return new RelayRoomUpdateRequest
            {
                name = updateName.text, capacity = Mathf.Clamp(capacity, 1, 32),
                tags = string.IsNullOrWhiteSpace(updateTags.text) ? new string[0] : updateTags.text.Split(','),
                visibility = updatePrivateRoom.isOn ? RelayRoomVisibility.Private : RelayRoomVisibility.Public,
                joinable = updateJoinable.isOn
            };
        }
    }
}
