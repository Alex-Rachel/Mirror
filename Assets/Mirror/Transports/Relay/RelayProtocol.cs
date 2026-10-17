using System;

namespace Mirror.Relay
{
    public enum RelayMessageType : byte
    {
        CreateRoom = 1,
        JoinRoom = 2,
        RoomCreated = 3,
        JoinedRoom = 4,
        Data = 5,
        Ping = 6,
        Leave = 7,
        PeerLeft = 8,
        Reject = 9
    }

    public struct RelayPacket
    {
        public RelayMessageType messageType;
        public Guid sessionId;
        public Guid token;
        public int peerId;
        public int targetPeerId;
        public ArraySegment<byte> payload;
    }

    public static class RelayProtocol
    {
        public const uint Magic = 0x594C524D; // "MRLY" in little endian.
        public const byte Version = 1;
        public const int GuidSize = 16;
        public const int HeaderSize = 4 + 1 + 1 + GuidSize + GuidSize + 4 + 4 + 2;
        public const int DefaultMtu = 1200;
        public const int DefaultMaxPacketSize = DefaultMtu;
        public const int MaxRoomIdLength = 64;

        public static int MaxPayload(int mtu = DefaultMtu) =>
            Math.Max(0, mtu - HeaderSize);

        public static int Write(
            byte[] buffer,
            RelayMessageType messageType,
            Guid sessionId,
            Guid token,
            int peerId,
            int targetPeerId,
            ArraySegment<byte> payload)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (payload.Count > MaxPayload()) throw new ArgumentOutOfRangeException(nameof(payload));
            if (buffer.Length < HeaderSize + payload.Count) throw new ArgumentException("Relay packet buffer is too small.", nameof(buffer));

            int offset = 0;
            WriteUInt32(buffer, ref offset, Magic);
            buffer[offset++] = Version;
            buffer[offset++] = (byte)messageType;
            WriteGuid(buffer, ref offset, sessionId);
            WriteGuid(buffer, ref offset, token);
            WriteInt32(buffer, ref offset, peerId);
            WriteInt32(buffer, ref offset, targetPeerId);
            WriteUInt16(buffer, ref offset, (ushort)payload.Count);

            if (payload.Count > 0)
            {
                if (payload.Array == null)
                    throw new ArgumentException("Payload array cannot be null when payload.Count is non-zero.", nameof(payload));
                Buffer.BlockCopy(payload.Array, payload.Offset, buffer, offset, payload.Count);
            }

            return HeaderSize + payload.Count;
        }

        public static bool TryRead(byte[] buffer, int count, out RelayPacket packet)
        {
            packet = default;
            if (buffer == null || count < HeaderSize || count > buffer.Length || count > DefaultMaxPacketSize)
                return false;

            int offset = 0;
            if (ReadUInt32(buffer, ref offset) != Magic)
                return false;
            if (buffer[offset++] != Version)
                return false;

            RelayMessageType messageType = (RelayMessageType)buffer[offset++];
            Guid sessionId = ReadGuid(buffer, ref offset);
            Guid token = ReadGuid(buffer, ref offset);
            int peerId = ReadInt32(buffer, ref offset);
            int targetPeerId = ReadInt32(buffer, ref offset);
            int payloadLength = ReadUInt16(buffer, ref offset);

            if (payloadLength != count - HeaderSize)
                return false;

            packet = new RelayPacket
            {
                messageType = messageType,
                sessionId = sessionId,
                token = token,
                peerId = peerId,
                targetPeerId = targetPeerId,
                payload = new ArraySegment<byte>(buffer, HeaderSize, payloadLength)
            };
            return true;
        }

        public static bool TryReadUtf8(ArraySegment<byte> payload, out string value)
        {
            if (payload.Array == null)
            {
                value = string.Empty;
                return false;
            }

            try
            {
                value = System.Text.Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
                return true;
            }
            catch
            {
                value = string.Empty;
                return false;
            }
        }

        public static ArraySegment<byte> Utf8(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            return new ArraySegment<byte>(bytes);
        }

        static void WriteGuid(byte[] buffer, ref int offset, Guid value)
        {
            byte[] bytes = value.ToByteArray();
            Buffer.BlockCopy(bytes, 0, buffer, offset, GuidSize);
            offset += GuidSize;
        }

        static Guid ReadGuid(byte[] buffer, ref int offset)
        {
            byte[] bytes = new byte[GuidSize];
            Buffer.BlockCopy(buffer, offset, bytes, 0, GuidSize);
            offset += GuidSize;
            return new Guid(bytes);
        }

        static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
        }

        static int ReadUInt16(byte[] buffer, ref int offset)
        {
            int value = buffer[offset] | buffer[offset + 1] << 8;
            offset += 2;
            return value;
        }

        static void WriteUInt32(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }

        static uint ReadUInt32(byte[] buffer, ref int offset)
        {
            uint value =
                (uint)buffer[offset] |
                (uint)buffer[offset + 1] << 8 |
                (uint)buffer[offset + 2] << 16 |
                (uint)buffer[offset + 3] << 24;
            offset += 4;
            return value;
        }

        static void WriteInt32(byte[] buffer, ref int offset, int value) =>
            WriteUInt32(buffer, ref offset, unchecked((uint)value));

        static int ReadInt32(byte[] buffer, ref int offset) =>
            unchecked((int)ReadUInt32(buffer, ref offset));
    }
}
