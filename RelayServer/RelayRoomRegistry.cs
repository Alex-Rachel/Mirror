using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Mirror.Relay.Server;

public sealed class RelayRoomRegistry
{
    readonly ConcurrentDictionary<Guid, RoomRecord> rooms = new();
    readonly int maxRooms;
    readonly int maxMembersPerRoom;
    readonly ushort relayPort;
    readonly string relayAddress;

    public RelayRoomRegistry(
        string relayAddress = "127.0.0.1",
        ushort relayPort = 9000,
        int maxRooms = 10000,
        int maxMembersPerRoom = 32)
    {
        this.relayAddress = relayAddress;
        this.relayPort = relayPort;
        this.maxRooms = Math.Max(1, maxRooms);
        this.maxMembersPerRoom = Math.Max(1, maxMembersPerRoom);
    }

    public int RoomCount => rooms.Count;

    public int MemberCount => rooms.Values.Sum(room =>
    {
        lock (room.Gate)
            return room.Members.Count;
    });

    public string RelayAddress => relayAddress;
    public ushort RelayPort => relayPort;

    public RoomOperationResult CreateRoom(CreateRoomRequest request, bool legacy = false)
    {
        if (rooms.Count >= maxRooms)
            return Fail("room_limit", "Relay room limit reached.");

        if (!TryNormalizeRoomRequest(request, out string name, out int capacity, out RelayRoomVisibility visibility, out string[] tags, out string error))
            return Fail("invalid_room", error);
        if (capacity > maxMembersPerRoom || !Enum.IsDefined(visibility))
            return Fail("invalid_room", "Room exceeds relay capacity or has invalid visibility.");

        Guid roomId = NewGuid();
        Guid ownerToken = NewGuid();
        Guid hostMemberToken = NewGuid();
        Guid relayToken = NewGuid();
        Guid memberId = NewGuid();
        string inviteCode = visibility == RelayRoomVisibility.Private || legacy
            ? CreateInviteCode()
            : string.Empty;

        RoomRecord room = new()
        {
            RoomId = roomId,
            Name = name,
            Visibility = visibility,
            Joinable = request.joinable,
            Started = false,
            Capacity = capacity,
            Tags = tags,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
            HostMemberId = memberId,
            OwnerTokenHash = HashToken(ownerToken),
            LegacyJoinTokenHash = legacy ? HashToken(relayToken) : null,
            InviteCodeHash = string.IsNullOrEmpty(inviteCode) ? null : HashText(inviteCode),
            InviteCode = inviteCode
        };

        room.Members[memberId] = new MemberRecord
        {
            MemberId = memberId,
            DisplayName = NormalizeDisplayName(request.displayName, "Host"),
            PeerId = 1,
            IsHost = true,
            MemberTokenHash = HashToken(hostMemberToken),
            RelayTokenHash = HashToken(relayToken),
            JoinedAtUtc = room.CreatedAtUtc,
            LastSeenUtc = room.CreatedAtUtc,
            Status = RelayMemberStatus.Pending,
            RelayToken = relayToken,
            MemberToken = hostMemberToken
        };

        if (!rooms.TryAdd(roomId, room))
            return Fail("room_create_failed", "Could not allocate a unique room id.");

        return new RoomOperationResult
        {
            Success = true,
            Credentials = new RelayRoomCredentialsInternal
            {
                RoomId = roomId,
                MemberId = memberId,
                PeerId = 1,
                RelayToken = relayToken,
                MemberToken = hostMemberToken,
                OwnerToken = ownerToken,
                InviteCode = inviteCode
            },
            Room = ToDetails(room, true)
        };
    }

