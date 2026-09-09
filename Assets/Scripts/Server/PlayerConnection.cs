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
        public string Mode; // "deathmatch" ou "zone_control", lu depuis join_matchmaking
        // Tuile Slippy Map "domicile" du joueur (voir Novgov.Generation.ZoneManager côté client,
        // calculée UNE FOIS via GPS puis jamais réinterrogée) — lue depuis join_matchmaking
        // (NetMessage.has_home_tile/zone_tile_x/y) pour Deathmatch/Zone de Contrôle, 2026-08-30
        // ("des milliers de cartes"). Absent (HasHomeTile=false) = joueur sans position GPS connue,
        // repli sur la tuile de l'adversaire ou sur "Default" (voir MatchSessionManager.TryStartMatch).
        public bool HasHomeTile = false;
        public int HomeTileX;
        public int HomeTileY;
        public int NewRating; // rempli par MatchSessionManager.UpdateRatings() en fin de partie
        public int RatingDelta;
        public DateTime LastHeartbeat = DateTime.UtcNow;
        public bool HasSubmittedThisTurn = false;
        public UnitOrder[] PendingOrders = Array.Empty<UnitOrder>();
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
        // SIGNAL DE VIE SERVEUR -> CLIENT, CENTRALISÉ (2026-09-07)
        //
        // POURQUOI CE MÉCANISME EXISTE, ET POURQUOI IL EST ICI ET PAS DANS UNE PHASE.
        // Le client applique un ReceiveTimeout FINI de 25s à sa socket (GameServerClient.Connect) :
        // 25 secondes sans le moindre octet reçu et son thread de lecture lève, ce qui le déconnecte
        // avec "connexion perdue". Or le serveur passait de longues périodes à n'envoyer STRICTEMENT
        // RIEN :
        //   - un joueur seul dans waitingDeathmatch/waitingZoneControl (MatchSessionManager.Update)
        //     n'était l'objet d'aucun envoi tant qu'aucun adversaire ne se présentait — un joueur qui
        //     attendait plus de 25s était donc TOUJOURS éjecté avant de pouvoir être apparié. C'est
        //     la raison pour laquelle Deathmatch/Zone de Contrôle étaient injouables en pratique dès
        //     qu'un second joueur ne rejoignait pas la file dans les 25 secondes ;
        //   - entre l'appariement et match_found, RunMatch récupère deux pseudos par HTTP puis peut
        //     générer une tuile inédite (Overpass + bake NavMesh, jusqu'à ~60s) sans rien émettre ;
        //   - la Conquête et l'entraînement contre l'IA n'avaient aucun signal de vie du tout.
        // La phase de déploiement, elle, avait bien reçu son propre signal de vie (correctif du
        // 2026-09-06) — mais LOCAL à cette phase. C'est exactement l'erreur de conception à ne pas
        // reproduire : chaque nouvelle phase devait penser à réimplémenter son keepalive, et trois
        // d'entre elles ne l'avaient pas fait.
        //
        // Le signal de vie est donc désormais une propriété de la CONNEXION, pas d'une phase :
        // toute connexion authentifiée vivante reçoit un "heartbeat" dès qu'elle est restée
        // silencieuse trop longtemps, quel que soit ce que le serveur est en train de faire — y
        // compris dans une phase qui n'existe pas encore. Un heartbeat serveur->client n'a besoin
        // d'aucun traitement côté client (son switch l'ignore, voir
        // MultiplayerMatchController.HandleServerMessage) : c'est l'ARRIVÉE de la trame qui réarme
        // le timeout de la socket, pas son contenu.
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
