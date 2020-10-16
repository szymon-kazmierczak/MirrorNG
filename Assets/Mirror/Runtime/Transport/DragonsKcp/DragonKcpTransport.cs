#region Statements

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

#endregion

namespace Mirror.DragonsKcp
{
    public class DragonKcpTransport : Transport
    {
        #region Fields

        public KcpOptions Options = new KcpOptions();

        private Server _server;
        private KcpConnection _client;
        public long ReceivedMessageCount { get; private set; }

        #endregion

        #region Overrides of Transport

        public override IEnumerable<string> Scheme => new[] { "kcp" };

        /// <summary>
        ///     Open up the port and listen for connections
        ///     Use in servers.
        /// </summary>
        /// <exception>If we cannot start the transport</exception>
        /// <returns></returns>
        public override UniTask ListenAsync()
        {
            _server = new Server(Options);

            return _server.Listen();
        }

        /// <summary>
        ///     Stop listening to the port
        /// </summary>
        public override void Disconnect()
        {
            _server?.Disconnect();
            _client?.Disconnect();
        }

        /// <summary>
        ///     Determines if this transport is supported in the current platform
        /// </summary>
        /// <returns>true if the transport works in this platform</returns>
        public override bool Supported => Application.platform != RuntimePlatform.WebGLPlayer;

        /// <summary>
        ///     Connect to a server located at a provided uri
        /// </summary>
        /// <param name="uri">address of the server to connect to</param>
        /// <returns>The connection to the server</returns>
        /// <exception>If connection cannot be established</exception>
        public override async UniTask<IConnection> ConnectAsync(Uri uri)
        {
            _client = new KcpConnection(null, null, Options);

            ushort port = (ushort)(uri.IsDefaultPort ? Options.Port : uri.Port);

            return await _client.ConnectAsync(uri.Host, port) ? _client : null;
        }

        /// <summary>
        ///     Accepts a connection from a client.
        ///     After ListenAsync completes,  clients will queue up until you call AcceptAsync
        ///     then you get the connection to the client
        /// </summary>
        /// <returns>The connection to a client</returns>
        public override async UniTask<IConnection> AcceptAsync()
        {
            while(_server != null)
            {
                while (_server.AcceptedConnections.TryTake(out KcpConnection connection))
                {
                    return connection;
                }

                await UniTask.Delay(100);
            }

            return null;
        }

        /// <summary>
        ///     Retrieves the address of this server.
        ///     Useful for network discovery
        /// </summary>
        /// <returns>the url at which this server can be reached</returns>
        public override IEnumerable<Uri> ServerUri()
        {
            var builder = new UriBuilder
            {
                Scheme = "kcp",
                Host = Options.BindAddress,
                Port = Options.Port
            };
            return new[] { builder.Uri };
        }

        #endregion
    }
}
