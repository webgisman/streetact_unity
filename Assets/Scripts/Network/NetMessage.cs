using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace StreetAct.Network
{
    /// <summary>
    /// Enveloppe unique pour tous les messages du protocole (voir Assets/_ServerDocs/multiplayer/03-network-protocol.md).
    /// JsonUtility ne supporte ni le polymorphisme ni les dictionnaires : on utilise une classe plate
    /// avec un champ "type" discriminant, et les champs non pertinents pour un type donné restent à leur valeur par défaut.
    /// </summary>
    [Serializable]
    public class NetMessage
    {
        public string type;

        // auth
        public string access_token;

        // match_found / opponent_ghosted / turn_timer / match_over
        public string match_id;
        public int team_id;
        public string opponent_username;
        public int seconds_remaining;
        public string reason;
        public int winner_team;

        // submit_turn
        public int turn_number;
        public UnitOrder[] orders;

        // turn_result
        public int snapshot_interval_ms;
        public Snapshot[] snapshots;
    }

    [Serializable]
    public class UnitOrder
    {
        public string unit_id;
        public PathNode[] path;
    }

    [Serializable]
    public class PathNode
    {
        public float x, y, z;
        public int action;
    }

    [Serializable]
    public class Snapshot
    {
        public int t;
        public UnitState[] units;
    }

    [Serializable]
    public class UnitState
    {
        public string unit_id;
        public float x, y, z, ry;
        public int health;
        public bool dead;
        public bool shooting;
    }

    /// <summary>
    /// Framing des messages sur le socket TCP : 4 octets (longueur, little-endian) + JSON UTF-8.
    /// Utilisé identiquement côté client et côté serveur.
    /// </summary>
    public static class NetFraming
    {
        public static void WriteMessage(NetworkStream stream, NetMessage msg)
        {
            string json = JsonUtility.ToJson(msg);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] header = BitConverter.GetBytes(payload.Length);
            stream.Write(header, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>
        /// Bloque jusqu'à réception d'un message complet, ou lève une exception si la connexion est fermée.
        /// </summary>
        public static NetMessage ReadMessage(NetworkStream stream)
        {
            byte[] header = ReadExact(stream, 4);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 8 * 1024 * 1024)
                throw new IOException($"Taille de message invalide reçue : {length}");

            byte[] payload = ReadExact(stream, length);
            string json = Encoding.UTF8.GetString(payload);
            return JsonUtility.FromJson<NetMessage>(json);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("Connexion fermée par le pair pendant la lecture.");
                offset += read;
            }
            return buffer;
        }
    }
}
