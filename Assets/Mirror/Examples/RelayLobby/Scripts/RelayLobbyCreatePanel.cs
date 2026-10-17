using Mirror.Relay;
using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyCreatePanel : MonoBehaviour
    {
        public InputField roomName;
        public InputField displayName;
        public InputField tags;
        public InputField capacity;
        public Toggle privateRoom;
        public Toggle joinable;
        public Button createButton;

        public RelayCreateRoomRequest ToRequest()
        {
            int.TryParse(capacity == null ? "8" : capacity.text, out int parsedCapacity);
            return new RelayCreateRoomRequest
            {
                name = roomName == null ? "Relay Room" : roomName.text,
                displayName = displayName == null ? "Host" : displayName.text,
                capacity = Mathf.Clamp(parsedCapacity <= 0 ? 8 : parsedCapacity, 1, 32),
                visibility = privateRoom != null && privateRoom.isOn
                    ? RelayRoomVisibility.Private
                    : RelayRoomVisibility.Public,
                joinable = joinable == null || joinable.isOn,
                tags = tags == null || string.IsNullOrWhiteSpace(tags.text)
                    ? new string[0]
                    : tags.text.Split(',')
            };
        }
    }
}
