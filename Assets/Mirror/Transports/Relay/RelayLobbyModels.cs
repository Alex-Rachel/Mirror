using System;

namespace Mirror.Relay
{
    public enum RelayRoomVisibility
    {
        Public = 0,
        Private = 1
    }

    public enum RelayMemberStatus
    {
        Pending = 0,
        Connected = 1,
        Disconnected = 2,
        Kicked = 3
    }

    [Serializable]
    public class RelayRoomBrief
    {
        public string roomId;
        public string name;
        public RelayRoomVisibility visibility;
        public bool joinable;
        public bool started;
        public int playerCount;
        public int capacity;
        public string[] tags;

        public int AvailableSlots => Math.Max(0, capacity - playerCount);
    }

    [Serializable]
    public class RelayRoomMember
    {
        public string memberId;
        public string displayName;
        public int peerId;
        public bool isHost;
        public RelayMemberStatus status;
        public string joinedAtUtc;
    }

    [Serializable]
    public class RelayRoomDetails
    {
        public string inviteCode;
        public RelayRoomBrief room;
        public string hostMemberId;
        public string createdAtUtc;
        public string lastActivityUtc;
        public RelayRoomMember[] members;
    }

    [Serializable]
    public class RelayRoomPage
    {
        public RelayRoomBrief[] rooms;
        public string nextCursor;
        public int totalCount;
    }

    [Serializable]
    public class RelayRoomMembersResponse
    {
        public RelayRoomMember[] members;
    }

    [Serializable]
    public class RelayCreateRoomRequest
    {
        public string name;
        public string displayName;
        public int capacity = 8;
        public RelayRoomVisibility visibility = RelayRoomVisibility.Public;
        public bool joinable = true;
        public string[] tags = new string[0];
    }

    [Serializable]
    public class RelayJoinRoomRequest
    {
        public string roomId;
        public string inviteCode;
        public string displayName;
    }

    [Serializable]
    public class RelayRoomUpdateRequest
    {
        public string name;
        public int capacity;
        public RelayRoomVisibility visibility;
        public bool joinable;
        public string[] tags = new string[0];
    }

    [Serializable]
    public class RelayRoomActionRequest
    {
        public string roomId;
        public string memberId;
        public string memberToken;
    }

    [Serializable]
    public class RelayRoomCredentials
    {
        public string roomId;
        public string memberId;
        public int peerId;
        public string relayAddress;
        public ushort relayPort;
        public string relayToken;
        public string memberToken;
        public string ownerToken;
        public string inviteCode;

        public bool IsValid =>
            Guid.TryParse(roomId, out Guid room) && room != Guid.Empty &&
            Guid.TryParse(memberId, out Guid member) && member != Guid.Empty &&
            Guid.TryParse(relayToken, out Guid token) && token != Guid.Empty &&
            Guid.TryParse(memberToken, out Guid auth) && auth != Guid.Empty &&
            peerId > 0 && !string.IsNullOrWhiteSpace(relayAddress) && relayPort > 0;

        public RelayCredentials ToTransportCredentials()
        {
            return new RelayCredentials
            {
                relayAddress = relayAddress,
                relayPort = relayPort,
                sessionId = Guid.Parse(roomId),
                token = Guid.Parse(relayToken),
                peerId = peerId
            };
        }

        public Uri ToUri()
        {
            return ToTransportCredentials().ToUri();
        }
    }

    [Serializable]
    public class RelayCreateRoomResponse
    {
        public RelayRoomCredentials credentials;
        public RelayRoomDetails room;
    }

    [Serializable]
    public class RelayJoinRoomResponse
    {
        public RelayRoomCredentials credentials;
        public RelayRoomDetails room;
    }

    [Serializable]
    public class RelayApiError
    {
        public string code;
        public string message;
    }

    [Serializable]
    public class RelayHealthResponse
    {
        public string status;
        public int roomCount;
        public int memberCount;
    }
}
