using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using StreetAct.Network;
using UnityEngine;

namespace StreetAct.Server
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
            Debug.Log($"[GameServerBootstrap] Serveur de jeu StreetAct à l'écoute sur le port {port}.");

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
                NetMessage first = NetFraming.ReadMessage(stream);

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

                client.ReceiveTimeout = 0;
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
