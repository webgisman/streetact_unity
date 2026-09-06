using System;
using System.Collections;
using System.Collections.Generic;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Orchestre les parties Deathmatch/Zone de Contrôle (concurrentes, en donnée pure — voir
    /// MatchState.cs et 04-unity-headless-server.md, "Option B" 2026-08-30 — un seul processus en
    /// fait tourner des centaines/milliers en parallèle) ET les combats de Conquête (INCHANGÉS,
    /// toujours 1 combat à la fois, basés sur de vraies UnitAI/BuildingStructure — voir
    /// RunConquestSkirmish). "Portée V1" mentionnée par endroits ci-dessous dans le code réfère à
    /// l'ancienne contrainte "un seul match total sur le processus" — dépassée pour Deathmatch/
    /// Zone de Contrôle, toujours vraie pour la Conquête seule.
    ///
    /// Limitation connue, toujours vraie : pas de reprise de partie après coupure TCP complète — un
    /// joueur qui se déconnecte puis se reconnecte rejoint la file d'attente pour un NOUVEAU match,
    /// il ne réintègre pas la partie en cours (qui continue avec son camp en mode Ghost jusqu'à la
    /// victoire/défaite).
    /// </summary>
    public partial class MatchSessionManager : MonoBehaviour
    {
        private const float PlanningSeconds = 60f;
        // 2026-09-06 : 45s -> 300s (5 min) sur demande explicite — trop court pour un vrai joueur qui
        // découvre son dock de placement, ça se terminait par un repli automatique (voir
        // AutoDeployTeamFallbackPure) ressenti comme une action "automatique" imposée sans prévenir.
        // Le repli reste nécessaire en dernier recours (sinon un adversaire réellement AFK/déconnecté
        // bloquerait indéfiniment la partie de l'autre joueur), mais avec cette marge il ne devrait
        // plus jamais se déclencher en usage normal — seulement pour une partie vraiment abandonnée.
        private const float DeploymentSeconds = 300f;
        // Le chargement procédural de la ville côté client (génération OSM) peut à lui seul
        // consommer une bonne partie, voire la totalité, du timer de déploiement ci-dessus —
        // sans ce filet, un joueur dont le chargement traînait n'avait JAMAIS l'occasion de voir son
        // propre dock de placement manuel avant le repli automatique (voir rapport de bug "unités
        // bleues et rouges qui apparaissent d'un coup"). Le VRAI compte à rebours de déploiement ne
        // démarre donc qu'une fois les DEUX clients prêts (message "deployment_ready"), avec ce
        // plafond en filet de sécurité si l'un d'eux ne répond jamais (chargement en échec, etc.).
        // 2026-09-06 : 60s -> 300s (5 min), même demande/même raison que DeploymentSeconds ci-dessus.
        private const float MapReadyMaxWaitSeconds = 300f;

        private const int ZoneControlTurnCap = 20;
        // Sans plafond, deux joueurs qui se contentent de se cacher chaque tour pouvaient faire
        // durer une partie indéfiniment (occupant inutilement une connexion/de la mémoire pour
        // rien, même si d'autres parties concurrentes ne sont plus bloquées par ça depuis l'Option
        // B) — le Deathmatch avait ce plafond en Zone de Contrôle mais pas ici.
        private const int DeathmatchTurnCap = 60;

        // Abaissé de 200 à 40 le 2026-09-02 (correctif "prend le raccourci") : chaque checkpoint
        // déclenche maintenant une vraie recherche A* (TacticalResolver.ExpandOrder, voir
        // Pathfinding.cs) pour suivre fidèlement la géométrie au lieu d'une simple ligne droite —
        // un client modifié soumettant 200 checkpoints par unité pouvait donc désormais déclencher
        // jusqu'à 200 recherches A* par unité par tour (coût réel, avant ce correctif un nœud
        // n'était qu'un segment de ligne droite, quasi gratuit). Un joueur légitime n'en pose
        // jamais plus de quelques-uns par tour (budget de mouvement de 50m) — 40 reste très
        // largement au-dessus de tout usage réel tout en bornant le pire cas.
        private const int MaxOrderPathNodes = 40;

        // Connexions authentifiées mais dont on n'a pas encore reçu "join_matchmaking" (donc dont
        // on ne connaît pas encore le mode voulu).
        private readonly List<PlayerConnection> pendingMode = new List<PlayerConnection>();

        // Une file d'attente séparée par mode : deux joueurs ne sont appariés que s'ils ont
        // demandé le même mode.
        private readonly List<PlayerConnection> waitingDeathmatch = new List<PlayerConnection>();
        private readonly List<PlayerConnection> waitingZoneControl = new List<PlayerConnection>();

        // Portée du verrou réduite le 2026-08-30 ("Option B", voir MatchState.cs) : ne protège plus
        // qu'un combat de Conquête (RunConquestSkirmish, toujours 1-à-la-fois, hors scope de ce
        // chantier) contre l'instant bref où une NOUVELLE partie Deathmatch/Zone de Contrôle fige sa
        // géométrie de départ (voir RunMatch) — les parties DÉJÀ démarrées ne le touchent plus jamais
        // ensuite, elles tournent en parallèle les unes des autres ET d'un combat de Conquête en
        // cours. Nommage conservé (matchInProgress) pour ne pas complexifier la Conquête (hors scope),
        // qui continue de le tenir pour toute la durée d'un combat exactement comme avant.
        private bool matchInProgress = false;

        // Vrai tant que la scène du serveur affiche la carte par défaut hors-ligne (deathmatch/
        // zone_control). Un combat de conquête (RunConquestSkirmish) remplace temporairement cette
        // géométrie par celle de la Zone réellement attaquée (LoadZoneOnServer) — sans restauration,
        // le PROCHAIN deathmatch/zone_control voulant la carte "Default" (ou une tuile GPS jamais
        // encore vue, voir GenerateAndCacheTile) hériterait silencieusement de cette dernière Zone
        // au lieu de la carte attendue — la scène reste un singleton partagé, MAIS depuis l'Option B
        // (2026-08-30) seul l'instant bref de génération/snapshot d'une partie y touche encore ;
        // une partie déjà démarrée (son MatchState.World déjà figé) n'est plus jamais affectée par
        // ce que la scène affiche ensuite.
        private bool defaultMapLoaded = true;

        private void Start()
        {
            StartCoroutine(SetupWorldOnce());
            StartCoroutine(InstanceHeartbeatLoop());
        }

        // =====================================================================
        // Registre d'instances (table "server_instances", schema.sql §7) — Pool multi-serveurs :
        // cette instance annonce périodiquement son état (libre/occupée, nombre de joueurs en
        // attente par mode) pour que les clients choisissent la bonne instance AVANT de se
        // connecter (voir MultiplayerMatchController.PickServerInstance côté client). Remplace le
        // modèle "un seul serveur pour tout le monde" (portée V1 initiale) par un pool d'instances
        // indépendantes, chacune ne gérant toujours qu'UN match à la fois en interne (le code de
        // simulation lui-même n'a pas changé).
        // =====================================================================
        private const float InstanceHeartbeatSeconds = 2f;

        private IEnumerator InstanceHeartbeatLoop()
        {
            while (true)
            {
                yield return ReportInstanceStatus();
                yield return new WaitForSeconds(InstanceHeartbeatSeconds);
            }
        }

        /// <summary>Upsert immédiat (pas d'attente du prochain tick) de l'état de cette instance —
        /// appelé à chaque fois que l'état change réellement (un joueur rejoint une file d'attente,
        /// un match démarre/se termine) pour réduire la fenêtre où deux joueurs pourraient être
        /// envoyés chacun vers une instance différente avant que l'état à jour soit visible.</summary>
        private IEnumerator ReportInstanceStatus()
        {
            string status = matchInProgress ? "busy" : "free";
            string nowIso = DateTime.UtcNow.ToString("o");
            string json = "{\"id\":\"" + GameServerBootstrap.InstanceId + "\"" +
                          ",\"public_port\":" + GameServerBootstrap.PublicPort +
                          ",\"status\":\"" + status + "\"" +
                          ",\"waiting_deathmatch\":" + waitingDeathmatch.Count +
                          ",\"waiting_zone_control\":" + waitingZoneControl.Count +
                          ",\"updated_at\":\"" + nowIso + "\"}";
            yield return PostgrestUpsert("/server_instances", json);
        }

        private IEnumerator SetupWorldOnce()
        {
            yield return null; // laisser les Awake/Start des autres composants de la scène s'exécuter d'abord

            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
            if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            // Invalide le cache de géométrie (murs/grille, voir TacticalGridBuilder) : une ville
            // fraîchement (re)générée a des bâtiments totalement différents, réutiliser l'ancien
            // cache donnerait des murs au mauvais endroit sur la nouvelle carte.
            TacticalGridBuilder.InvalidateCache();

            MeasureBarricadeHalfWidth();

            Debug.Log("[MatchSessionManager] Carte par défaut (hors-ligne) chargée pour le serveur.");
        }

        /// <summary>Mesure UNE SEULE FOIS, au démarrage du serveur, la demi-largeur réelle d'une
        /// barricade (Road_barrier) — nécessaire car les parties Deathmatch/Zone de Contrôle (voir
        /// MatchState, "Option B" 2026-08-30) ne créent plus jamais de vrai RoadBarrier/BoxCollider :
        /// une barricade y est un segment Barricade{p1,p2} en donnée pure (voir
        /// ResolveDeploymentPure), qui doit occuper exactement le même espace au sol que la version
        /// Conquête/Solo (RoadBarrier.Start(), dimensions dérivées du mesh réel) pour un blocage de
        /// ligne de vue/déplacement cohérent — jamais une valeur devinée.</summary>
        private void MeasureBarricadeHalfWidth()
        {
            GameObject prefab = Resources.Load<GameObject>("Road_barrier");
            if (prefab == null) return; // repli sur MatchState.BarricadeHalfWidthMeters par défaut (2f)

            // Recalcule directement les bornes depuis les Renderers (même math que RoadBarrier.
            // Start(), lignes 32-48) SANS passer par le cycle de vie MonoBehaviour normal (Start() ne
            // s'exécuterait qu'à la frame SUIVANTE pour un objet fraîchement instancié — inutilisable
            // ici, cette méthode doit renvoyer un résultat immédiatement, en plein milieu d'une frame
            // de démarrage serveur).
            GameObject probe = Instantiate(prefab, new Vector3(0f, -9999f, 0f), Quaternion.identity);
            probe.transform.localScale = Vector3.one * 1.3f; // même échelle que UnitSpawnerUI.SpawnUnitAt

            Bounds bounds = new Bounds(probe.transform.position, Vector3.zero);
            bool hasBounds = false;
            foreach (Renderer r in probe.GetComponentsInChildren<Renderer>())
            {
                if (r.gameObject.name.Contains("Health")) continue;
                if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                else bounds.Encapsulate(r.bounds);
            }

            if (hasBounds)
            {
                float sizeX = Mathf.Max(2.0f, bounds.size.x / Mathf.Max(0.01f, probe.transform.lossyScale.x));
                MatchState.BarricadeHalfWidthMeters = (sizeX * probe.transform.lossyScale.x) * 0.5f;
            }
            Destroy(probe);
        }

        private void Update()
        {
            while (GameServerBootstrap.AuthenticatedConnections.TryDequeue(out PlayerConnection conn))
            {
                pendingMode.Add(conn);
            }

            // Lire "join_matchmaking" pour connaître le mode voulu, puis basculer la connexion
            // dans la file du mode correspondant. Par rétrocompatibilité (client plus ancien sans
            // champ "mode"), une valeur absente/vide vaut "deathmatch".
            for (int i = pendingMode.Count - 1; i >= 0; i--)
            {
                PlayerConnection conn = pendingMode[i];
                while (conn.TryDequeueMessage(out NetMessage msg))
                {
                    if (msg.type != "join_matchmaking") continue;
                    conn.Mode = string.IsNullOrEmpty(msg.mode) ? "deathmatch" : msg.mode;
                    // Tuile "domicile" du joueur (2026-08-30, "des milliers de cartes") — voir
                    // NetMessage.has_home_tile. Pertinent seulement pour deathmatch/zone_control ; la
                    // Conquête a déjà son propre usage de zone_tile_x/y (HandleConquestMessage), non
                    // affecté par ce champ.
                    conn.HasHomeTile = msg.has_home_tile;
                    conn.HomeTileX = msg.zone_tile_x;
                    conn.HomeTileY = msg.zone_tile_y;
                    pendingMode.RemoveAt(i);

                    // La conquête n'est PAS un appariement aléatoire entre deux joueurs en file
                    // d'attente : c'est une demande ciblée sur une Zone précise (tileX,tileY),
                    // résolue immédiatement (capture instantanée ou combat contre une garnison IA).
                    // Voir HandleConquestMessage.
                    if (conn.Mode == "conquest")
                    {
                        HandleConquestMessage(conn, msg);
                        break;
                    }

                    // Entraînement contre l'IA (2026-09-02) : comme la Conquête, ce n'est PAS un
                    // appariement entre deux joueurs en file d'attente — un joueur SEUL déclenche
                    // une partie immédiate contre une garnison IA sur la carte par défaut, pour
                    // patienter pendant qu'il reste éligible à un vrai adversaire (le client
                    // referme sa connexion en file et en ouvre une nouvelle dédiée à l'entraînement,
                    // voir MultiplayerMatchController.StartPracticeVsAI côté client — cette
                    // connexion-ci n'a donc jamais été ajoutée à waitingDeathmatch/ZoneControl).
                    // Voir HandlePracticeAiMessage.
                    if (conn.Mode == "practice_ai")
                    {
                        HandlePracticeAiMessage(conn);
                        break;
                    }

                    var targetList = conn.Mode == "zone_control" ? waitingZoneControl : waitingDeathmatch;
                    targetList.Add(conn);
                    Debug.Log($"[MatchSessionManager] Joueur en file d'attente ({conn.Mode}) : {conn.UserId} ({targetList.Count} en attente)");
                    // Rapport immédiat (pas d'attente du prochain heartbeat) : un second joueur qui
                    // choisit une instance dans la seconde qui suit doit voir qu'il y a déjà
                    // quelqu'un en attente ici pour le même mode, voir ReportInstanceStatus.
                    StartCoroutine(ReportInstanceStatus());
                    break;
                }
            }

            // Nettoyer les connexions perdues avant même d'avoir rejoint un match.
            pendingMode.RemoveAll(c => c.IsDisconnected);
            waitingDeathmatch.RemoveAll(c => c.IsDisconnected);
            waitingZoneControl.RemoveAll(c => c.IsDisconnected);

            // Depuis l'Option B (2026-08-30, voir MatchState.cs) : PLUS de garde matchInProgress ici
            // — de nouvelles parties Deathmatch/Zone de Contrôle peuvent démarrer à tout instant,
            // même pendant que d'autres tournent déjà (elles ne partagent plus aucun état mutable
            // entre elles). matchInProgress protège seulement, à l'intérieur de RunMatch, l'instant
            // bref où une partie fige sa géométrie de départ contre un combat de Conquête concurrent.
            TryStartMatch(waitingDeathmatch);
            TryStartMatch(waitingZoneControl);
        }
    }
}
