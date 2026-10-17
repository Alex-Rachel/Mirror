using System;

namespace Mirror.Relay
{
    public sealed class RelayRoomEvents
    {
        public event Action<RelayRoomDetails> RoomChanged;
        public event Action<RelayRoomMember> MemberJoined;
        public event Action<RelayRoomMember> MemberLeft;
        public event Action<string> RoomClosed;
        public event Action<Exception> Error;

        internal void RaiseRoomChanged(RelayRoomDetails room)
        {
            RoomChanged?.Invoke(room);
        }

        internal void RaiseMemberJoined(RelayRoomMember member)
        {
            MemberJoined?.Invoke(member);
        }

        internal void RaiseMemberLeft(RelayRoomMember member)
        {
            MemberLeft?.Invoke(member);
        }

        internal void RaiseRoomClosed(string reason)
        {
            RoomClosed?.Invoke(reason ?? string.Empty);
        }

        internal void RaiseError(Exception exception)
        {
            Error?.Invoke(exception);
        }
    }
}