    public RoomOperationResult JoinRoom(JoinRoomRequest request)
    {
        if (!rooms.TryGetValue(request.roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");

        lock (room.Gate)
        {
            if (!room.Joinable || room.Started)
                return Fail("room_closed", "Room is no longer accepting members.");
            if (room.Members.Count >= room.Capacity)
                return Fail("room_full", "Room is full.");
            if (room.Visibility == RelayRoomVisibility.Private &&
                (string.IsNullOrWhiteSpace(request.inviteCode) ||
                 room.InviteCodeHash == null ||
                 !FixedEquals(room.InviteCodeHash, HashText(request.inviteCode))))
                return Fail("invalid_invite", "Invalid room invite code.");

            Guid memberId = NewGuid();
            Guid memberToken = NewGuid();
            Guid relayToken = NewGuid();
            MemberRecord member = new()
            {
                MemberId = memberId,
                DisplayName = NormalizeDisplayName(request.displayName, $"Player {room.NextPeerId}"),
                PeerId = room.NextPeerId++,
                IsHost = false,
                MemberTokenHash = HashToken(memberToken),
                RelayTokenHash = HashToken(relayToken),
                JoinedAtUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                Status = RelayMemberStatus.Pending,
                RelayToken = relayToken,
                MemberToken = memberToken
            };
            room.Members[memberId] = member;
            room.LastActivityUtc = DateTime.UtcNow;

            return new RoomOperationResult
            {
                Success = true,
                Credentials = new RelayRoomCredentialsInternal
                {
                    RoomId = room.RoomId,
                    MemberId = memberId,
                    PeerId = member.PeerId,
                    RelayToken = relayToken,
                    MemberToken = memberToken,
                    InviteCode = string.Empty
                },
                Room = ToDetails(room)
            };
        }
    }

    public RoomOperationResult JoinLegacyRoom(Guid roomId, Guid legacyJoinToken, EndPoint endpoint)
    {
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");

        lock (room.Gate)
        {
            if (room.LegacyJoinTokenHash == null ||
                !FixedEquals(room.LegacyJoinTokenHash, HashToken(legacyJoinToken)))
                return Fail("invalid_invite", "Invalid room or invite token.");
            if (room.Members.Count >= room.Capacity)
                return Fail("room_full", "Room is full.");

            Guid memberId = NewGuid();
            Guid memberToken = NewGuid();
            Guid relayToken = NewGuid();
            MemberRecord member = new()
            {
                MemberId = memberId,
                DisplayName = $"Player {room.NextPeerId}",
                PeerId = room.NextPeerId++,
                IsHost = false,
                MemberTokenHash = HashToken(memberToken),
                RelayTokenHash = HashToken(relayToken),
                JoinedAtUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                Status = RelayMemberStatus.Connected,
                Endpoint = endpoint,
                RelayToken = relayToken,
                MemberToken = memberToken
            };
            room.Members[memberId] = member;
            room.LastActivityUtc = DateTime.UtcNow;
            return new RoomOperationResult
            {
                Success = true,
                Credentials = new RelayRoomCredentialsInternal
                {
                    RoomId = room.RoomId,
                    MemberId = memberId,
                    PeerId = member.PeerId,
                    RelayToken = relayToken,
                    MemberToken = memberToken,
                    InviteCode = room.Visibility == RelayRoomVisibility.Private ? string.Empty : room.InviteCode
                },
                Room = ToDetails(room)
            };
        }
    }

    public RoomOperationResult CreateLegacyRoom(EndPoint endpoint)
    {
        RoomOperationResult result = CreateRoom(new CreateRoomRequest
        {
            name = "Relay Room",
            displayName = "Host",
            capacity = maxMembersPerRoom,
            visibility = RelayRoomVisibility.Private,
            joinable = true
        }, legacy: true);

        if (!result.Success || result.Credentials == null)
            return result;

        if (rooms.TryGetValue(result.Credentials.RoomId, out RoomRecord? room))
        {
            lock (room.Gate)
            {
                MemberRecord host = room.Members[room.HostMemberId];
                host.Endpoint = endpoint;
                host.Status = RelayMemberStatus.Connected;
            }
        }

        return result;
    }

    public RoomPage ListRooms(int pageSize, string? cursor, string? tag, string? state)
    {
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 50);
        int offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
            int.TryParse(cursor, out offset);

        List<RoomBrief> all = rooms.Values
            .Select(ToBrief)
            .Where(room => room.visibility == RelayRoomVisibility.Public &&
                           room.joinable &&
                           !room.started)
            .Where(room => string.IsNullOrWhiteSpace(tag) ||
                           room.tags.Any(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase)))
            .Where(room => string.IsNullOrWhiteSpace(state) ||
                           string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
            .OrderBy(room => room.roomId)
            .ToList();

        RoomBrief[] page = all.Skip(Math.Max(0, offset)).Take(pageSize).ToArray();
        int nextOffset = offset + page.Length;
        return new RoomPage
        {
            rooms = page,
            totalCount = all.Count,
            nextCursor = nextOffset < all.Count ? nextOffset.ToString() : null
        };
    }

    public RoomDetails? GetRoom(Guid roomId, bool owner = false)
    {
        return rooms.TryGetValue(roomId, out RoomRecord? room) ? ToDetails(room, owner) : null;
    }

    public bool TryGetMember(Guid roomId, Guid memberId, string token, out RoomMember member, out bool isOwner)
    {
        member = default!;
        isOwner = false;
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return false;

        lock (room.Gate)
        {
            if (!room.Members.TryGetValue(memberId, out MemberRecord? record))
                return false;
            if (!FixedEquals(record.MemberTokenHash, HashText(token)))
                return false;
            member = ToMember(record);
            isOwner = record.IsHost;
            return true;
        }
    }

    public bool AuthorizeOwner(Guid roomId, string token)
    {
        return rooms.TryGetValue(roomId, out RoomRecord? room) &&
               FixedEquals(room.OwnerTokenHash, HashText(token));
    }

    public RoomOperationResult UpdateRoom(Guid roomId, string ownerToken, RoomUpdateRequest request)
    {
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");
        lock (room.Gate)
        {
            if (!FixedEquals(room.OwnerTokenHash, HashText(ownerToken)))
                return Fail("forbidden", "Owner authorization required.");
            if (room.Started)
                return Fail("room_started", "Started rooms cannot be modified.");
            if (request.capacity.HasValue &&
                (request.capacity.Value < room.Members.Count || request.capacity.Value > maxMembersPerRoom))
                return Fail("invalid_capacity", "Capacity must contain current members and fit relay limits.");
            if (request.name != null)
                room.Name = NormalizeRoomName(request.name);
            if (request.capacity.HasValue)
                room.Capacity = request.capacity.Value;
            if (request.visibility.HasValue)
            {
                if (!Enum.IsDefined(request.visibility.Value))
                    return Fail("invalid_room", "Invalid visibility.");
                if (request.visibility.Value == RelayRoomVisibility.Private && room.Visibility != RelayRoomVisibility.Private)
                {
                    room.InviteCode = CreateInviteCode();
                    room.InviteCodeHash = HashText(room.InviteCode);
                }
                if (request.visibility.Value == RelayRoomVisibility.Public)
                {
                    room.InviteCode = string.Empty;
                    room.InviteCodeHash = null;
                }
                room.Visibility = request.visibility.Value;
            }
            if (request.joinable.HasValue)
                room.Joinable = request.joinable.Value;
            if (request.tags != null)
                room.Tags = NormalizeTags(request.tags);
            room.LastActivityUtc = DateTime.UtcNow;
            return Success(room);
        }
    }

    public RoomOperationResult StartRoom(Guid roomId, string ownerToken)
    {
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");
        lock (room.Gate)
        {
            if (!FixedEquals(room.OwnerTokenHash, HashText(ownerToken)))
                return Fail("forbidden", "Owner authorization required.");
            if (room.Started)
                return Success(room);
            room.Started = true;
            room.Joinable = false;
            room.LastActivityUtc = DateTime.UtcNow;
            return Success(room);
        }
    }

    public RoomOperationResult LeaveRoom(Guid roomId, Guid memberId, string memberToken, out int peerId, out List<int> remainingPeerIds)
    {
        return LeaveRoom(roomId, memberId, memberToken, out peerId, out remainingPeerIds, out _);
    }

    public RoomOperationResult LeaveRoom(
        Guid roomId,
        Guid memberId,
        string memberToken,
        out int peerId,
        out List<int> remainingPeerIds,
        out List<RelayPeerEndpoint> remainingPeers)
    {
        peerId = 0;
        remainingPeerIds = new List<int>();
        remainingPeers = new List<RelayPeerEndpoint>();
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");

        lock (room.Gate)
        {
            if (!room.Members.TryGetValue(memberId, out MemberRecord? member) ||
                !FixedEquals(member.MemberTokenHash, HashText(memberToken)))
                return Fail("forbidden", "Member authorization required.");

            int leavingPeerId = member.PeerId;
            peerId = leavingPeerId;
            remainingPeers = room.Members.Values
                .Where(item => item.PeerId != leavingPeerId)
                .Select(ToPeerEndpoint)
                .ToList();
            remainingPeerIds = remainingPeers.Select(item => item.PeerId).ToList();
            room.Members.Remove(memberId);
            room.LastActivityUtc = DateTime.UtcNow;
            if (member.IsHost)
            {
                rooms.TryRemove(roomId, out _);
                return new RoomOperationResult { Success = true };
            }
            return Success(room);
        }
    }

    public RoomOperationResult CloseRoom(Guid roomId, string ownerToken, out List<int> peerIds)
    {
        peerIds = new List<int>();
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");
        lock (room.Gate)
        {
            if (!FixedEquals(room.OwnerTokenHash, HashText(ownerToken)))
                return Fail("forbidden", "Owner authorization required.");
            peerIds = room.Members.Values.Select(member => member.PeerId).Where(peerId => peerId > 1).ToList();
            rooms.TryRemove(roomId, out _);
            return new RoomOperationResult { Success = true };
        }
    }

    public RoomOperationResult KickMember(Guid roomId, Guid memberId, string ownerToken, out int peerId, out List<int> remainingPeerIds)
    {
        peerId = 0;
        remainingPeerIds = new List<int>();
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return Fail("room_not_found", "Room was not found.");
        lock (room.Gate)
        {
            if (!FixedEquals(room.OwnerTokenHash, HashText(ownerToken)))
                return Fail("forbidden", "Owner authorization required.");
            if (!room.Members.TryGetValue(memberId, out MemberRecord? member) || member.IsHost)
                return Fail("member_not_found", "Member was not found.");
            peerId = member.PeerId;
            room.Members.Remove(memberId);
            remainingPeerIds = room.Members.Values.Select(item => item.PeerId).ToList();
            room.LastActivityUtc = DateTime.UtcNow;
            return Success(room);
        }
    }

    public bool TryAuthenticateRelay(Guid roomId, int peerId, Guid relayToken, EndPoint endpoint, out int targetPeerId, out bool isHost)
    {
        targetPeerId = 0;
        isHost = false;
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return false;
        lock (room.Gate)
        {
            MemberRecord? member = room.Members.Values.FirstOrDefault(item =>
                item.PeerId == peerId && FixedEquals(item.RelayTokenHash, HashToken(relayToken)));
            if (member == null || member.Status == RelayMemberStatus.Kicked)
                return false;
            member.Endpoint = endpoint;
            member.LastSeenUtc = DateTime.UtcNow;
            member.Status = RelayMemberStatus.Connected;
            room.LastActivityUtc = member.LastSeenUtc;
            targetPeerId = member.PeerId;
            isHost = member.IsHost;
            return true;
        }
    }

    public bool TryGetPeer(Guid roomId, int peerId, out EndPoint? endpoint, out Guid relayToken, out RelayMemberStatus status)
    {
        endpoint = null;
        relayToken = default;
        status = RelayMemberStatus.Disconnected;
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return false;
        lock (room.Gate)
        {
            MemberRecord? member = room.Members.Values.FirstOrDefault(item => item.PeerId == peerId);
            if (member == null)
                return false;
            endpoint = member.Endpoint;
            relayToken = member.RelayToken;
            status = member.Status;
            return true;
        }
    }

    public bool RemovePeer(Guid roomId, int peerId, out bool wasHost, out List<int> remainingPeerIds)
    {
        return RemovePeer(roomId, peerId, out wasHost, out remainingPeerIds, out _);
    }

    public bool RemovePeer(
        Guid roomId,
        int peerId,
        out bool wasHost,
        out List<int> remainingPeerIds,
        out List<RelayPeerEndpoint> remainingPeers)
    {
        wasHost = false;
        remainingPeerIds = new List<int>();
        remainingPeers = new List<RelayPeerEndpoint>();
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return false;
        lock (room.Gate)
        {
            MemberRecord? member = room.Members.Values.FirstOrDefault(item => item.PeerId == peerId);
            if (member == null)
                return false;
            wasHost = member.IsHost;
            room.Members.Remove(member.MemberId);
            remainingPeers = room.Members.Values.Select(ToPeerEndpoint).ToList();
            remainingPeerIds = remainingPeers.Select(item => item.PeerId).ToList();
            if (wasHost)
            {
                rooms.TryRemove(roomId, out _);
                return true;
            }
            room.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    public List<ExpiredPeer> ExpirePeers(TimeSpan peerTimeout)
    {
        DateTime cutoff = DateTime.UtcNow - peerTimeout;
        List<ExpiredPeer> expired = new();
        foreach (RoomRecord room in rooms.Values)
        {
            lock (room.Gate)
            {
                foreach (MemberRecord member in room.Members.Values.Where(item => item.LastSeenUtc < cutoff).ToList())
                {
                    bool wasHost = member.IsHost;
                    room.Members.Remove(member.MemberId);
                    List<RelayPeerEndpoint> remaining = room.Members.Values
                        .Select(ToPeerEndpoint)
                        .ToList();
                    expired.Add(new ExpiredPeer(room.RoomId, member.PeerId, wasHost, remaining));
                    if (wasHost)
                    {
                        room.Joinable = false;
                        rooms.TryRemove(room.RoomId, out _);
                        break;
                    }
                }
            }
        }
        return expired;
    }

    public RoomOperationResult? GetSuccessForRoom(Guid roomId)
    {
        return rooms.TryGetValue(roomId, out RoomRecord? room) ? Success(room) : null;
    }

    public RelayRoomCredentialsInternal? GetHostCredentials(Guid roomId, string ownerToken)
    {
        if (!rooms.TryGetValue(roomId, out RoomRecord? room))
            return null;
        lock (room.Gate)
        {
            if (!FixedEquals(room.OwnerTokenHash, HashText(ownerToken)))
                return null;
            MemberRecord host = room.Members[room.HostMemberId];
            return new RelayRoomCredentialsInternal
            {
                RoomId = room.RoomId,
                MemberId = host.MemberId,
                PeerId = host.PeerId,
                RelayToken = host.RelayToken,
                MemberToken = host.MemberToken,
                OwnerToken = default,
                InviteCode = room.InviteCode
            };
        }
    }

    RoomOperationResult Success(RoomRecord room) =>
        new() { Success = true, Room = ToDetails(room, true) };

    static RoomOperationResult Fail(string code, string message) =>
        new() { Success = false, Code = code, Message = message };

    static bool TryNormalizeRoomRequest(
        CreateRoomRequest request,
        out string name,
        out int capacity,
        out RelayRoomVisibility visibility,
        out string[] tags,
        out string error)
    {
        name = NormalizeRoomName(request.name);
        capacity = request.capacity <= 0 ? 8 : request.capacity;
        visibility = request.visibility;
        tags = NormalizeTags(request.tags);
        error = string.Empty;
        if (name.Length == 0)
        {
            error = "Room name is required.";
            return false;
        }
        if (capacity is < 1 or > 256)
        {
            error = "Capacity must be between 1 and 256.";
            return false;
        }
        return true;
    }

    static string NormalizeRoomName(string? value)
    {
        string name = (value ?? string.Empty).Trim();
        return name.Length > 64 ? name[..64] : name;
    }

    static string NormalizeDisplayName(string? value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return name.Length > 32 ? name[..32] : name;
    }

    static string[] NormalizeTags(string[]? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(value => value.Length > 32 ? value[..32] : value)
            .ToArray();

    RoomBrief ToBrief(RoomRecord room)
    {
        lock (room.Gate)
        {
            return new RoomBrief
            {
                roomId = room.RoomId,
                name = room.Name,
                visibility = room.Visibility,
                joinable = room.Joinable,
                started = room.Started,
                playerCount = room.Members.Count,
                capacity = room.Capacity,
                tags = room.Tags.ToArray()
            };
        }
    }

    RoomDetails ToDetails(RoomRecord room, bool owner = false)
    {
        lock (room.Gate)
        {
            return new RoomDetails
            {
                inviteCode = owner ? room.InviteCode : null,
                room = new RoomBrief
                {
                    roomId = room.RoomId,
                    name = room.Name,
                    visibility = room.Visibility,
                    joinable = room.Joinable,
                    started = room.Started,
                    playerCount = room.Members.Count,
                    capacity = room.Capacity,
                    tags = room.Tags.ToArray()
                },
                hostMemberId = room.HostMemberId,
                createdAtUtc = room.CreatedAtUtc,
                lastActivityUtc = room.LastActivityUtc,
                members = room.Members.Values.Select(ToMember).OrderBy(member => member.peerId).ToArray()
            };
        }
    }

    static RoomMember ToMember(MemberRecord member) =>
        new()
        {
            memberId = member.MemberId,
            displayName = member.DisplayName,
            peerId = member.PeerId,
            isHost = member.IsHost,
            status = member.Status,
            joinedAtUtc = member.JoinedAtUtc
        };

    static RelayPeerEndpoint ToPeerEndpoint(MemberRecord member) =>
        new(member.PeerId, member.Endpoint, member.RelayToken);

    static Guid NewGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }

    static string CreateInviteCode()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }

    static byte[] HashToken(Guid token) => HashText(token.ToString("D"));

    static byte[] HashText(string? value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));

    static bool FixedEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    sealed class RoomRecord
    {
        public readonly object Gate = new();
        public Guid RoomId;
        public string Name = string.Empty;
        public RelayRoomVisibility Visibility;
        public bool Joinable;
        public bool Started;
        public int Capacity;
        public string[] Tags = Array.Empty<string>();
        public DateTime CreatedAtUtc;
        public DateTime LastActivityUtc;
        public Guid HostMemberId;
        public byte[] OwnerTokenHash = Array.Empty<byte>();
        public byte[]? LegacyJoinTokenHash;
        public byte[]? InviteCodeHash;
        public string InviteCode = string.Empty;
        public int NextPeerId = 2;
        public Dictionary<Guid, MemberRecord> Members = new();
    }

    sealed class MemberRecord
    {
        public Guid MemberId;
        public string DisplayName = string.Empty;
        public int PeerId;
        public bool IsHost;
        public byte[] MemberTokenHash = Array.Empty<byte>();
        public byte[] RelayTokenHash = Array.Empty<byte>();
        public DateTime JoinedAtUtc;
        public DateTime LastSeenUtc;
        public RelayMemberStatus Status;
        public EndPoint? Endpoint;
        public Guid RelayToken;
        public Guid MemberToken;
    }
}

public sealed record RelayPeerEndpoint(int PeerId, EndPoint? Endpoint, Guid RelayToken);

public sealed record ExpiredPeer(
    Guid RoomId,
    int PeerId,
    bool WasHost,
    List<RelayPeerEndpoint> RemainingPeers);
