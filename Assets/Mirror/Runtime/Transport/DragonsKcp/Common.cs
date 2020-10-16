#region Statements

using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;

#endregion

namespace Mirror.DragonsKcp
{
    public abstract class Common
    {
        #region Fields

        protected readonly CancellationTokenSource CancellationToken;
        protected Socket SocketConnection;
        protected readonly byte[] ReceiveBuffer = new byte[1500];
        protected readonly KcpOptions Options;

        #endregion

        protected Common(KcpOptions options)
        {
            Options = options;

            CancellationToken = new CancellationTokenSource();
        }

        /// <summary>
        ///     Shutdown and disconnect
        /// </summary>
        public abstract void Disconnect();

        /// <summary>
        ///     Process socket updates.
        /// </summary>
        protected abstract UniTaskVoid Update();
    }
}
