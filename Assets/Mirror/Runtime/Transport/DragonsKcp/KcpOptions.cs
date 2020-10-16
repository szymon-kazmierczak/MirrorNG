#region Statements

using System;
using UnityEngine;

#endregion

namespace Mirror.DragonsKcp
{
    [Serializable]
    public class KcpOptions
    {
        [Header("Transport Configuration")]
        [Tooltip("The port we want to connect clients on and server to bind to.")] public ushort Port = 7777;
        [Tooltip("The address we want to bind the server on.")] public string BindAddress = "localhost";
        [Tooltip("Set this to same as server component maximum connections.")]public int MaximumConnections = 4;
        [Tooltip("How long to wait from server before connection not accepted.")] public int ClientConnectionTimeout = 30;
    }
}
