using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using StreetAct.Network;
using UnityEngine;

namespace StreetAct.Server
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
        public DateTime LastHeartbeat = DateTime.UtcNow;
        public bool HasSubmittedThisTurn = false;
        public UnitOrder[] PendingOrders = Array.Empty<UnitOrder>();

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
