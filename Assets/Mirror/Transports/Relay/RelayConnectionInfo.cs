using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mirror.Relay
{
    public struct RelayCredentials
    {
        public string relayAddress;
        public ushort relayPort;
        public Guid sessionId;
        public Guid token;
        public int peerId;

        public Uri ToUri()
        {
            UriBuilder builder = new UriBuilder(RelayKcpTransport.Scheme, relayAddress, relayPort);
            builder.Query = $"room={sessionId:D}&token={token:D}&peer={peerId.ToString(CultureInfo.InvariantCulture)}";
            return builder.Uri;
        }

        public string ToInviteCode() =>
            $"{sessionId:D}|{token:D}";

        public static bool TryParseInvite(string invite, out Guid sessionId, out Guid hostToken)
        {
            sessionId = default;
            hostToken = default;
            if (string.IsNullOrWhiteSpace(invite))
                return false;

            string[] parts = invite.Split('|');
            return parts.Length == 2 &&
                   Guid.TryParse(parts[0], out sessionId) &&
                   Guid.TryParse(parts[1], out hostToken);
        }
    }

    internal struct RelayConnectionInfo
    {
        public string relayAddress;
        public ushort relayPort;
        public Guid sessionId;
        public Guid token;
        public int peerId;

        public static bool TryParse(
            Uri uri,
            string fallbackAddress,
            ushort fallbackPort,
            out RelayConnectionInfo info)
        {
            info = default;
            if (uri == null)
                return false;
            if (uri.Scheme != RelayKcpTransport.Scheme)
                return false;

            Dictionary<string, string> query = ParseQuery(uri.Query);
            if (!query.TryGetValue("room", out string room) ||
                !Guid.TryParse(room, out Guid sessionId))
                return false;

            Guid token = default;
            if (query.TryGetValue("token", out string tokenValue) &&
                !Guid.TryParse(tokenValue, out token))
                return false;

            int peerId = 0;
            if (query.TryGetValue("peer", out string peerValue) &&
                !int.TryParse(peerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out peerId))
                return false;

            info = new RelayConnectionInfo
            {
                relayAddress = string.IsNullOrWhiteSpace(uri.Host) ? fallbackAddress : uri.Host,
                relayPort = uri.IsDefaultPort ? fallbackPort : checked((ushort)uri.Port),
                sessionId = sessionId,
                token = token,
                peerId = peerId
            };
            return true;
        }

        public static bool TryParse(
            string address,
            string fallbackAddress,
            ushort fallbackPort,
            out RelayConnectionInfo info)
        {
            info = default;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri))
                return false;
            return TryParse(uri, fallbackAddress, fallbackPort, out info);
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;

            string trimmed = query[0] == '?' ? query.Substring(1) : query;
            foreach (string pair in trimmed.Split('&'))
            {
                if (string.IsNullOrEmpty(pair))
                    continue;

                string[] parts = pair.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(parts[0]);
                string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                result[key] = value;
            }
            return result;
        }
    }
}
