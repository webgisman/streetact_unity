using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Représente une connexion TCP authentifiée d'un joueur, côté serveur.
    /// La lecture réseau tourne sur un thread dédié ; MatchSessionManager consomme la file
    /// depuis le thread principal Unity (coroutines) pour rester thread-safe.
    /// </summary>
    public class PlayerConnection
    {
        public readonly TcpClient TcpClient;
        public readonly NetworkStream Stream;
        public readonly string UserId;
        public string Username = "Joueur";

        public int TeamId; // assigné par MatchSessionManager au début d'un match
        public string Mode; // "conquest" ou "siege" (bataille de siège), lu depuis join_matchmaking
        public int NewRating; // rempli par MatchSessionManager.UpdateRatings() en fin de partie
        public int RatingDelta;
        public DateTime LastHeartbeat = DateTime.UtcNow;
        public bool HasSubmittedThisTurn = false;
        public UnitOrder[] PendingOrders = Array.Empty<UnitOrder>();
        // Accusé de réception d'un "turn_result" par CE client (voir MatchSessionManager_
        // CombatRealEngine.RunExecutionPhaseRealEngine) — sert de barrière avant l'envoi de
        // "turn_playback_start", pour que les deux joueurs commencent à rejouer le même tour
        // au même instant plutôt que chacun dès que SON PROPRE turn_result (taille différente
        // selon le brouillard de guerre, donc temps de réception différent) lui est arrivé.
        public bool HasAckedTurnResult = false;
        public bool HasSubmittedDeployment = false;
        public UnitPlacement[] PendingDeployment = null;
        // Vrai dès que CE client a fini de charger la carte et affiche réellement le dock de
        // déploiement (message "deployment_ready", voir MultiplayerMatchController.OpenDeploymentDock)
        // — le compte à rebours de 45s du déploiement n'attend plus qu'un joueur qui n'a même pas
        // encore vu son propre dock à l'écran (voir MatchSessionManager.RunDeploymentPhase).
        public bool MapReady = false;

        private readonly ConcurrentQueue<NetMessage> incoming = new ConcurrentQueue<NetMessage>();
        private readonly object sendLock = new object();
        private Thread receiveThread;
        private volatile bool disconnected = false;

        public bool IsDisconnected => disconnected;

        // ---------------------------------------------------------------------------------------
        // SIGNAL DE VIE SERVEUR -> CLIENT
        //
        // Le client coupe sa connexion au bout de 25 s sans recevoir un seul octet (ReceiveTimeout,
        // GameServerClient.Connect). Le serveur, lui, peut rester longtemps sans rien envoyer : salle
        // d'attente d'un siège, chargement d'une carte, planification. Toute connexion authentifiée
        // restée silencieuse reçoit donc un "heartbeat", quelle que soit la phase en cours — c'est une
        // propriété de la CONNEXION, pas d'une phase (trois phases avaient oublié leur propre
        // keepalive avant le 2026-09-07). Le client ignore son contenu : seule l'arrivée compte.
        private static readonly ConcurrentDictionary<PlayerConnection, byte> LiveConnections =
            new ConcurrentDictionary<PlayerConnection, byte>();

        /// <summary>Intervalle entre deux signaux de vie. Volontairement à ~1/3 du ReceiveTimeout
        /// client (25s) : deux trames consécutives peuvent se perdre sans que le joueur saute.
        /// L'ancien intervalle local à la phase de déploiement était de 15s, soit moins de deux
        /// fois la marge — un seul hoquet réseau suffisait à rejouer la déconnexion qu'il corrigeait.</summary>
        public const double KeepaliveIntervalSeconds = 8.0;

        private DateTime lastSentUtc = DateTime.UtcNow;

        /// <summary>Envoie un signal de vie à TOUTE connexion authentifiée restée silencieuse plus
        /// de <see cref="KeepaliveIntervalSeconds"/>, et oublie les connexions mortes. Appelé une
        /// fois par frame par MatchSessionManager.Update, depuis le thread principal Unity.</summary>
        public static void PumpKeepalives()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var entry in LiveConnections)
            {
                PlayerConnection conn = entry.Key;
                if (conn.disconnected)
                {
                    LiveConnections.TryRemove(conn, out _);
                    continue;
                }
                if ((now - conn.lastSentUtc).TotalSeconds >= KeepaliveIntervalSeconds)
                {
                    // Send met lastSentUtc à jour — y compris quand un vrai message vient d'être
                    // envoyé par ailleurs, ce qui évite d'ajouter un heartbeat inutile juste après
                    // un turn_timer.
                    conn.Send(new NetMessage { type = "heartbeat" });
                }
            }
        }

        public PlayerConnection(TcpClient client, NetworkStream stream, string userId)
        {
            TcpClient = client;
            Stream = stream;
            UserId = userId;
            LiveConnections.TryAdd(this, 0);
        }

        public void StartReceiving()
        {
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (!disconnected)
                {
                    NetMessage msg = NetFraming.ReadMessage(Stream);
                    incoming.Enqueue(msg);
                }
            }
            catch (Exception)
            {
                // Connexion perdue — traité via IsDisconnected par le thread principal.
            }
            disconnected = true;
        }

        public bool TryDequeueMessage(out NetMessage msg) => incoming.TryDequeue(out msg);

        public void Send(NetMessage msg)
        {
            if (disconnected) return;
            lock (sendLock)
            {
                try
                {
                    NetFraming.WriteMessage(Stream, msg);
                    // Tout envoi réel repousse d'autant le prochain signal de vie (voir PumpKeepalives).
                    lastSentUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayerConnection] Échec d'envoi à {UserId} : {ex.Message}");
                    disconnected = true;
                }
            }
        }

        public void Close()
        {
            disconnected = true;
            LiveConnections.TryRemove(this, out _);
            try { Stream?.Close(); } catch { }
            try { TcpClient?.Close(); } catch { }
        }
    }
}
