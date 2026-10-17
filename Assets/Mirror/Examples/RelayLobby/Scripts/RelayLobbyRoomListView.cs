using System;
using System.Collections.Generic;
using Mirror.Relay;
using UnityEngine;

namespace Mirror.Examples.RelayLobby
{
    public sealed class RelayLobbyRoomListView : MonoBehaviour
    {
        public Transform content;
        public RelayLobbyRoomEntry entryPrefab;
        readonly List<RelayLobbyRoomEntry> entries = new List<RelayLobbyRoomEntry>();
        public void SetRooms(RelayRoomBrief[] rooms, Action<RelayRoomBrief> select)
        {
            rooms = rooms ?? Array.Empty<RelayRoomBrief>();
            while (entries.Count < rooms.Length) entries.Add(Instantiate(entryPrefab, content));
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].gameObject.SetActive(i < rooms.Length);
                if (i < rooms.Length) entries[i].SetRoom(rooms[i], select);
            }
        }
    }
}
