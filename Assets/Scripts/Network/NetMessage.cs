using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Novgov.Network
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

        // join_matchmaking / match_found : "deathmatch", "zone_control" ou "conquest"
        public string mode;

        // match_found / opponent_ghosted / turn_timer / match_over
        public string match_id;
        public int team_id;
        public string opponent_username;
        public int seconds_remaining;
        public string reason;
        public int winner_team;

        // join_matchmaking (mode="conquest") / match_found / zone_captured / zone_attack_result :
        // index de tuile Slippy Map (Zoom CityGenerator.ZONE_ZOOM) de la Zone de Conquête concernée.
        // join_matchmaking (mode="deathmatch"/"zone_control", 2026-08-30, "des milliers de cartes") :
        // tuile "domicile" du joueur (Novgov.Generation.ZoneManager.HomeTileX/Y côté client) si
        // has_home_tile est vrai — voir has_home_tile ci-dessous, ces deux champs ne veulent rien dire
        // seuls pour ce mode. match_found (deathmatch/zone_control) : tuile RÉELLEMENT retenue pour la
        // partie par le serveur (voir MatchSessionManager.TryStartMatch) — (0,0) signifie la carte
        // "Default" partagée, jamais une vraie tuile GPS (voir has_home_tile).
        public int zone_tile_x;
        public int zone_tile_y;

        // join_matchmaking (deathmatch/zone_control uniquement, 2026-08-30) : vrai si zone_tile_x/y
        // ci-dessus est une vraie tuile domicile du joueur. Champ séparé plutôt que de surcharger le
        // sentinel (0,0) — (0,0) est une tuile Slippy Map réelle (bien qu'improbable, en plein océan),
        // et ce champ devient une vraie donnée de matchmaking (pas seulement documentaire) une fois
        // que Deathmatch/Zone de Contrôle l'utilisent réellement (voir PlayerConnection.HasHomeTile).
        public bool has_home_tile;

        // zone_captured / zone_attack_result : résultat de la demande d'attaque/capture d'une Zone.
        public bool success;

        // match_over
        public int your_new_rating;
        public int rating_delta;

        // submit_turn
        public int turn_number;
        public UnitOrder[] orders;

        // turn_result
        public int snapshot_interval_ms;
        public Snapshot[] snapshots;

        // submit_deployment (client -> serveur) : placement manuel choisi par le joueur pendant la
        // phase de déploiement PvP, voir 03-network-protocol.md.
        public UnitPlacement[] placements;

        // deployment_result (serveur -> client) : positions FINALES validées par le serveur pour
        // les DEUX camps (zone de déploiement respectée, effectif autorisé) — voir
        // MatchSessionManager.RunDeploymentPhase. Le client ne fait jamais confiance à ses propres
        // positions candidates : il efface tout et respawn exactement cette liste (y compris pour
        // son propre camp, au cas où le serveur ait dû recadrer une position hors zone).
        public DeployedUnit[] deployed_units;

        // zone_geometry_ready (serveur -> client, correctif 2026-09-05 — voir 03-network-protocol.md
        // §équité géométrique) : le JSON Overpass BRUT exact que le serveur autoritaire a lui-même
        // utilisé pour générer la Zone (zone_tile_x/y ci-dessus) de cette partie. Envoyé UNE FOIS,
        // dès que la géométrie serveur est prête, à TOUS les joueurs concernés (jamais pour la carte
        // "Default" : elle est bundle identique des deux côtés, inutile de la faire transiter).
        //
        // AVANT ce message, chaque client refaisait sa PROPRE requête Overpass indépendante pour la
        // même tuile (CityGenerator.GenerateCity()) — rien ne garantissait que les bâtiments reçus
        // soient identiques à ceux que le serveur utilise pour arbitrer le combat (miroirs Overpass
        // différents, édition OSM survenue entre les deux requêtes). Le client applique maintenant
        // ce JSON tel quel (CityGenerator.LoadZoneFromServerData) au lieu de le redemander lui-même.
        public string city_data_json;

        // city_verify (client -> serveur, 2026-09-12, "équité géométrique, 2ème étage") : hash
        // déterministe (voir Novgov.TacticalCore.TacticalGridBuilder.ComputeBuildingListHash) de la
        // ville que CE client vient de générer localement, envoyé une fois sa carte prête, AVANT que
        // le déploiement ne s'ouvre réellement — voir MultiplayerMatchController.
        // LoadMatchMapThenOpenDeployment. city_building_count est purement informatif (logs serveur).
        public int city_building_hash;
        public int city_building_count;

        // city_verify_result (serveur -> client) : `success` (champ partagé, voir plus haut) vrai si
        // city_building_hash concordait avec MatchState.AuthoritativeCityHash — dans ce cas
        // city_buildings reste vide, rien à faire. Sinon, la structure de bâtiments AUTORITAIRE du
        // serveur (celle qui a réellement servi à figer ms.World, voir EnsureTileLoadedAndSnapshot)
        // est jointe intégralement (jamais un différentiel partiel — voir CityGenerator.
        // ApplyAuthoritativeBuildings : recréer une ville à partir d'un sous-ensemble laisserait des
        // bâtiments du côté divergent, l'objectif est une ville identique à 100%, pas rapiécée).
        public BuildingGeometryDto[] city_buildings;
    }

    /// <summary>Un point 2D (X, Z monde) — mêmes conventions que Novgov.TacticalCore.TacticalBuilding
    /// (footprint/portes/fenêtres en 2D, la hauteur/l'élévation étant portée séparément).</summary>
    [Serializable]
    public class Vector2Data
    {
        public float x, y;
    }

    /// <summary>Copie réseau de Novgov.TacticalCore.TacticalDoor — voir BuildingGeometryDto.</summary>
    [Serializable]
    public class DoorGeometryDto
    {
        public Vector2Data position;
        public Vector2Data entry_direction;
        public float width;
    }

    /// <summary>Copie réseau de Novgov.TacticalCore.TacticalWindow — voir BuildingGeometryDto. La
    /// hauteur réelle (Y) d'une fenêtre n'est PAS transmise : elle est recalculée côté client par une
    /// formule purement déterministe à partir de floor_level (voir CityGenerator.
    /// GenerateDoorsAndWindows, "floorY = 1.4f + f * 3.0f" — CityGenerator.ApplyAuthoritativeBuildings
    /// reproduit exactement cette même formule), pas la peine de faire transiter une valeur qui se
    /// recalcule à l'identique sans ambiguïté.</summary>
    [Serializable]
    public class WindowGeometryDto
    {
        public int id;
        public Vector2Data position;
        public Vector2Data outward_normal;
        public int floor_level;
    }

    /// <summary>Copie réseau intégrale de Novgov.TacticalCore.TacticalBuilding — voir
    /// "city_verify_result" ci-dessus et CityGenerator.ApplyAuthoritativeBuildings (côté client, seul
    /// consommateur). L'empreinte (footprint) ne porte que le contour EXTÉRIEUR (pas de trous
    /// intérieurs) — même simplification déjà acceptée par le cache disque L2 de TacticalGridBuilder
    /// (BuildingTemplateDto), rien de nouveau introduit ici.</summary>
    [Serializable]
    public class BuildingGeometryDto
    {
        public int id;
        public float height;
        public Vector2Data[] footprint;
        public DoorGeometryDto[] doors;
        public WindowGeometryDto[] windows;
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

        // Mode "zone_control" uniquement — 0 à 100, absent (0/0) en mode deathmatch.
        public float zone_progress_team1;
        public float zone_progress_team2;

        // Bâtiments détruits PENDANT CE TICK précis (2026-09-12) : index dans BuildingStructure.
        // AllBuildings, voir TacticalGridBuilder ("template.id") — jamais retiré de cette liste côté
        // client pendant un rejeu réseau (voir DestructibleEnvironment.ApplyNetworkDestruction),
        // pour que cet index reste valide pour tout le reste de la partie. Avant ce champ,
        // TacticalEvent.Kind.WallDestroyed n'avait AUCUN consommateur réseau — le bâtiment restait
        // visuellement intact chez les deux joueurs alors que le serveur le savait détruit (voir
        // §19.9.3 de 08-known-issues-and-todo.md). Null/vide la plupart des ticks.
        public int[] destroyed_building_ids;
    }

    [Serializable]
    public class UnitState
    {
        public string unit_id;
        public float x, y, z, ry;
        public int health;
        public bool dead;
        public bool shooting;

        // Retour visuel de combat (2026-09-11) : identifiant de la cible visée CE tick, présent
        // seulement quand "shooting" est vrai — permet au client de rejouer un vrai traceur/flash
        // vers la bonne unité (et un effet d'impact SUR elle) au lieu de piloter uniquement
        // l'Animator sans le moindre effet visible. Absent (chaîne vide) pour un tir de mortier — la
        // résolution y associe un faux tireur "mortar" sans unité réelle, voir MatchSessionManager_
        // CombatPure.ApplyAreaDamage.
        public string shoot_target_id;

        // Brouillard de guerre réseau (2026-08-30) : une unité ADVERSE n'apparaît plus jamais dans
        // "turn_result" tant qu'elle n'est pas repérée (voir MatchSessionManager.
        // ComputeVisibleUnitIds) — la première fois qu'elle l'est, elle n'existe pas encore côté
        // client (voir MultiplayerMatchController.PlaySnapshotsCoroutine), qui a donc besoin de son
        // type/camp pour la faire apparaître, pas seulement de sa position.
        public int unit_type;
        public int team_id;
    }

    /// <summary>Une unité que le joueur souhaite placer, envoyée dans "submit_deployment". Le
    /// serveur ne fait JAMAIS confiance à cette position telle quelle (voir MatchSessionManager.
    /// ResolveDeployment) : elle est recadrée dans la zone de déploiement légale du camp avant
    /// d'être réellement instanciée.</summary>
    [Serializable]
    public class UnitPlacement
    {
        // Valeur brute de UnitSpawnerUI.UnitType (0=Fantassin, 1=CharLeopard, 2=VehiculeCanon,
        // 3=Mortier, 4=BarricadeRoutiere).
        public int unit_type;
        public float x, y, z;
    }

    /// <summary>Une unité réellement déployée après résolution serveur (diffusée dans
    /// "deployment_result", aux DEUX clients, pour les DEUX camps).</summary>
    [Serializable]
    public class DeployedUnit
    {
        public string unit_id;
        public int unit_type;
        public int team_id;
        public float x, y, z;
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
        /// Taille max par défaut (gameplay, connexion déjà authentifiée) : les snapshots/ordres
        /// tiennent largement dedans, voir 03-network-protocol.md.
        /// </summary>
        public const int DefaultMaxMessageSize = 8 * 1024 * 1024;

        /// <summary>
        /// Bloque jusqu'à réception d'un message complet, ou lève une exception si la connexion est fermée.
        /// </summary>
        /// <param name="maxLength">
        /// Plafond appliqué à la longueur ANNONCÉE par l'en-tête, avant même de lire le corps —
        /// pour le handshake pré-authentification, GameServerBootstrap passe une valeur bien plus
        /// petite que la valeur par défaut : sans ça, n'importe quel socket non authentifié peut
        /// annoncer jusqu'à 8 Mo et faire allouer ce buffer côté serveur avant d'envoyer le moindre
        /// octet du corps, sans jamais se connecter réellement — quelques centaines de sockets
        /// simultanés suffisent à épuiser la RAM du VPS avant toute vérification du JWT.
        /// </param>
        public static NetMessage ReadMessage(NetworkStream stream, int maxLength = DefaultMaxMessageSize)
        {
            byte[] header = ReadExact(stream, 4);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > maxLength)
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
