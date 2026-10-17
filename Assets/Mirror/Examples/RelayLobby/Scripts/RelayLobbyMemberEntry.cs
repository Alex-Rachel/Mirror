using System;
using Mirror.Relay;
using UnityEngine;
using UnityEngine.UI;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyMemberEntry : MonoBehaviour
    {
        public Text memberName;
        public Text status;
        public Button kickButton;

        public void SetMember(RelayRoomMember member, bool canKick, Action<string> kick)
        {
            if (memberName != null)
                memberName.text = member.isHost ? $"{member.displayName} [Host]" : member.displayName;
            if (status != null)
                status.text = member.status.ToString();
            if (kickButton != null)
            {
                kickButton.gameObject.SetActive(canKick && !member.isHost);
                kickButton.onClick.RemoveAllListeners();
                kickButton.onClick.AddListener(() => kick?.Invoke(member.memberId));
            }
        }
    }
}
