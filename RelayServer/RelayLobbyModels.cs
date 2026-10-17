using System.Text.Json.Serialization;

namespace Mirror.Relay.Server;

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

public sealed class CreateRoomRequest
{
    public string? name { get; set; }
    public string? displayName { get; set; }
    public int capacity { get; set; } = 8;
    public RelayRoomVisibility visibility { get; set; } = RelayRoomVisibility.Public;
    public bool joinable { get; set; } = true;
    public string[]? tags { get; set; }
}

public sealed class JoinRoomRequest
{
    public Guid roomId { get; set; }
    public string? inviteCode { get; set; }
    public string? displayName { get; set; }
}

public sealed class RoomUpdateRequest
{
    public string? name { get; set; }
    public int? capacity { get; set; }
    public RelayRoomVisibility? visibility { get; set; }
    public bool? joinable { get; set; }
    public string[]? tags { get; set; }
}

public sealed class RoomActionRequest
{
    public Guid roomId { get; set; }
    public Guid memberId { get; set; }
    public string? memberToken { get; set; }
}

public sealed class RoomBrief
{
    public Guid roomId { get; set; }
    public string name { get; set; } = string.Empty;
    public RelayRoomVisibility visibility { get; set; }
    public bool joinable { get; set; }
    public bool started { get; set; }
    public int playerCount { get; set; }
    public int capacity { get; set; }
    public string[] tags { get; set; } = Array.Empty<string>();
}

public sealed class RoomMember
{
    public Guid memberId { get; set; }
    public string displayName { get; set; } = string.Empty;
    public int peerId { get; set; }
    public bool isHost { get; set; }
    public RelayMemberStatus status { get; set; }
    public DateTime joinedAtUtc { get; set; }
}

public sealed class RoomDetails
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? inviteCode { get; set; }
    public RoomBrief room { get; set; } = new();
    public Guid hostMemberId { get; set; }
    public DateTime createdAtUtc { get; set; }
    public DateTime lastActivityUtc { get; set; }
    public RoomMember[] members { get; set; } = Array.Empty<RoomMember>();
}

public sealed class RoomPage
{
    public RoomBrief[] rooms { get; set; } = Array.Empty<RoomBrief>();
    public string? nextCursor { get; set; }
    public int totalCount { get; set; }
}

public sealed class RoomMembersResponse
{
    public RoomMember[] members { get; set; } = Array.Empty<RoomMember>();
}

public sealed class RelayRoomCredentials
{
    public Guid roomId { get; set; }
    public Guid memberId { get; set; }
    public int peerId { get; set; }
    public string relayAddress { get; set; } = "127.0.0.1";
    public ushort relayPort { get; set; }
    public string relayToken { get; set; } = string.Empty;
    public string memberToken { get; set; } = string.Empty;
    public string ownerToken { get; set; } = string.Empty;
    public string inviteCode { get; set; } = string.Empty;
}

public sealed class CreateRoomResponse
{
    public RelayRoomCredentials credentials { get; set; } = new();
    public RoomDetails room { get; set; } = new();
}

public sealed class JoinRoomResponse
{
    public RelayRoomCredentials credentials { get; set; } = new();
    public RoomDetails room { get; set; } = new();
}

public sealed class RelayApiError
{
    public string code { get; set; } = "error";
    public string message { get; set; } = string.Empty;
}

public sealed class RelayHealthResponse
{
    public string status { get; set; } = "ok";
    public int roomCount { get; set; }
    public int memberCount { get; set; }
}

public sealed class RelayRoomCredentialsInternal
{
    public Guid RoomId { get; init; }
    public Guid MemberId { get; init; }
    public int PeerId { get; init; }
    public Guid RelayToken { get; init; }
    public Guid MemberToken { get; init; }
    public Guid OwnerToken { get; init; }
    public string InviteCode { get; init; } = string.Empty;
}

public sealed class RoomOperationResult
{
    public bool Success { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public RelayRoomCredentialsInternal? Credentials { get; init; }
    public RoomDetails? Room { get; init; }
}
