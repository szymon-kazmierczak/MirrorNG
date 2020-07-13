﻿using UnityEngine;
using Mirror.AsyncTcp;
using UnityEditor;

namespace Mirror
{

    public static class NetworkMenu
    {
        // Start is called before the first frame update
        [MenuItem("GameObject/Network/NetworkManager", priority = 7)]
        public static GameObject CreateNetworkManager()
        {
            var go = new GameObject("NetworkManager", typeof(AsyncTcpTransport), typeof(NetworkSceneManager), typeof(NetworkClient), typeof(NetworkServer), typeof(NetworkManager), typeof(PlayerSpawner), typeof(NetworkManagerHUD));

            AsyncTcpTransport transport = go.GetComponent<AsyncTcpTransport>();
            NetworkSceneManager nsm = go.GetComponent<NetworkSceneManager>();

            NetworkClient networkClient = go.GetComponent<NetworkClient>();
            networkClient.Transport = transport;
            networkClient.sceneManager = nsm;

            NetworkServer networkServer = go.GetComponent<NetworkServer>();
            networkServer.transport = transport;
            networkServer.sceneManager = nsm;

            NetworkManager networkManager = go.GetComponent<NetworkManager>();
            networkManager.client = networkClient;
            networkManager.server = networkServer;
            networkManager.transport = transport;

            PlayerSpawner playerSpawner = go.GetComponent<PlayerSpawner>();
            playerSpawner.client = networkClient;
            playerSpawner.server = networkServer;

            nsm.client = networkClient;
            nsm.server = networkServer;
            return go;
        }
    }
}