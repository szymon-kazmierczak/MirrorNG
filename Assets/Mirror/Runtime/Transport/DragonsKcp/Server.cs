#region Statements

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using Mirror.KCP;
using UnityEngine;

#endregion

namespace Mirror.DragonsKcp
{
    public class Server : Common
    {
        private EndPoint _newClientEp = new IPEndPoint(IPAddress.IPv6Any, 0);

        internal readonly BlockingCollection<KcpConnection> AcceptedConnections = new BlockingCollection<KcpConnection>();
        private readonly Dictionary<IPEndPoint, KcpConnection> ConnectedClients = new Dictionary<IPEndPoint, KcpConnection>(new IPEndpointComparer());

        public Server(KcpOptions options) : base(options)
        {
            Debug.Log("Sever is spinning up.");

            UniTask.RunOnThreadPool(Update).Forget();
        }

        public UniTask Listen()
        {
            SocketConnection =
                new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) {DualMode = true};
            SocketConnection.Bind(new IPEndPoint(IPAddress.IPv6Any, Options.Port));

            return UniTask.CompletedTask;
        }

        private void ProcessIncomingInput(EndPoint endpoint, byte[] data, int msgLength)
        {
            // Make sure to check total connections. If too many here send back a
            // message saying so.
            if (ConnectedClients.Count >= Options.MaximumConnections)
            {
                byte[] denyMessage = {(byte)InternalMessage.TooManyUsers};

                SocketConnection.SendTo(denyMessage, endpoint);
            }

            ConnectedClients.TryGetValue((IPEndPoint) endpoint, out KcpConnection connection);

            switch (msgLength)
            {
                // Check to see if it was the accept message from server.
                case 1 when data[0] == (byte)InternalMessage.Connect:
                    // add it to a queue
                    connection = new KcpConnection(SocketConnection, endpoint, Options);

                    AcceptedConnections.TryAdd(connection);

                    ConnectedClients.Add((IPEndPoint) endpoint, connection);

                    // Send back to client we accepted request to connect.
                    byte[] acceptMessage = {(byte)InternalMessage.AcceptConnection};

                    SocketConnection.SendTo(acceptMessage, endpoint);
                    return;
                case 1 when data[0] == (byte)InternalMessage.Disconnect:
                    if (ConnectedClients.ContainsKey((IPEndPoint)endpoint))
                        ConnectedClients.Remove((IPEndPoint)endpoint);
                    return;
                default:
                    // Already connected let's process data.               
                    connection?.ProcessIncomingInput(data, msgLength);
                    break;
            }
        }

        #region Overrides of Common

        /// <summary>
        ///     Shutdown and disconnect
        /// </summary>
        public override void Disconnect()
        {
            CancellationToken?.Cancel();
            SocketConnection?.Close();
            SocketConnection = null;
        }

        protected sealed override async UniTaskVoid Update()
        {
            int msgLength = 0;

            while(!CancellationToken.IsCancellationRequested)
            {
                while (SocketConnection != null && SocketConnection.Poll(0, SelectMode.SelectRead))
                {
                    msgLength = SocketConnection.ReceiveFrom(ReceiveBuffer, 0, ReceiveBuffer.Length,
                        SocketFlags.None, ref _newClientEp);

                    DragonKcpTransport.ReceivedMessageCount++;

                    ProcessIncomingInput(_newClientEp, ReceiveBuffer, msgLength);
                }

                await UniTask.Delay(msgLength);
            }
        }

        #endregion
    }
}
