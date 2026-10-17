using System;
using System.Collections.Generic;
using Mirror.Relay;
using UnityEngine;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyMemberListView : MonoBehaviour
    {
        public Transform content;
        public RelayLobbyMemberEntry entryPrefab;
        readonly List<RelayLobbyMemberEntry> entries = new List<RelayLobbyMemberEntry>();
        public void SetMembers(RelayRoomMember[] members, bool owner, Action<string> kick)
        {
            members = members ?? Array.Empty<RelayRoomMember>();
            while (entries.Count < members.Length) entries.Add(Instantiate(entryPrefab, content));
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].gameObject.SetActive(i < members.Length);
                if (i < members.Length) entries[i].SetMember(members[i], owner, kick);
            }
        }
    }
}
