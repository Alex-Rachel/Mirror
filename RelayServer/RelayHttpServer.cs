using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Mirror.Relay.Server;

public sealed class RelayHttpServer : IDisposable
{
    readonly RelayRoomRegistry registry;
    readonly RelayUdpServer udp;
    readonly string url;
    readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    WebApplication? app;
    Task? runTask;

    public RelayHttpServer(RelayRoomRegistry registry, RelayUdpServer udp, string url)
    {
        this.registry = registry;
        this.udp = udp;
        this.url = url;
    }

    public void Start()
    {
        if (app != null)
            return;

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RelayHttpServer).Assembly.GetName().Name,
            Args = Array.Empty<string>()
        });
        builder.WebHost.UseUrls(url);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        app = builder.Build();
        MapRoutes(app);
        runTask = app.RunAsync();
    }

    public void Stop()
    {
        if (app == null)
            return;

        try
        {
            app.StopAsync().GetAwaiter().GetResult();
            runTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown is best effort.
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            app = null;
            runTask = null;
        }
    }

    public void Dispose() => Stop();

    void MapRoutes(WebApplication application)
    {
        application.MapGet("/health", () => Results.Ok(new RelayHealthResponse
        {
            status = "ok",
            roomCount = registry.RoomCount,
            memberCount = registry.MemberCount
        }));

        application.MapGet("/api/v1/rooms", (HttpRequest request) =>
        {
            int.TryParse(request.Query["pageSize"], out int pageSize);
            string? cursor = request.Query["cursor"];
            string? tag = request.Query["tag"];
            string? state = request.Query["state"];
            return Results.Ok(registry.ListRooms(pageSize, cursor, tag, state));
        });

        application.MapPost("/api/v1/rooms", (CreateRoomRequest request) =>
        {
            RoomOperationResult result = registry.CreateRoom(request);
            return result.Success && result.Credentials != null
                ? Results.Ok(CreateResponse(result))
                : Error(result);
        });

        application.MapGet("/api/v1/rooms/{roomId:guid}", (Guid roomId, HttpRequest request) =>
        {
            RoomDetails? room = registry.GetRoom(roomId);
            if (room == null)
                return Error("room_not_found", "Room was not found.", StatusCodes.Status404NotFound);

            string? token = BearerToken(request);
            if (!string.IsNullOrEmpty(token))
            {
                if (registry.AuthorizeOwner(roomId, token))
                    return Results.Ok(registry.GetRoom(roomId, true));

                if (Guid.TryParse(request.Query["memberId"], out Guid memberId) &&
                    registry.TryGetMember(roomId, memberId, token, out _, out _))
                    return Results.Ok(room);
                return Error("forbidden", "Member authorization required.", StatusCodes.Status403Forbidden);
            }

            room.members = Array.Empty<RoomMember>();
            return Results.Ok(room);
        });

        application.MapGet("/api/v1/rooms/{roomId:guid}/members", (Guid roomId, HttpRequest request) =>
        {
            RoomDetails? room = registry.GetRoom(roomId);
            if (room == null)
                return Error("room_not_found", "Room was not found.", StatusCodes.Status404NotFound);

            string? token = BearerToken(request);
            if (registry.AuthorizeOwner(roomId, token ?? string.Empty))
                return Results.Ok(new RoomMembersResponse { members = room.members });
            if (!Guid.TryParse(request.Query["memberId"], out Guid memberId) ||
                !registry.TryGetMember(roomId, memberId, token ?? string.Empty, out _, out _))
                return Error("forbidden", "Member authorization required.", StatusCodes.Status403Forbidden);
            return Results.Ok(new RoomMembersResponse { members = room.members });
        });

        application.MapPost("/api/v1/rooms:join", (JoinRoomRequest request) =>
        {
            RoomOperationResult result = registry.JoinRoom(request);
            return result.Success && result.Credentials != null
                ? Results.Ok(JoinResponse(result))
                : Error(result);
        });

        application.MapPatch("/api/v1/rooms/{roomId:guid}", (Guid roomId, RoomUpdateRequest request, HttpRequest httpRequest) =>
        {
            RoomOperationResult result = registry.UpdateRoom(roomId, BearerToken(httpRequest) ?? string.Empty, request);
            return result.Success ? Results.Ok(result.Room) : Error(result);
        });

        application.MapPost("/api/v1/rooms:start", (RoomActionRequest request, HttpRequest httpRequest) =>
        {
            RoomOperationResult result = registry.StartRoom(request.roomId, BearerToken(httpRequest) ?? string.Empty);
            return result.Success ? Results.Ok(result.Room) : Error(result);
        });

        application.MapPost("/api/v1/rooms:leave", (RoomActionRequest request) =>
        {
            string memberToken = request.memberToken ?? string.Empty;
            if (!registry.TryGetMember(request.roomId, request.memberId, memberToken, out _, out _))
                return Error("forbidden", "Member authorization required.", StatusCodes.Status403Forbidden);
            if (!udp.LeaveMember(request.roomId, request.memberId, memberToken))
                return Error("room_not_found", "Room was not found.", StatusCodes.Status404NotFound);
            return Results.Ok(registry.GetRoom(request.roomId) ?? new RoomDetails());
        });

        application.MapDelete("/api/v1/rooms/{roomId:guid}", (Guid roomId, HttpRequest request) =>
        {
            if (!registry.AuthorizeOwner(roomId, BearerToken(request) ?? string.Empty))
                return Error("forbidden", "Owner authorization required.", StatusCodes.Status403Forbidden);
            if (udp.CloseRoom(roomId))
            {
                return Results.NoContent();
            }
            return Error("room_not_found", "Room was not found.", StatusCodes.Status404NotFound);
        });

        application.MapPost("/api/v1/rooms/{roomId:guid}/members/{memberId:guid}:kick", (Guid roomId, Guid memberId, HttpRequest request) =>
        {
            string ownerToken = BearerToken(request) ?? string.Empty;
            if (!registry.AuthorizeOwner(roomId, ownerToken))
                return Error("forbidden", "Owner authorization required.", StatusCodes.Status403Forbidden);
            RoomDetails? details = registry.GetRoom(roomId);
            RoomMember? target = details?.members.FirstOrDefault(member => member.memberId == memberId);
            if (target == null || target.isHost)
                return Error("member_not_found", "Member was not found.", StatusCodes.Status404NotFound);

            registry.TryGetPeer(roomId, target.peerId, out EndPoint? endpoint, out Guid relayToken, out _);
            RoomOperationResult result = registry.KickMember(
                roomId,
                memberId,
                ownerToken,
                out int peerId,
                out _);
            if (result.Success)
            {
                udp.NotifyPeerKicked(roomId, peerId, endpoint, relayToken);
                return Results.Ok(result.Room);
            }
            return Error(result);
        });
    }

    CreateRoomResponse CreateResponse(RoomOperationResult result)
    {
        RelayRoomCredentialsInternal credentials = result.Credentials!;
        return new CreateRoomResponse
        {
            credentials = ToPublicCredentials(credentials),
            room = result.Room!
        };
    }

    JoinRoomResponse JoinResponse(RoomOperationResult result)
    {
        RelayRoomCredentialsInternal credentials = result.Credentials!;
        return new JoinRoomResponse
        {
            credentials = ToPublicCredentials(credentials),
            room = result.Room!
        };
    }

    RelayRoomCredentials ToPublicCredentials(RelayRoomCredentialsInternal credentials) =>
        new()
        {
            roomId = credentials.RoomId,
            memberId = credentials.MemberId,
            peerId = credentials.PeerId,
            relayAddress = registry.RelayAddress,
            relayPort = registry.RelayPort,
            relayToken = credentials.RelayToken.ToString("D"),
            memberToken = credentials.MemberToken.ToString("D"),
            ownerToken = credentials.OwnerToken == default ? string.Empty : credentials.OwnerToken.ToString("D"),
            inviteCode = credentials.InviteCode
        };

    static string? BearerToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var value))
            return null;
        const string prefix = "Bearer ";
        string header = value.ToString();
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : header.Trim();
    }

    static IResult Error(RoomOperationResult result) =>
        Error(result.Code, result.Message, result.Code switch
        {
            "room_not_found" or "member_not_found" => StatusCodes.Status404NotFound,
            "forbidden" or "invalid_invite" => StatusCodes.Status403Forbidden,
            "room_full" or "room_started" or "room_closed" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });

    static IResult Error(string code, string message, int statusCode) =>
        Results.Json(new RelayApiError { code = code, message = message }, statusCode: statusCode);

}
