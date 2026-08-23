using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Novgov.Network
{
    /// <summary>
    /// Connexion TCP vers le serveur de jeu Unity headless (voir 03-network-protocol.md).
    /// La lecture réseau tourne sur un thread dédié ; les messages reçus sont mis en file et
    /// redistribués sur le thread principal Unity via Update() pour rester thread-safe.
    /// </summary>
    public class GameServerClient : MonoBehaviour
    {
        public static GameServerClient Instance { get; private set; }

        public static string ServerHost = "novgov.com";
        public static int ServerPort = 7777;

        public event Action<NetMessage> OnMessage;
        public event Action<string> OnDisconnected;

        private TcpClient tcpClient;
        private NetworkStream stream;
        private Thread receiveThread;
        private readonly ConcurrentQueue<NetMessage> incoming = new ConcurrentQueue<NetMessage>();
        private readonly object sendLock = new object();
        private volatile bool isConnected = false;
        private volatile string pendingDisconnectReason = null;

        public bool IsConnected => isConnected;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            while (incoming.TryDequeue(out NetMessage msg))
            {
                OnMessage?.Invoke(msg);
            }

            string reason = pendingDisconnectReason;
            if (reason != null)
            {
                pendingDisconnectReason = null;
                OnDisconnected?.Invoke(reason);
            }
        }

        public void Connect(string accessToken)
        {
            if (isConnected) return;

            try
            {
                tcpClient = new TcpClient();
                tcpClient.Connect(ServerHost, ServerPort);
                stream = tcpClient.GetStream();
                isConnected = true;

                Send(new NetMessage { type = "auth", access_token = accessToken });

                receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                receiveThread.Start();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GameServerClient] Connexion échouée : {ex.Message}");
                isConnected = false;
            }
        }

        public void Send(NetMessage msg)
        {
            if (!isConnected || stream == null) return;
            lock (sendLock)
            {
                try
                {
                    NetFraming.WriteMessage(stream, msg);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GameServerClient] Échec d'envoi : {ex.Message}");
                    Disconnect("send_failed");
                }
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (isConnected)
                {
                    NetMessage msg = NetFraming.ReadMessage(stream);
                    incoming.Enqueue(msg);
                }
            }
            catch (Exception)
            {
                // Connexion fermée ou erreur réseau — traité par Disconnect ci-dessous.
            }
            Disconnect("connection_lost");
        }

        public void Disconnect(string reason)
        {
            if (!isConnected) return;
            isConnected = false;
            try { stream?.Close(); } catch { }
            try { tcpClient?.Close(); } catch { }
            pendingDisconnectReason = reason; // dispatché sur le thread principal via Update()
        }

        private void OnDestroy()
        {
            Disconnect("destroyed");
        }
    }
}
