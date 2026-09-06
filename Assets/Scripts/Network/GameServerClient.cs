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
        // Délai avant de couper réellement la connexion après une mise en arrière-plan (voir
        // OnApplicationPause) : un simple changement d'appli furtif (notification, verrouillage
        // d'écran bref) ne doit pas suffire à faire perdre le match — seule une absence prolongée
        // doit déclencher la déconnexion. Utilise System.Threading.Timer (pas une coroutine) car
        // Unity arrête sa boucle Update()/coroutines pendant la pause : un minuteur .NET classique
        // continue de tourner sur son propre thread indépendamment du cycle de vie Unity.
        private const float BackgroundGraceSeconds = 12f;
        private Timer pauseGraceTimer;
        // Toutes les ~5s (voir 03-network-protocol.md, message "heartbeat") : sans cet envoi
        // régulier, le serveur (une fois son propre timeout de lecture rendu fini, voir
        // GameServerBootstrap.HandleHandshake) considérerait un joueur simplement silencieux
        // pendant sa phase de planification comme déconnecté.
        private const float HeartbeatIntervalSeconds = 5f;
        private float heartbeatTimer = 0f;

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

            if (isConnected)
            {
                heartbeatTimer += Time.deltaTime;
                if (heartbeatTimer >= HeartbeatIntervalSeconds)
                {
                    heartbeatTimer = 0f;
                    Send(new NetMessage { type = "heartbeat" });
                }
            }
        }

        public void Connect(string accessToken)
        {
            if (isConnected) return;

            try
            {
                tcpClient = new TcpClient();
                tcpClient.NoDelay = true;
                // 25s, pas 15s : le serveur envoie désormais un signal de vie toutes les 15s pendant
                // l'attente de chargement de carte (voir MatchSessionManager_Deployment.cs,
                // ServerKeepaliveIntervalSeconds) — avec un timeout EXACTEMENT égal à cet intervalle,
                // la moindre latence réseau aurait suffi à redéclencher la même déconnexion
                // "connection_lost" qu'on vient de corriger. Marge large et volontaire.
                tcpClient.ReceiveTimeout = 25000;
                tcpClient.SendTimeout = 10000;
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
                try { stream?.Close(); } catch { }
                try { tcpClient?.Close(); } catch { }
                pendingDisconnectReason = "connection_failed";
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
            bool wasConnected = isConnected;
            isConnected = false;
            try { stream?.Close(); } catch { }
            try { tcpClient?.Close(); } catch { }
            if (wasConnected || !string.IsNullOrEmpty(reason))
            {
                pendingDisconnectReason = reason; // dispatché sur le thread principal via Update()
            }
        }

        private void OnDestroy()
        {
            pauseGraceTimer?.Dispose();
            Disconnect("destroyed");
        }

        // Sans ces deux callbacks, fermer l'app (bouton Accueil, tâche tuée, écran verrouillé) ne
        // fermait proprement la socket que si Unity détruisait l'objet en sortie normale — jamais
        // en mise en arrière-plan Android/iOS. La connexion restait alors ouverte côté serveur
        // jusqu'à ce qu'un envoi/une lecture finisse par expirer (voir GameServerBootstrap et le
        // timeout ajouté sur PlayerConnection), au lieu d'être immédiatement traitée comme une
        // vraie déconnexion (Ghost dès le tour suivant).
        private void OnApplicationQuit()
        {
            pauseGraceTimer?.Dispose();
            Disconnect("app_quit");
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                pauseGraceTimer?.Dispose();
                pauseGraceTimer = new Timer(_ => Disconnect("app_paused"), null,
                    TimeSpan.FromSeconds(BackgroundGraceSeconds), Timeout.InfiniteTimeSpan);
            }
            else
            {
                // Retour au premier plan avant l'expiration du délai de grâce : la connexion tient
                // toujours, on annule la déconnexion différée.
                pauseGraceTimer?.Dispose();
                pauseGraceTimer = null;
            }
        }
    }
}
