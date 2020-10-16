#region Statements

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using Mirror.KCP;
using UnityEngine;

#endregion

namespace Mirror.DragonsKcp
{
    public class KcpConnection : Common, IConnection
    {
        #region Fields

        private readonly BlockingCollection<byte[]> _receivedMessages = new BlockingCollection<byte[]>();
        private byte[] _mirrorReceiveBuffer;
        private EndPoint _remoteEndpoint;
        private readonly Kcp _kcp;
        private volatile uint _lastReceived;
        private UniTaskCompletionSource _connectedComplete;

        /// <summary>
        /// Space for CRC64
        /// </summary>
        private const int Reserved = sizeof(ulong);

        // If we don't receive anything these many milliseconds
        // then consider us disconnected
        private const int Timeout = 15000;

        #endregion

        public KcpConnection(Socket socket, EndPoint endPoint, KcpOptions options) : base(options)
        {
            SocketConnection = socket;
            _remoteEndpoint = endPoint;

            _kcp = new Kcp(0, SendWithChecksum);
            _kcp.SetNoDelay();

            // reserve some space for CRC64
            _kcp.ReserveBytes(Reserved);

            KcpUpdate().Forget();
        }

        internal async UniTask<IConnection> ConnectAsync(string host, ushort port)
        {
            IPAddress[] ipAddress = await Dns.GetHostAddressesAsync(host);

            if (ipAddress.Length < 1)
                throw new SocketException((int)SocketError.HostNotFound);

            _remoteEndpoint = new IPEndPoint(ipAddress[0], port);

            UniTask.RunOnThreadPool(Update).Forget();

            SocketConnection = new Socket(_remoteEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            SocketConnection.Connect(_remoteEndpoint);

            byte[] connectMessage = {(byte)InternalMessage.Connect};

            SocketConnection.Send(connectMessage, SocketFlags.None);

            _connectedComplete = new UniTaskCompletionSource();
            UniTask connectedCompleteTask = _connectedComplete.Task;

            while (await UniTask.WhenAny(connectedCompleteTask,
                UniTask.Delay(TimeSpan.FromSeconds(Math.Max(1, Options.ClientConnectionTimeout)))) != 0)
            {
                return null;
            }

            return this;
        }

        private void RawSend(byte[] data, int length)
        {
            SocketConnection?.SendTo(data, length, SocketFlags.None, _remoteEndpoint);
        }

        private void SendWithChecksum(byte[] data, int length)
        {
            // add a CRC64 checksum in the reserved space
            ulong crc = Crc64.Compute(data, Reserved, length - Reserved);

            Utils.Encode64U(data, 0, crc);

            RawSend(data, length);

            //if (_kcp.WaitSnd > 1000)
            //{
            //    Debug.LogWarningFormat("Too many packets waiting in the send queue {0}, you are sending too much data,  the transport can't keep up", _kcp.WaitSnd);
            //}
        }

        #region Overrides of Common

        public UniTask SendAsync(ArraySegment<byte> data)
        {
            _kcp.Send(data.Array, data.Offset, data.Count);

            return UniTask.CompletedTask;
        }

        /// <summary>
        /// reads a message from connection
        /// </summary>
        /// <param name="buffer">buffer where the message will be written</param>
        /// <returns>true if we got a message, false if we got disconnected</returns>
        public async UniTask<bool> ReceiveAsync(MemoryStream buffer)
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                while (_receivedMessages.TryTake(out byte[] data))
                {
                    // we have some data,  return it
                    buffer.SetLength(0);

                    await buffer.WriteAsync(data, 0, data.Length);

                    return true;
                }

                await UniTask.Delay(1);
            }

            return false;
        }

        /// <summary>
        ///     Shutdown and disconnect
        /// </summary>
        public override async void Disconnect()
        {
            SocketConnection.Send(new[] {(byte)InternalMessage.Disconnect}, SocketFlags.None);

            _kcp.Flush(false);

            await UniTask.Delay(1000);

            CancellationToken?.Cancel();
            SocketConnection?.Close();
            SocketConnection = null;
        }

        /// <summary>
        /// the address of endpoint we are connected to
        /// Note this can be IPEndPoint or a custom implementation
        /// of EndPoint, which depends on the transport
        /// </summary>
        /// <returns></returns>
        public EndPoint GetEndPointAddress()
        {
            return _remoteEndpoint;
        }

        /// <summary>
        ///     Process kcp update checks.
        /// </summary>
        private async UniTaskVoid KcpUpdate()
        {
            while(!CancellationToken.IsCancellationRequested)
            {
                _lastReceived = _kcp.CurrentMS;

                while (_kcp.CurrentMS < _lastReceived + Timeout)
                {
                    _kcp.Update();

                    int check = _kcp.Check();

                    // call every 10 ms unless check says we can wait longer
                    if (check < 10)
                        check = 10;

                    await UniTask.Delay(check);
                }
            }
        }

        /// <summary>
        ///     Process raw and kcp socket updates.
        /// </summary>
        protected override async UniTaskVoid Update()
        {
            try
            {
                int msgLength = 0;

                while (!CancellationToken.IsCancellationRequested)
                {
                    while (SocketConnection != null && SocketConnection.Poll(0, SelectMode.SelectRead))
                    {
                        msgLength = SocketConnection.ReceiveFrom(ReceiveBuffer, ref _remoteEndpoint);

                        ProcessIncomingInput(ReceiveBuffer, msgLength);
                    }

                    await UniTask.Delay(msgLength);
                }
            }
            catch (SocketException)
            {
                // this is fine,  the socket might have been closed in the other end
            }
        }

        internal void ProcessIncomingInput(byte[] data, int msgLength)
        {
            switch (msgLength)
            {
                // Check to see if it was the accept message from server.
                case 1 when data[0] == (byte)InternalMessage.Disconnect:
                    Disconnect();
                    return;
                case 1 when data[0] == (byte)InternalMessage.AcceptConnection:

                    _connectedComplete.TrySetResult();

                    return;
                default:

                    // If we don't get back a 0 we did not receive data or had errors.
                    _kcp.Input(data, Reserved, msgLength, true, false);

                    _lastReceived = _kcp.CurrentMS;

                    int msgSize = _kcp.PeekSize();

                    if (msgSize <= 0)
                    {
                        return;
                    }

                    _mirrorReceiveBuffer = new byte[msgSize];

                    _kcp.Receive(_mirrorReceiveBuffer, 0, _mirrorReceiveBuffer.Length);

                    _receivedMessages.TryAdd(_mirrorReceiveBuffer);
                    return;
            }
        }

        #endregion
    }
}
