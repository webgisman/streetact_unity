using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Point d'entrée du serveur de jeu headless. Ne s'exécute que dans un build "Dedicated Server"
    /// (UNITY_SERVER est défini automatiquement par Unity pour ce target). Voir
    /// Assets/_ServerDocs/multiplayer/04-unity-headless-server.md.
    /// </summary>
    public static class GameServerBootstrap
    {
        public static string JwtSecret { get; private set; }
        public static string RestUrl { get; private set; }
        public static string ServiceRoleKey { get; private set; }

        public static readonly ConcurrentQueue<PlayerConnection> AuthenticatedConnections = new ConcurrentQueue<PlayerConnection>();

        private static TcpListener listener;

#if UNITY_SERVER
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            int port = GetEnvInt("GAME_PORT", 7777);
            JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "";
            RestUrl = Environment.GetEnvironmentVariable("REST_URL") ?? "http://rest:3000";
            ServiceRoleKey = Environment.GetEnvironmentVariable("SERVICE_ROLE_KEY") ?? "";

            if (string.IsNullOrEmpty(JwtSecret))
            {
                Debug.LogError("[GameServerBootstrap] JWT_SECRET manquant — le serveur ne peut valider aucune connexion. Arrêt.");
                return;
            }

            Application.targetFrameRate = 30; // Suffisant pour la simulation de combat, économise le CPU serveur
            QualitySettings.vSyncCount = 0;

            var go = new GameObject("MatchSessionManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MatchSessionManager>();

            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Debug.Log($"[GameServerBootstrap] Serveur de jeu Novgov à l'écoute sur le port {port}.");

            Thread acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            acceptThread.Start();
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GameServerBootstrap] Arrêt de la boucle d'acceptation : {ex.Message}");
                    return;
                }

                // Le handshake d'auth se fait sur un thread jetable pour ne jamais bloquer l'acceptation
                // de nouvelles connexions pendant qu'un client lent ou malveillant traîne.
                Thread handshakeThread = new Thread(() => HandleHandshake(client)) { IsBackground = true };
                handshakeThread.Start();
            }
        }

        private static void HandleHandshake(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                NetworkStream stream = client.GetStream();
                // 8 Ko est très généreux pour un message "auth" (JWT + enveloppe JSON) et empêche un
                // socket non authentifié de faire allouer jusqu'à 8 Mo (la limite gameplay normale)
                // rien qu'en annonçant une longueur bidon dans l'en-tête — voir NetFraming.ReadMessage.
                const int MaxHandshakeMessageSize = 8 * 1024;
                NetMessage first = NetFraming.ReadMessage(stream, MaxHandshakeMessageSize);

                if (first.type != "auth")
                {
                    Debug.LogWarning("[GameServerBootstrap] Premier message reçu n'est pas 'auth' — connexion rejetée.");
                    client.Close();
                    return;
                }

                string userId = JwtValidator.ValidateAndGetUserId(first.access_token, JwtSecret);
                if (userId == null)
                {
                    Debug.LogWarning("[GameServerBootstrap] JWT invalide ou expiré — connexion rejetée.");
                    client.Close();
                    return;
                }

                // Auparavant remis à 0 (infini) après l'auth : un client mobile qui disparaît
                // silencieusement (perte de réseau ou app tuée sans FIN/RST propre, voir
                // GameServerClient.OnApplicationPause/Quit côté client) laissait alors le thread de
                // lecture bloqué indéfiniment, et surtout risquait de bloquer un futur PlayerConnection.Send()
                // (turn_timer/turn_result, appelés depuis le thread principal) si le tampon socket finissait
                // par se remplir — gelant tout le serveur puisqu'un seul match tourne à la fois (voir
                // 06-security-checklist.md). Un timeout fini, combiné au heartbeat client toutes les 5s
                // (voir GameServerClient/MatchSessionManager.DrainMessages), permet de détecter une
                // connexion morte sans jamais couper un joueur simplement silencieux en pleine réflexion.
                client.ReceiveTimeout = 20000;
                client.SendTimeout = 10000;
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
                var connection = new PlayerConnection(client, stream, userId);
                connection.StartReceiving();
                AuthenticatedConnections.Enqueue(connection);
                Debug.Log($"[GameServerBootstrap] Joueur authentifié : {userId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GameServerBootstrap] Handshake échoué : {ex.Message}");
                try { client.Close(); } catch { }
            }
        }

        private static int GetEnvInt(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out int value) ? value : fallback;
        }
#endif
    }
}
