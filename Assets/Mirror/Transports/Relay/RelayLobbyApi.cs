using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Mirror.Relay
{
    public sealed class RelayApiException : Exception
    {
        public long StatusCode { get; }
        public string Code { get; }
        public RelayApiException(long statusCode, string code, string message)
            : base($"HTTP {statusCode} ({code}): {message}")
        {
            StatusCode = statusCode;
            Code = code;
        }
    }

    public sealed class RelayLobbyApi
    {
        [Serializable]
        sealed class StartRequest { public string roomId; }
        readonly string baseUrl;

        public RelayLobbyApi(string baseUrl)
        {
            this.baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        }

        public Task<RelayRoomPage> ListRoomsAsync(
            int pageSize = 50,
            string cursor = null,
            string tag = null,
            string state = null,
            CancellationToken cancellationToken = default)
        {
            string query = $"pageSize={Mathf.Clamp(pageSize, 1, 50)}";
            if (!string.IsNullOrEmpty(cursor))
                query += $"&cursor={UnityWebRequest.EscapeURL(cursor)}";
            if (!string.IsNullOrEmpty(tag))
                query += $"&tag={UnityWebRequest.EscapeURL(tag)}";
            if (!string.IsNullOrEmpty(state))
                query += $"&state={UnityWebRequest.EscapeURL(state)}";
            return SendAsync<RelayRoomPage>("GET", $"/api/v1/rooms?{query}", null, null, cancellationToken);
        }

        public Task<RelayCreateRoomResponse> CreateRoomAsync(RelayCreateRoomRequest request, CancellationToken cancellationToken = default) =>
            SendAsync<RelayCreateRoomResponse>("POST", "/api/v1/rooms", request, null, cancellationToken);

        public Task<RelayJoinRoomResponse> JoinRoomAsync(RelayJoinRoomRequest request, CancellationToken cancellationToken = default) =>
            SendAsync<RelayJoinRoomResponse>("POST", "/api/v1/rooms:join", request, null, cancellationToken);

        public Task<RelayRoomDetails> GetRoomAsync(string roomId, string memberId = null, string memberToken = null, CancellationToken cancellationToken = default)
        {
            string path = $"/api/v1/rooms/{UnityWebRequest.EscapeURL(roomId)}";
            if (!string.IsNullOrEmpty(memberId))
                path += $"?memberId={UnityWebRequest.EscapeURL(memberId)}";
            return SendAsync<RelayRoomDetails>("GET", path, null, memberToken, cancellationToken);
        }

        public async Task<RelayRoomMember[]> GetMembersAsync(string roomId, string memberId, string memberToken, CancellationToken cancellationToken = default)
        {
            RelayRoomMembersResponse response = await SendAsync<RelayRoomMembersResponse>(
                "GET",
                $"/api/v1/rooms/{UnityWebRequest.EscapeURL(roomId)}/members?memberId={UnityWebRequest.EscapeURL(memberId)}",
                null,
                memberToken, cancellationToken);
            return response.members ?? new RelayRoomMember[0];
        }

        public Task<RelayRoomDetails> UpdateRoomAsync(string roomId, RelayRoomUpdateRequest request, string ownerToken, CancellationToken cancellationToken = default) =>
            SendAsync<RelayRoomDetails>(
                "PATCH",
                $"/api/v1/rooms/{UnityWebRequest.EscapeURL(roomId)}",
                request,
                ownerToken, cancellationToken);

        public Task<RelayRoomDetails> StartRoomAsync(string roomId, string ownerToken, CancellationToken cancellationToken = default) =>
            SendAsync<RelayRoomDetails>(
                "POST",
                "/api/v1/rooms:start",
                new StartRequest { roomId = roomId },
                ownerToken, cancellationToken);

        public Task<RelayRoomDetails> LeaveRoomAsync(string roomId, string memberId, string memberToken, CancellationToken cancellationToken = default) =>
            SendAsync<RelayRoomDetails>(
                "POST",
                "/api/v1/rooms:leave",
                new RelayRoomActionRequest
                {
                    roomId = roomId,
                    memberId = memberId,
                    memberToken = memberToken
                },
                null, cancellationToken);

        public Task DeleteRoomAsync(string roomId, string ownerToken, CancellationToken cancellationToken = default) =>
            SendNoContentAsync("DELETE", $"/api/v1/rooms/{UnityWebRequest.EscapeURL(roomId)}", ownerToken, cancellationToken);

        public Task<RelayRoomDetails> KickMemberAsync(string roomId, string memberId, string ownerToken, CancellationToken cancellationToken = default) =>
            SendAsync<RelayRoomDetails>(
                "POST",
                $"/api/v1/rooms/{UnityWebRequest.EscapeURL(roomId)}/members/{UnityWebRequest.EscapeURL(memberId)}:kick",
                null,
                ownerToken, cancellationToken);

        public Task<RelayHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            SendAsync<RelayHealthResponse>("GET", "/health", null, null, cancellationToken);

        async Task<T> SendAsync<T>(string method, string path, object body, string bearerToken, CancellationToken cancellationToken)
        {
            string responseText = await SendRawAsync(method, path, body, bearerToken, cancellationToken);
            T value = JsonUtility.FromJson<T>(responseText);
            if (value == null)
                throw new InvalidOperationException($"Relay API returned an empty {typeof(T).Name} response.");
            return value;
        }

        async Task SendNoContentAsync(string method, string path, string bearerToken, CancellationToken cancellationToken)
        {
            await SendRawAsync(method, path, null, bearerToken, cancellationToken);
        }

        async Task<string> SendRawAsync(string method, string path, object body, string bearerToken, CancellationToken cancellationToken)
        {
            string url = baseUrl + path;
            using UnityWebRequest request = new UnityWebRequest(url, method);
            request.timeout = 10;
            request.downloadHandler = new DownloadHandlerBuffer();
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(bearerToken))
                request.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Delay(20, cancellationToken);
                }
                    bool failed =
#if UNITY_2020_3_OR_NEWER
                        request.result != UnityWebRequest.Result.Success;
#else
                        request.isNetworkError || request.isHttpError;
#endif
                    if (failed || request.responseCode < 200 || request.responseCode >= 300)
                    {
                        string code = "transport_error";
                        string message = request.error;
                        if (string.IsNullOrEmpty(message))
                            message = $"Relay API returned HTTP {request.responseCode}.";
                        if (!string.IsNullOrEmpty(request.downloadHandler.text))
                        {
                            try
                            {
                                RelayApiError error = JsonUtility.FromJson<RelayApiError>(request.downloadHandler.text);
                                if (error != null && !string.IsNullOrEmpty(error.message))
                                {
                                    code = error.code;
                                    message = error.message;
                                }
                            }
                            catch
                            {
                                // Keep the transport error when the response is not a Relay JSON error.
                            }
                        }
                        throw new RelayApiException(request.responseCode, code, message);
                    }
                    else
                    {
                        return request.downloadHandler.text ?? string.Empty;
                    }
            }
            finally
            {
                if (!request.isDone)
                    request.Abort();
            }
        }
    }
}
