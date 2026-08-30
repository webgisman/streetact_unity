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

        public PlayerConnection(TcpClient client, NetworkStream stream, string userId)
        {
            TcpClient = client;
            Stream = stream;
            UserId = userId;
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
            try { Stream?.Close(); } catch { }
            try { TcpClient?.Close(); } catch { }
        }
    }
}
