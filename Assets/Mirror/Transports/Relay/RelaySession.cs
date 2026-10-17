using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Mirror.Relay
{
    public static class RelaySession
    {
        public static Task<RelayCredentials> CreateAsync(
            string relayAddress,
            ushort relayPort,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Create(relayAddress, relayPort, timeoutMilliseconds, cancellationToken),
                cancellationToken);
        }

        public static Task<RelayCredentials> JoinAsync(
            string relayAddress,
            ushort relayPort,
            Guid sessionId,
            Guid hostToken,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Join(relayAddress, relayPort, sessionId, hostToken, timeoutMilliseconds, cancellationToken),
                cancellationToken);
        }

        public static RelayCredentials Create(
            string relayAddress,
            ushort relayPort,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            using (Socket socket = Connect(relayAddress, relayPort, timeoutMilliseconds, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] buffer = new byte[RelayProtocol.HeaderSize];
                int count = RelayProtocol.Write(
                    buffer,
                    RelayMessageType.CreateRoom,
                    default,
                    default,
                    0,
                    0,
                    default);
                socket.Send(buffer, 0, count, SocketFlags.None);

                RelayPacket packet = Receive(socket, timeoutMilliseconds, cancellationToken);
                if (packet.messageType != RelayMessageType.RoomCreated ||
                    packet.sessionId == default ||
                    packet.token == default ||
                    packet.peerId <= 0)
                    throw new InvalidOperationException("Relay did not create a room.");

                return new RelayCredentials
                {
                    relayAddress = relayAddress,
                    relayPort = relayPort,
                    sessionId = packet.sessionId,
                    token = packet.token,
                    peerId = packet.peerId
                };
            }
        }

        public static RelayCredentials Join(
            string relayAddress,
            ushort relayPort,
            Guid sessionId,
            Guid hostToken,
            int timeoutMilliseconds = 10000,
            CancellationToken cancellationToken = default)
        {
            using (Socket socket = Connect(relayAddress, relayPort, timeoutMilliseconds, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] buffer = new byte[RelayProtocol.HeaderSize];
                int count = RelayProtocol.Write(
                    buffer,
                    RelayMessageType.JoinRoom,
                    sessionId,
                    hostToken,
                    0,
                    0,
                    default);
                socket.Send(buffer, 0, count, SocketFlags.None);

                RelayPacket packet = Receive(socket, timeoutMilliseconds, cancellationToken);
                if (packet.messageType == RelayMessageType.Reject)
                {
                    RelayProtocol.TryReadUtf8(packet.payload, out string reason);
                    throw new InvalidOperationException(string.IsNullOrEmpty(reason) ? "Relay rejected the join." : reason);
                }

                if (packet.messageType != RelayMessageType.JoinedRoom ||
                    packet.sessionId != sessionId ||
                    packet.token == default ||
                    packet.peerId <= 1)
                    throw new InvalidOperationException("Relay did not join the room.");

                return new RelayCredentials
                {
                    relayAddress = relayAddress,
                    relayPort = relayPort,
                    sessionId = packet.sessionId,
                    token = packet.token,
                    peerId = packet.peerId
                };
            }
        }

        public static void Leave(RelayCredentials credentials, int timeoutMilliseconds = 500)
        {
            try
            {
                using (Socket socket = Connect(credentials.relayAddress, credentials.relayPort, timeoutMilliseconds, out _))
                {
                    byte[] buffer = new byte[RelayProtocol.HeaderSize];
                    int count = RelayProtocol.Write(
                        buffer,
                        RelayMessageType.Leave,
                        credentials.sessionId,
                        credentials.token,
                        credentials.peerId,
                        0,
                        default);
                    socket.Send(buffer, 0, count, SocketFlags.None);
                }
            }
            catch
            {
                // Leaving is best effort during shutdown.
            }
        }

        static Socket Connect(
            string relayAddress,
            ushort relayPort,
            int timeoutMilliseconds,
            out IPEndPoint endpoint)
        {
            if (!kcp2k.Common.ResolveHostname(relayAddress, out IPAddress[] addresses))
                throw new SocketException((int)SocketError.HostNotFound);

            endpoint = new IPEndPoint(addresses[0], relayPort);
            Socket socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = true,
                ReceiveTimeout = timeoutMilliseconds,
                SendTimeout = timeoutMilliseconds
            };
            socket.Connect(endpoint);
            return socket;
        }

        static RelayPacket Receive(Socket socket, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            // Cancellation must interrupt the blocking control socket as well.
            using CancellationTokenRegistration registration = cancellationToken.Register(socket.Close);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] buffer = new byte[65535];
                socket.ReceiveTimeout = timeoutMilliseconds;
                int received = socket.Receive(buffer);
                cancellationToken.ThrowIfCancellationRequested();
                if (!RelayProtocol.TryRead(buffer, received, out RelayPacket packet))
                    throw new InvalidOperationException("Relay returned an invalid packet.");
                return packet;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }
}
