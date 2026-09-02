using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;
using UnityEngine.Networking;

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
    public class MatchSessionManager : MonoBehaviour
    {
        private const float PlanningSeconds = 60f;
        private const float DeploymentSeconds = 45f;
        // Le chargement procédural de la ville côté client (génération OSM) peut à lui seul
        // consommer une bonne partie, voire la totalité, du timer de déploiement de 45s ci-dessus —
        // sans ce filet, un joueur dont le chargement traînait n'avait JAMAIS l'occasion de voir son
        // propre dock de placement manuel avant le repli automatique (voir rapport de bug "unités
        // bleues et rouges qui apparaissent d'un coup"). Le VRAI compte à rebours de déploiement ne
        // démarre donc qu'une fois les DEUX clients prêts (message "deployment_ready"), avec ce
        // plafond en filet de sécurité si l'un d'eux ne répond jamais (chargement en échec, etc.).
        private const float MapReadyMaxWaitSeconds = 60f;
        // Mêmes points d'ancrage que UnitSpawnerUI.AutoDeployBattlefield/AutoDeployTeamFallback —
        // le rayon délimite la zone légale où un placement manuel soumis via "submit_deployment"
        // est accepté (voir ClampToDeploymentZone) ; au-delà, la position est ramenée sur le bord
        // de la zone plutôt que rejetée en bloc, pour rester tolérant à une imprécision de tap tout
        // en empêchant un client modifié de déployer au contact immédiat de l'adversaire.
        private static readonly Vector3 Team1DeploymentZoneCenter = new Vector3(-25f, 0f, -25f);
        private static readonly Vector3 Team2DeploymentZoneCenter = new Vector3(25f, 0f, 25f);
        private const float DeploymentZoneRadius = 22f;
        // Budget de déploiement manuel : jusqu'à 4 unités de combat (n'importe quel mélange parmi
        // Fantassin/CharLeopard/VehiculeCanon/Mortier) + jusqu'à 8 barricades (même stock que le
        // dock solo, voir UnitSpawnerUI.maxBarricadesPerTeam) — au-delà, ou un type d'unité hors de
        // l'enum, la soumission ENTIÈRE est rejetée et ce camp reçoit le repli automatique.
        private const int MaxDeployedCombatUnits = 6;
        private const int MaxDeployedBarricades = 8;
        // Budget en points (voir UnitTypeStats.DeploymentCost) : plafonne la PUISSANCE
        // totale déployée, pas seulement le nombre d'unités — sans ça, déployer le nombre max
        // d'unités les plus lourdes (CharLeopard) était toujours strictement supérieur à toute
        // composition mixte, tuant toute variété tactique (voir rapport d'audit jouabilité, défaut
        // bloquant #1). 8 points permet par ex. 2 CharLeopard + 2 Fantassin, ou 4 VehiculeCanon, ou
        // 1 CharLeopard + 1 Mortier + 1 VehiculeCanon + 1 Fantassin — mais jamais 4 CharLeopard (12).
        private const int CombatPointBudget = 8;
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

        private string currentMatchMode = "deathmatch";

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
        // Nombre de parties Deathmatch/Zone de Contrôle RÉELLEMENT en cours sur ce processus — sert
        // uniquement à la télémétrie (logs), matchInProgress ne reflète plus cette notion depuis
        // l'Option B.
        private int activeMatchCount = 0;

        // "Des milliers de cartes" (2026-08-30) — voir EnsureTileLoadedAndSnapshot :
        // anti-doublon (une tuile neuve visée par deux parties en même temps n'est générée qu'une
        // fois) et anti-abus (limite le rythme de génération de tuile neuve par utilisateur, seul
        // moyen aujourd'hui pour un client de déclencher un travail coûteux — fetch OSM + bake NavMesh
        // synchrone qui gèle tout le processus — côté serveur sans validation, contrairement à la
        // Conquête qui vérifie la propriété en base).
        private readonly Dictionary<string, Coroutine> tileGenerationInFlight = new Dictionary<string, Coroutine>();
        private readonly Dictionary<string, DateTime> lastTileGenerationByUser = new Dictionary<string, DateTime>();
        private const float TileGenerationCooldownSeconds = 30f;

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

        private void TryStartMatch(List<PlayerConnection> queue)
        {
            if (queue.Count < 2) return;

            // queue[0]/queue[1] peuvent partager le même UserId si un joueur se connecte deux fois
            // avec le même JWT (deux appareils, ou un script) — sans cette vérification, il pouvait
            // s'apparier contre lui-même, contrôler les deux camps et forcer un résultat pour farmer
            // de l'ELO (voir rapport d'audit §1.4). On cherche la première paire d'UserId distincts
            // dans la file plutôt que de toujours prendre les deux premiers en position.
            int i1 = 0, i2 = -1;
            for (int j = 1; j < queue.Count; j++)
            {
                if (queue[j].UserId != queue[i1].UserId) { i2 = j; break; }
            }
            if (i2 < 0) return; // toute la file en attente est le même joueur en double — on patiente

            PlayerConnection p1 = queue[i1];
            PlayerConnection p2 = queue[i2];
            queue.RemoveAt(i2);
            queue.RemoveAt(i1);
            var (cacheKey, owningUserId) = DetermineMatchCacheKey(p1, p2);
            activeMatchCount++;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunMatchGuarded(p1, p2, cacheKey, owningUserId));
        }

        /// <summary>Choisit la tuile Slippy Map de la partie (2026-08-30, "des milliers de cartes") —
        /// celle de p1 (premier arrivé en file) par défaut ; bascule sur celle de p2 UNIQUEMENT si
        /// elle est déjà en cache et pas celle de p1, pour garder les démarrages de partie rapides le
        /// plus souvent possible (voir TacticalGridBuilder.IsTileCached). "Default" si aucun des deux
        /// n'a de tuile domicile connue (jamais eu de position GPS, voir NetMessage.has_home_tile).
        /// owningUserId sert uniquement à l'anti-abus (EnsureTileLoadedAndSnapshot) : le joueur dont
        /// la tuile a été retenue, jamais nul sauf repli sur Default.</summary>
        private static (string cacheKey, string owningUserId) DetermineMatchCacheKey(PlayerConnection p1, PlayerConnection p2)
        {
            string p1Key = p1.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p1.HomeTileX}_{p1.HomeTileY}" : null;
            string p2Key = p2.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p2.HomeTileX}_{p2.HomeTileY}" : null;

            if (p1Key == null && p2Key == null) return ("Default", null);
            if (p1Key == null) return (p2Key, p2.UserId);
            if (p2Key == null) return (p1Key, p1.UserId);

            bool p1Cached = TacticalGridBuilder.IsTileCached(p1Key);
            bool p2Cached = TacticalGridBuilder.IsTileCached(p2Key);
            if (p2Cached && !p1Cached) return (p2Key, p2.UserId);
            return (p1Key, p1.UserId);
        }

        /// <summary>
        /// Enveloppe RunMatch() pour qu'une exception non prévue (message client malformé,
        /// coordonnée invalide, etc.) n'abandonne jamais CETTE partie dans un état incohérent, et
        /// ne laisse jamais matchInProgress bloqué à "true" pour toujours si le crash survient
        /// pendant la section critique brève de RunMatch (voir EnsureTileLoadedAndSnapshot) — ce qui
        /// bloquerait alors le démarrage de TOUTE nouvelle partie sur ce processus, pas seulement
        /// celle-ci. `yield return` n'est pas autorisé dans un bloc try/catch en C#, d'où ce
        /// pompage manuel de l'énumérateur plutôt qu'un try/catch direct autour du corps de RunMatch.
        /// </summary>
        private IEnumerator RunMatchGuarded(PlayerConnection p1, PlayerConnection p2, string cacheKey, string owningUserId)
        {
            IEnumerator inner = RunMatch(p1, p2, cacheKey, owningUserId);
            while (true)
            {
                // "yield break"/"yield return" ne sont pas autorisés à l'intérieur d'un bloc
                // catch (ni d'un try qui a un catch) en C# — on capture juste l'échec ici et on
                // gère l'arrêt du match APRÈS le try/catch, en dehors de ces blocs.
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant un match — abandon en match nul pour ne pas bloquer le serveur : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    AbortMatchSafely(p1);
                    AbortMatchSafely(p2);
                    // Filet de sécurité : si le crash a eu lieu PENDANT la section critique brève de
                    // RunMatch (voir plus bas), matchInProgress resterait sinon bloqué à "true" pour
                    // toujours — sans conséquence s'il n'était pas tenu (juste une réaffectation à
                    // "false", déjà sa valeur).
                    matchInProgress = false;
                    activeMatchCount--;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    activeMatchCount--;
                    yield break;
                }
                yield return inner.Current;
            }
        }

        private static void AbortMatchSafely(PlayerConnection p)
        {
            try { if (!p.IsDisconnected) p.Send(new NetMessage { type = "match_over", winner_team = 0 }); } catch { }
            try { p.Close(); } catch { }
        }

        /// <summary>
        /// Point d'entrée Deathmatch/Zone de Contrôle — "Option B" (2026-08-30, voir MatchState.cs) :
        /// à partir d'ici, TOUT l'état de la partie vit dans un MatchState local à cette coroutine,
        /// jamais dans un champ d'instance ni un GameObject partagé. C'est ce qui permet à plusieurs
        /// parties de tourner en parallèle sur ce même processus sans jamais se corrompre entre elles
        /// (voir le bug de corruption de santé de bâtiment trouvé pendant la conception de ce
        /// chantier — impossible ici puisque chaque MatchState a son propre TacticalWorldState).
        /// </summary>
        private IEnumerator RunMatch(PlayerConnection p1, PlayerConnection p2, string cacheKey, string owningUserId)
        {
            var ms = new MatchState
            {
                MatchId = Guid.NewGuid().ToString(),
                P1 = p1,
                P2 = p2,
            };
            p1.TeamId = 1;
            p2.TeamId = 2;
            ms.Mode = string.IsNullOrEmpty(p1.Mode) ? "deathmatch" : p1.Mode;

            yield return FetchUsername(p1);
            yield return FetchUsername(p2);

            // Fige la géométrie de départ de CETTE partie — chemin rapide (tuile déjà en cache,
            // aucun verrou/aucun contact avec la scène) ou lent (tuile jamais vue, génération protégée
            // par matchInProgress contre un combat de Conquête concurrent — voir
            // EnsureTileLoadedAndSnapshot, 2026-08-30 "des milliers de cartes"). Une fois cette étape
            // terminée, `ms` est totalement autonome — le reste du match ne touche plus jamais
            // matchInProgress, ni la scène Unity, ni aucun état partagé avec une autre partie.
            yield return EnsureTileLoadedAndSnapshot(ms, cacheKey, owningUserId);
            TryParseTileCacheKey(ms.CacheKey, out int matchTileX, out int matchTileY);

            if (ms.Mode == "zone_control")
            {
                Vector2 zoneCenter2D = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, Vector2.zero, 40f);
                ms.Zone = new MatchZoneState { Center = new Vector3(zoneCenter2D.x, 0f, zoneCenter2D.y) };
            }

            bool hasRealTile = ms.CacheKey != "Default";
            p1.Send(new NetMessage { type = "match_found", match_id = ms.MatchId, team_id = 1, opponent_username = p2.Username, mode = ms.Mode, has_home_tile = hasRealTile, zone_tile_x = matchTileX, zone_tile_y = matchTileY });
            p2.Send(new NetMessage { type = "match_found", match_id = ms.MatchId, team_id = 2, opponent_username = p1.Username, mode = ms.Mode, has_home_tile = hasRealTile, zone_tile_x = matchTileX, zone_tile_y = matchTileY });

            yield return CreateMatchRecord(ms.MatchId, p1, p2, ms.Mode);

            // Placement manuel (voir 03-network-protocol.md, "submit_deployment"/"deployment_result") :
            // chaque joueur choisit où poser sa PROPRE escouade dans sa zone de déploiement pendant
            // que l'autre fait de même, en parallèle — pas de tour par tour ici. Un joueur qui ne
            // soumet rien (ou une soumission invalide) de valide avant l'expiration du timer reçoit
            // le repli automatique (AutoDeployTeamFallbackPure) pour SON seul camp.
            yield return RunDeploymentPhasePure(ms);

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunPlanningPhasePure(ms, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhasePure(ms, turnNumber);

                int team1Alive = ms.World.units.Count(u => u.team == 1 && !u.isDead);
                int team2Alive = ms.World.units.Count(u => u.team == 2 && !u.isDead);

                if (team1Alive == 0 || team2Alive == 0)
                {
                    matchOver = true;
                    // Anéantissement mutuel (les deux équipes à 0) = match nul, pas une victoire par défaut.
                    winnerTeam = (team1Alive == 0 && team2Alive == 0) ? 0 : (team1Alive == 0 ? 2 : 1);
                }
                else if (ms.Mode == "zone_control")
                {
                    int zoneWinner = ms.Zone != null ? ms.Zone.GetWinningTeamIfComplete() : 0;
                    if (zoneWinner != 0)
                    {
                        matchOver = true;
                        winnerTeam = zoneWinner;
                    }
                    else if (turnNumber >= ZoneControlTurnCap)
                    {
                        matchOver = true;
                        float p1Progress = ms.Zone != null ? ms.Zone.ProgressTeam1 : 0f;
                        float p2Progress = ms.Zone != null ? ms.Zone.ProgressTeam2 : 0f;
                        winnerTeam = Mathf.Approximately(p1Progress, p2Progress) ? 0 : (p1Progress > p2Progress ? 1 : 2);
                    }
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    // Départage par nombre d'unités vivantes, puis par total de points de vie
                    // restants ; égalité parfaite sur les deux critères = match nul.
                    matchOver = true;
                    int team1Health = ms.World.units.Where(u => u.team == 1 && !u.isDead).Sum(u => u.health);
                    int team2Health = ms.World.units.Where(u => u.team == 2 && !u.isDead).Sum(u => u.health);
                    if (team1Alive != team2Alive) winnerTeam = team1Alive > team2Alive ? 1 : 2;
                    else if (team1Health != team2Health) winnerTeam = team1Health > team2Health ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            yield return UpdateRatings(p1, p2, winnerTeam);

            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p1.NewRating, rating_delta = p1.RatingDelta });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, your_new_rating = p2.NewRating, rating_delta = p2.RatingDelta });

            yield return CloseMatchRecord(ms.MatchId, winnerTeam);

            p1.Close();
            p2.Close();
            StartCoroutine(ReportInstanceStatus());
        }

        /// <summary>Fige ms.World pour la tuile <paramref name="cacheKey"/> — chemin rapide (déjà en
        /// cache mémoire/disque, aucun contact avec la scène vivante, aucun verrou) ou lent (jamais
        /// vue : génération protégée par matchInProgress + anti-doublon + anti-abus). Voir
        /// TacticalGridBuilder.BuildFromCacheOnly/GenerateAndCacheTile. 2026-08-30, "des milliers de
        /// cartes".</summary>
        private IEnumerator EnsureTileLoadedAndSnapshot(MatchState ms, string cacheKey, string requestingUserId)
        {
            TacticalWorldState cached = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
            if (cached != null) { ms.World = cached; ms.CacheKey = cacheKey; yield break; }

            if (cacheKey != "Default" && !string.IsNullOrEmpty(requestingUserId) && !CanTriggerTileGeneration(requestingUserId))
            {
                Debug.LogWarning($"[MatchSessionManager] Limite de génération de tuile atteinte pour {requestingUserId} ({cacheKey}) — repli sur la carte par défaut pour cette partie.");
                cacheKey = "Default";
                cached = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
                if (cached != null) { ms.World = cached; ms.CacheKey = cacheKey; yield break; }
            }

            if (!tileGenerationInFlight.TryGetValue(cacheKey, out Coroutine inFlight))
            {
                inFlight = StartCoroutine(GenerateAndCacheTile(cacheKey));
                tileGenerationInFlight[cacheKey] = inFlight;
            }
            yield return inFlight;
            if (tileGenerationInFlight.TryGetValue(cacheKey, out Coroutine current) && current == inFlight)
            {
                tileGenerationInFlight.Remove(cacheKey);
            }

            ms.World = TacticalGridBuilder.BuildFromCacheOnly(cacheKey);
            ms.CacheKey = cacheKey;
            if (ms.World == null)
            {
                // Échec de génération (Overpass injoignable, ville trop grosse pour le délai
                // d'attente, etc.) — jamais laisser une partie sans géométrie, repli sur Default.
                Debug.LogWarning($"[MatchSessionManager] Génération de la tuile {cacheKey} indisponible — repli sur la carte par défaut.");
                ms.World = TacticalGridBuilder.BuildFromCacheOnly("Default");
                ms.CacheKey = "Default";
                if (ms.World == null)
                {
                    // Ne devrait jamais arriver (SetupWorldOnce garantit "Default" au démarrage) —
                    // mieux vaut une exception explicite ici (rattrapée par RunMatchGuarded, la
                    // partie s'annule proprement) qu'un NullReferenceException plus loin dans
                    // RunExecutionPhasePure.
                    throw new InvalidOperationException("Aucune géométrie disponible, ni pour la tuile demandée ni pour 'Default'.");
                }
            }
        }

        /// <summary>Charge RÉELLEMENT une tuile jamais vue dans la scène serveur (réutilise
        /// LoadZoneOnServer, le mécanisme existant de la Conquête — fetch Overpass + bake NavMesh
        /// synchrone, jusqu'à ~60s pire cas) puis extrait/écrit son cache disque via BuildFromScene()
        /// (portes/fenêtres incluses, voir TacticalGridBuilder). Tient matchInProgress pendant toute
        /// l'opération — compromis accepté avec la Conquête (voir le plan), pas de solution "propre"
        /// sans un processus dédié à la génération (hors scope).</summary>
        private IEnumerator GenerateAndCacheTile(string cacheKey)
        {
            while (matchInProgress) yield return null;
            matchInProgress = true;

            if (cacheKey == "Default")
            {
                if (!defaultMapLoaded) yield return RestoreDefaultMapOnServer();
            }
            else if (TryParseTileCacheKey(cacheKey, out int tileX, out int tileY))
            {
                yield return LoadZoneOnServer(tileX, tileY);
            }

            TacticalGridBuilder.BuildFromScene(); // reconstruction complète (jamais vue) -> écrit L1+L2

            matchInProgress = false;
        }

        /// <summary>"Z{zoom}_{tileX}_{tileY}" -> (tileX, tileY) — false pour "Default" ou toute autre
        /// valeur malformée (l'appelant doit alors traiter comme "Default").</summary>
        private static bool TryParseTileCacheKey(string cacheKey, out int tileX, out int tileY)
        {
            tileX = 0; tileY = 0;
            if (string.IsNullOrEmpty(cacheKey) || cacheKey == "Default") return false;
            string[] parts = cacheKey.Split('_');
            return parts.Length == 3 && int.TryParse(parts[1], out tileX) && int.TryParse(parts[2], out tileY);
        }

        /// <summary>Anti-abus (voir EnsureTileLoadedAndSnapshot) — vrai au plus une fois toutes les
        /// TileGenerationCooldownSeconds par utilisateur.</summary>
        private bool CanTriggerTileGeneration(string userId)
        {
            if (lastTileGenerationByUser.TryGetValue(userId, out DateTime last)
                && (DateTime.UtcNow - last).TotalSeconds < TileGenerationCooldownSeconds)
            {
                return false;
            }
            lastTileGenerationByUser[userId] = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Attend jusqu'à DeploymentSeconds que les DEUX joueurs soumettent "submit_deployment" (en
        /// parallèle, pas tour par tour), puis spawn réellement les unités des deux camps — soit à
        /// partir du placement manuel soumis (recadré dans la zone légale, voir ResolveDeployment),
        /// soit via le repli automatique si rien de valide n'a été reçu à temps — et diffuse le
        /// résultat final aux deux clients via "deployment_result" (voir 03-network-protocol.md).
        /// </summary>
        private IEnumerator RunDeploymentPhase(PlayerConnection p1, PlayerConnection p2)
        {
            p1.HasSubmittedDeployment = false;
            p2.HasSubmittedDeployment = false;
            p1.PendingDeployment = null;
            p2.PendingDeployment = null;
            p1.MapReady = false;
            p2.MapReady = false;

            // Instrumentation temps réel (2026-08-30, test grandeur nature demandé explicitement) :
            // horodatage UTC réel à chaque étape, pour vérifier depuis un vrai log que ces phases
            // durent effectivement ce qu'elles sont censées durer selon L'HORLOGE DU SERVEUR — et pas
            // seulement selon Time.deltaTime accumulé, qui pourrait dériver si le serveur ralentit
            // sous charge (voir GameServerBootstrap.targetFrameRate=30).
            DateTime deploymentPhaseStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] RunDeploymentPhase démarré à {deploymentPhaseStartUtc:O} — attente MapReady (max {MapReadyMaxWaitSeconds}s) puis déploiement (max {DeploymentSeconds}s).");

            // Attend que les DEUX clients aient fini de charger leur carte et affichent réellement
            // leur dock de déploiement avant de démarrer le VRAI compte à rebours — voir
            // MapReadyMaxWaitSeconds.
            float mapWait = MapReadyMaxWaitSeconds;
            while (mapWait > 0f && !(p1.MapReady && p2.MapReady))
            {
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;
                mapWait -= Time.deltaTime;
                yield return null;
            }

            double mapReadyElapsedSec = (DateTime.UtcNow - deploymentPhaseStartUtc).TotalSeconds;
            Debug.Log($"[Timing] MapReady terminé après {mapReadyElapsedSec:F1}s réelles (p1.MapReady={p1.MapReady}, p2.MapReady={p2.MapReady}) — démarrage du compte à rebours de déploiement.");

            DateTime deploymentCountdownStartUtc = DateTime.UtcNow;
            float remaining = DeploymentSeconds;
            while (remaining > 0f && !(p1.HasSubmittedDeployment && p2.HasSubmittedDeployment))
            {
                // Réutilise DrainMessages (turnNumber=0 ne correspondra jamais à un vrai
                // "submit_turn", qui commence à 1 — seuls "heartbeat"/"submit_deployment" sont donc
                // traités ici, sans dupliquer la boucle de lecture des messages entrants).
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);

                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                remaining -= Time.deltaTime;
                yield return null;
            }

            double deploymentElapsedSec = (DateTime.UtcNow - deploymentCountdownStartUtc).TotalSeconds;
            bool deploymentTimedOut = !(p1.HasSubmittedDeployment && p2.HasSubmittedDeployment);
            Debug.Log($"[Timing] Compte à rebours de déploiement terminé après {deploymentElapsedSec:F1}s réelles (attendu {DeploymentSeconds}s) — p1.HasSubmittedDeployment={p1.HasSubmittedDeployment}, p2.HasSubmittedDeployment={p2.HasSubmittedDeployment}, expiré par timeout={deploymentTimedOut}.");

            var team1Units = ResolveDeployment(p1, 1);
            var team2Units = ResolveDeployment(p2, 2);
            Debug.Log($"[Trajectoire] Déploiement résolu : équipe1={team1Units.Count} unité(s) ({string.Join(", ", team1Units.Select(u => $"{u.unit_id}@({u.x:F1},{u.z:F1})"))}), équipe2={team2Units.Count} unité(s) ({string.Join(", ", team2Units.Select(u => $"{u.unit_id}@({u.x:F1},{u.z:F1})"))}).");

            // Brouillard de guerre au déploiement (2026-08-30, voir rapport de bug "des unités
            // bleues et rouges apparaissent d'un coup") : avant le tout premier tour, aucune ligne
            // de vue n'a encore été calculée — révéler la position de déploiement de l'adversaire à
            // cet instant n'aurait aucune justification tactique. Chaque client ne reçoit donc plus
            // que SA PROPRE escouade ; les unités adverses n'existeront côté client qu'à partir du
            // moment où elles seront repérées en jeu (voir PlaySnapshotsCoroutine, apparition
            // dynamique à la première visibilité). Exception : les BARRICADES restent visibles des
            // deux côtés dès le déploiement — ce sont des éléments de terrain physiques (comme un
            // bâtiment, jamais caché non plus), pas un renseignement sur les forces adverses ; les
            // masquer aurait créé des murs invisibles dont l'effet de couverture reste bien réel côté
            // serveur mais ne se voit nulle part à l'écran.
            var team1Barricades = team1Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            var team2Barricades = team2Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "deployment_result", deployed_units = team1Units.Concat(team2Barricades).ToArray() });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "deployment_result", deployed_units = team2Units.Concat(team1Barricades).ToArray() });
        }

        /// <summary>Vrai si le multi-ensemble de placements respecte le budget autorisé (voir
        /// MaxDeployedCombatUnits/MaxDeployedBarricades/CombatPointBudget) et ne contient que des
        /// types/coordonnées valides — sinon la soumission ENTIÈRE est rejetée (repli automatique
        /// pour tout ce camp), plutôt que d'essayer de n'en garder qu'une partie.</summary>
        private static bool IsRosterValid(UnitPlacement[] placements)
        {
            if (placements == null || placements.Length == 0) return false;
            if (placements.Length > MaxDeployedCombatUnits + MaxDeployedBarricades) return false;

            int combatCount = 0, barricadeCount = 0, totalCost = 0;
            foreach (var p in placements)
            {
                if (!Enum.IsDefined(typeof(UnitSpawnerUI.UnitType), p.unit_type)) return false;
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return false;
                if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return false;

                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                if (type == UnitSpawnerUI.UnitType.BarricadeRoutiere) barricadeCount++;
                else combatCount++;
                totalCost += UnitTypeStats.DeploymentCost(type);
            }
            return combatCount <= MaxDeployedCombatUnits && barricadeCount <= MaxDeployedBarricades
                && totalCost <= CombatPointBudget;
        }

        /// <summary>Ramène (x, z) dans la zone de déploiement légale du camp (cercle centré sur le
        /// même point d'ancrage qu'AutoDeployBattlefield/AutoDeployTeamFallback) — jamais un rejet
        /// en bloc pour une simple imprécision de tap, mais impossible de déployer au contact
        /// immédiat de l'adversaire en soumettant volontairement une position lointaine.</summary>
        private static Vector3 ClampToDeploymentZone(Vector3 pos, int team)
        {
            Vector3 center = team == 1 ? Team1DeploymentZoneCenter : Team2DeploymentZoneCenter;
            Vector3 flat = new Vector3(pos.x - center.x, 0f, pos.z - center.z);
            if (flat.magnitude > DeploymentZoneRadius) flat = flat.normalized * DeploymentZoneRadius;
            return new Vector3(center.x + flat.x, pos.y, center.z + flat.z);
        }

        private static int InferUnitType(UnitAI u)
        {
            if (u.isMortar) return (int)UnitSpawnerUI.UnitType.Mortier;
            if (u.isCanonVehicle) return (int)UnitSpawnerUI.UnitType.VehiculeCanon;
            if (u.isTank) return (int)UnitSpawnerUI.UnitType.CharLeopard;
            return (int)UnitSpawnerUI.UnitType.Fantassin;
        }

        /// <summary>Spawn réellement les unités d'UN camp (placement manuel validé+recadré, ou repli
        /// automatique) et renvoie la liste des unités effectivement posées, pour le broadcast
        /// "deployment_result".</summary>
        private List<DeployedUnit> ResolveDeployment(PlayerConnection conn, int team)
        {
            bool valid = conn.HasSubmittedDeployment && IsRosterValid(conn.PendingDeployment);

            if (valid)
            {
                foreach (var p in conn.PendingDeployment)
                {
                    Vector3 clamped = ClampToDeploymentZone(new Vector3(p.x, p.y, p.z), team);
                    UnitSpawnerUI.Instance.SpawnUnitAt((UnitSpawnerUI.UnitType)p.unit_type, clamped, team);
                }
            }
            else
            {
                UnitSpawnerUI.Instance.AutoDeployTeamFallback(team);
            }

            var placed = new List<DeployedUnit>();
            foreach (var u in UnitAI.AllLivingUnits)
            {
                if (u.teamID != team) continue;
                placed.Add(new DeployedUnit
                {
                    unit_id = u.gameObject.name,
                    unit_type = InferUnitType(u),
                    team_id = team,
                    x = u.transform.position.x,
                    y = u.transform.position.y,
                    z = u.transform.position.z
                });
            }
            foreach (var b in RoadBarrier.AllBarriers)
            {
                if (b == null || b.teamID != team) continue;
                placed.Add(new DeployedUnit
                {
                    unit_id = b.gameObject.name,
                    unit_type = (int)UnitSpawnerUI.UnitType.BarricadeRoutiere,
                    team_id = team,
                    x = b.transform.position.x,
                    y = b.transform.position.y,
                    z = b.transform.position.z
                });
            }
            return placed;
        }

        // =====================================================================
        // "Option B" (2026-08-30) — équivalents en donnée pure des méthodes ci-dessus, pour les
        // parties Deathmatch/Zone de Contrôle (voir MatchState.cs). Les méthodes ci-dessus
        // (RunDeploymentPhase, RunPlanningPhase, RunExecutionPhase, ApplyForPlayer,
        // ApplyOrdersToUnits, BuildUnitOrders, BuildSnapshotsFromEvents, CaptureTacticalSnapshot,
        // ResolveDeployment) restent INTACTES et continuent de servir EXCLUSIVEMENT le mode Conquête
        // (RunConquestSkirmish, toujours basé sur de vraies UnitAI/BuildingStructure, hors scope de ce
        // chantier) — aucune des deux familles de méthodes n'appelle jamais l'autre.
        // =====================================================================

        /// <summary>Version en donnée pure de RunDeploymentPhase — même timing/mêmes messages réseau
        /// exactement, mais construit les unités/barricades directement en TacticalUnit/Barricade
        /// (voir MatchState.World) au lieu d'Instancier de vraies UnitAI/RoadBarrier via
        /// UnitSpawnerUI.SpawnUnitAt.</summary>
        private IEnumerator RunDeploymentPhasePure(MatchState ms)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            p1.HasSubmittedDeployment = false;
            p2.HasSubmittedDeployment = false;
            p1.PendingDeployment = null;
            p2.PendingDeployment = null;
            p1.MapReady = false;
            p2.MapReady = false;

            DateTime deploymentPhaseStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] [{ms.MatchId}] RunDeploymentPhasePure démarré à {deploymentPhaseStartUtc:O} — attente MapReady (max {MapReadyMaxWaitSeconds}s) puis déploiement (max {DeploymentSeconds}s).");

            float mapWait = MapReadyMaxWaitSeconds;
            while (mapWait > 0f && !(p1.MapReady && p2.MapReady))
            {
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;
                mapWait -= Time.deltaTime;
                yield return null;
            }

            double mapReadyElapsedSec = (DateTime.UtcNow - deploymentPhaseStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{ms.MatchId}] MapReady terminé après {mapReadyElapsedSec:F1}s réelles (p1.MapReady={p1.MapReady}, p2.MapReady={p2.MapReady}).");

            DateTime deploymentCountdownStartUtc = DateTime.UtcNow;
            float remaining = DeploymentSeconds;
            while (remaining > 0f && !(p1.HasSubmittedDeployment && p2.HasSubmittedDeployment))
            {
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;
                remaining -= Time.deltaTime;
                yield return null;
            }

            double deploymentElapsedSec = (DateTime.UtcNow - deploymentCountdownStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{ms.MatchId}] Compte à rebours de déploiement terminé après {deploymentElapsedSec:F1}s réelles (attendu {DeploymentSeconds}s).");

            var team1Units = ResolveDeploymentPure(ms, p1, 1);
            var team2Units = ResolveDeploymentPure(ms, p2, 2);
            Debug.Log($"[Trajectoire] [{ms.MatchId}] Déploiement résolu : équipe1={team1Units.Count} unité(s), équipe2={team2Units.Count} unité(s).");

            var team1Barricades = team1Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            var team2Barricades = team2Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "deployment_result", deployed_units = team1Units.Concat(team2Barricades).ToArray() });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "deployment_result", deployed_units = team2Units.Concat(team1Barricades).ToArray() });
        }

        /// <summary>Équivalent pur de ResolveDeployment — place directement dans ms.World (units ou
        /// barricades), sans jamais passer par UnitSpawnerUI.SpawnUnitAt (qui instancierait de vrais
        /// prefabs, exactement ce que ce chantier élimine pour ces deux modes).</summary>
        private List<DeployedUnit> ResolveDeploymentPure(MatchState ms, PlayerConnection conn, int team)
        {
            bool valid = conn.HasSubmittedDeployment && IsRosterValid(conn.PendingDeployment);
            var placements = new List<UnitPlacement>();

            if (valid)
            {
                foreach (var p in conn.PendingDeployment)
                {
                    Vector3 clamped = ClampToDeploymentZone(new Vector3(p.x, p.y, p.z), team);
                    placements.Add(new UnitPlacement { unit_type = p.unit_type, x = clamped.x, y = clamped.y, z = clamped.z });
                }
            }
            else
            {
                placements.AddRange(AutoDeployTeamFallbackPure(ms, team));
            }

            var placed = new List<DeployedUnit>();
            foreach (var p in placements)
            {
                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                if (type == UnitSpawnerUI.UnitType.BarricadeRoutiere)
                {
                    string id = $"Barricade_{team}_{ms.NextUnitSequence++}";
                    // Toujours Quaternion.identity côté original (UnitSpawnerUI.SpawnUnitAt,
                    // BarricadeRoutiere) : l'orientation n'est jamais transmise par
                    // "submit_deployment" (UnitPlacement n'a pas de champ rotation), donc toujours
                    // alignée sur l'axe X monde ici aussi.
                    // Recalage au sol AVANT usage (2026-09-02) — jusqu'ici seule PlaceCombatUnitPure
                    // (unités de combat) passait par MatchGeometry.FindGroundLevelInGrid ; une
                    // barricade manuelle utilisait p.x/p.z BRUTS, ce qui pouvait la poser en pleine
                    // empreinte de bâtiment (même bug que UnitSpawnerUI.SpawnUnitAt côté moteur
                    // "vivant" de la Conquête, voir son commentaire — corrigé là aussi le même jour).
                    Vector2 grounded = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, new Vector2(p.x, p.z), 5f);
                    Vector2 dir = Vector2.right;
                    float halfWidth = MatchState.BarricadeHalfWidthMeters;
                    ms.World.barricades.Add(new Barricade
                    {
                        p1 = grounded - dir * halfWidth,
                        p2 = grounded + dir * halfWidth,
                        ownerTeam = team,
                        hp = 250f // RoadBarrier.cs:17-18 — PV par défaut exacts
                    });
                    placed.Add(new DeployedUnit { unit_id = id, unit_type = p.unit_type, team_id = team, x = grounded.x, y = GroundLevelY, z = grounded.y });
                }
                else
                {
                    placed.Add(PlaceCombatUnitPure(ms, type, new Vector3(p.x, p.y, p.z), team));
                }
            }
            return placed;
        }

        // Hauteur Y cosmétique constante pour une unité au sol — le terrain d'une tuile est plat
        // (seuls les bâtiments ont du relief, voir TacticalUnit.zStrata), cohérente avec les valeurs
        // déjà observées en production via le vrai NavMesh (ex. y=0.25).
        private const float GroundLevelY = 0.25f;

        /// <summary>Construit une TacticalUnit directement (pas de UnitAI réelle) et l'ajoute à
        /// ms.World.units — repositionnement au sol via MatchGeometry.FindGroundLevelInGrid (grille de
        /// marche déjà en cache pour CETTE partie, voir MatchState.World.grid) au lieu du vrai NavMesh
        /// vivant (UnitSpawnerUI.FindGroundLevelNavPoint) — 2026-08-30, "des milliers de cartes" :
        /// nécessaire dès qu'une AUTRE tuile que celle de cette partie peut être chargée en scène au
        /// même instant (voir MatchGeometry). Appliqué qu'il s'agisse d'un placement manuel ou d'un
        /// repli automatique, exactement comme l'original.</summary>
        private DeployedUnit PlaceCombatUnitPure(MatchState ms, UnitSpawnerUI.UnitType type, Vector3 rawPos, int team)
        {
            Vector2 grounded = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, new Vector2(rawPos.x, rawPos.z), 5f);
            UnitTypeStats.Get(type, out int health, out float porteeDetection, out int weaponDamage, out float weaponCooldownSeconds, out bool isMortar);

            string id = $"{UnitTypeStats.TypeName(type)}_{team}_{ms.NextUnitSequence++}";
            var tu = new TacticalUnit
            {
                id = id,
                team = team,
                position = grounded,
                zStrata = ZStrata.Sol,
                health = health,
                isDead = false,
                spottingRange = isMortar ? 25f : 35f, // seuil "toit" non pertinent au déploiement (zStrata=Sol)
                engagementRange = porteeDetection,
                weaponDamage = weaponDamage,
                weaponCooldownSeconds = weaponCooldownSeconds,
                isMortar = isMortar,
            };
            ms.World.units.Add(tu);
            ms.UnitTypeById[id] = (int)type;
            ms.CurrentYById[id] = GroundLevelY;

            return new DeployedUnit { unit_id = id, unit_type = (int)type, team_id = team, x = grounded.x, y = GroundLevelY, z = grounded.y };
        }

        /// <summary>Équivalent pur de UnitSpawnerUI.AutoDeployTeamFallback (mêmes points d'ancrage et
        /// décalages exacts, extraInfantry jamais utilisé ici — réservé à la Conquête) — renvoie des
        /// UnitPlacement plutôt que de spawner directement, pour repasser par le même chemin
        /// (PlaceCombatUnitPure, avec son repositionnement au sol) qu'un placement manuel.</summary>
        private static List<UnitPlacement> AutoDeployTeamFallbackPure(MatchState ms, int team)
        {
            Vector2 anchor2D = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, team == 1 ? new Vector2(-25f, -25f) : new Vector2(25f, 25f), 40f);
            Vector3 anchor = new Vector3(anchor2D.x, 0f, anchor2D.y);
            var list = new List<UnitPlacement>();

            void Add(UnitSpawnerUI.UnitType type, Vector3 offset)
            {
                Vector3 pos = anchor + offset;
                list.Add(new UnitPlacement { unit_type = (int)type, x = pos.x, y = pos.y, z = pos.z });
            }

            if (team == 1)
            {
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(-2f, 0, -2f));
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(2f, 0, 2f));
                Add(UnitSpawnerUI.UnitType.CharLeopard, new Vector3(5f, 0, -3f));
                Add(UnitSpawnerUI.UnitType.Mortier, new Vector3(-5f, 0, -4f));
            }
            else
            {
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(-3f, 0, 3f));
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(3f, 0, -3f));
                Add(UnitSpawnerUI.UnitType.CharLeopard, new Vector3(6f, 0, 4f));
                Add(UnitSpawnerUI.UnitType.Mortier, new Vector3(-6f, 0, 5f));
            }
            return list;
        }

        /// <summary>Version en donnée pure de RunPlanningPhase — même timing exactement.</summary>
        private IEnumerator RunPlanningPhasePure(MatchState ms, int turnNumber)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            float remaining = PlanningSeconds;
            int lastTick = -1;
            DateTime planningStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} — RunPlanningPhasePure démarré à {planningStartUtc:O} (max {PlanningSeconds}s).");

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    var tick = new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft };
                    if (!p1.IsDisconnected) p1.Send(tick);
                    if (!p2.IsDisconnected) p2.Send(tick);
                }

                remaining -= Time.deltaTime;
                yield return null;
            }

            double planningElapsedSec = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} — RunPlanningPhasePure terminé après {planningElapsedSec:F1}s réelles (attendu max {PlanningSeconds}s).");

            ApplyForPlayerPure(ms, p1, p2);
            ApplyForPlayerPure(ms, p2, p1);
        }

        /// <summary>Équivalent pur de ApplyForPlayer. Simplification ASSUMÉE pour le repli IA d'un
        /// joueur absent/en retard (voir la discussion d'architecture du 2026-08-30) : plutôt que
        /// TacticalAIPlanner (qui dépend de vraies UnitAI/de la liste globale UnitAI.AllLivingUnits,
        /// incompatible avec des parties concurrentes), ses unités "tiennent la position" ce tour-ci
        /// (aucun ordre) — TacticalResolver.Resolve() évalue quand même les tirs pour TOUTE unité
        /// vivante, pas seulement celles avec un ordre actif (voir TacticalResolver.cs, énumération de
        /// "shooters" sur state.units en entier) : ces unités se défendent donc normalement, juste
        /// sans avancer/flanquer activement comme le ferait la vraie IA en Solo/Conquête.</summary>
        private void ApplyForPlayerPure(MatchState ms, PlayerConnection conn, PlayerConnection opponent)
        {
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                foreach (var u in ms.World.units.Where(u => u.team == conn.TeamId)) ms.PendingOrderNodes.Remove(u.id);

                if (!opponent.IsDisconnected)
                {
                    opponent.Send(new NetMessage
                    {
                        type = "opponent_ghosted",
                        team_id = conn.TeamId,
                        reason = conn.IsDisconnected ? "disconnected" : "timeout"
                    });
                }
            }
            else
            {
                var myUnits = ms.World.units.Where(u => u.team == conn.TeamId && !u.isDead).ToList();
                ApplyOrdersToUnitsPure(ms, myUnits, conn.PendingOrders);
            }
        }

        /// <summary>Équivalent pur de ApplyOrdersToUnits — écrit dans ms.PendingOrderNodes au lieu
        /// d'appeler unit.AddTacticalNode sur une UnitAI réelle. Mêmes validations exactement (chemin
        /// tronqué à MaxOrderPathNodes, NaN/Infinity/action hors enum rejetés silencieusement).</summary>
        private void ApplyOrdersToUnitsPure(MatchState ms, List<TacticalUnit> myUnits, UnitOrder[] orders)
        {
            if (orders == null) return;
            var myIds = new HashSet<string>(myUnits.Select(u => u.id));

            foreach (var order in orders)
            {
                if (!myIds.Contains(order.unit_id)) continue;

                ms.PendingOrderNodes.Remove(order.unit_id);
                if (order.path == null) continue;
                if (order.path.Length > MaxOrderPathNodes) continue;

                var nodes = new List<TacticalPathManager.TacticalNode>();
                foreach (var node in order.path)
                {
                    if (float.IsNaN(node.x) || float.IsNaN(node.y) || float.IsNaN(node.z)) continue;
                    if (float.IsInfinity(node.x) || float.IsInfinity(node.y) || float.IsInfinity(node.z)) continue;
                    if (!Enum.IsDefined(typeof(TacticalPathManager.NodeAction), node.action)) continue;

                    nodes.Add(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
                ms.PendingOrderNodes[order.unit_id] = nodes;
            }
        }

        /// <summary>Version en donnée pure de RunExecutionPhase — AUCUNE écriture sur un GameObject de
        /// scène (ni UnitAI, ni BuildingStructure/DestructibleEnvironment) : ms.World EST le résultat
        /// officiel, avant et après Resolve(), rien de plus à synchroniser ailleurs. C'est exactement
        /// ce qui rend plusieurs parties sûres en parallèle (voir le bug de corruption de santé de
        /// bâtiment/occupation de fenêtre qui a motivé ce chantier).</summary>
        private IEnumerator RunExecutionPhasePure(MatchState ms, int turnNumber)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            DateTime executionStartUtc = DateTime.UtcNow;

            // Rafraîchit spottingRange selon la strate ACTUELLE (peut avoir changé au tour précédent
            // via une Escalade — TacticalResolver.ApplyEndOfPathEffects met déjà zStrata à jour en
            // place, voir TacticalTypes.cs) : même barème que BuildTacticalUnit (55m toit / 25m
            // mortier / 35m sinon), recalculé chaque tour comme l'original.
            foreach (var u in ms.World.units)
            {
                if (u.isDead) continue;
                u.spottingRange = u.zStrata == ZStrata.Toit ? 55f : (u.isMortar ? 25f : 35f);
            }

            var unitsBeforeResolution = ms.World.units.Where(u => !u.isDead).ToList();
            var mortarStrikes = new List<Vector2>();
            var ordersTeam1 = new List<UnitOrders>();
            var ordersTeam2 = new List<UnitOrders>();
            var pendingWindowByUnitId = new Dictionary<string, (int buildingIdx, int windowId)?>();

            foreach (var unit in unitsBeforeResolution)
            {
                ms.PendingOrderNodes.TryGetValue(unit.id, out var path);
                UnitOrders orders = BuildUnitOrdersPure(ms, unit, path, mortarStrikes, pendingWindowByUnitId);
                if (orders.checkpoints.Count > 0)
                {
                    (unit.team == 1 ? ordersTeam1 : ordersTeam2).Add(orders);
                }
            }

            Debug.Log($"[Trajectoire] [{ms.MatchId}] Tour {turnNumber} : équipe1={ordersTeam1.Count} unité(s) avec ordres, équipe2={ordersTeam2.Count} unité(s) avec ordres, {mortarStrikes.Count} frappe(s) de mortier.");

            double preResolveMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            // Pool de threads (2026-08-30, consolidation à une seule instance de processus) —
            // TacticalResolver.Resolve() est une fonction pure : ms.World/ordersTeam1/2/mortarStrikes
            // sont des données PROPRES à cette partie, jamais partagées avec une autre (voir
            // MatchState) — donc sûr à exécuter sur un thread d'arrière-plan. Sans ça, avec une seule
            // instance/un seul thread principal Unity, plusieurs parties dont le minuteur expire au
            // même instant se sérialiseraient entre elles (chacune attend son tour pour son calcul,
            // même bref) — Task.Run les laisse s'exécuter en parallèle sur plusieurs cœurs CPU.
            var resolveTask = Task.Run(() => TacticalResolver.Resolve(ms.World, ordersTeam1, ordersTeam2, mortarStrikes));
            while (!resolveTask.IsCompleted) yield return null;
            if (resolveTask.Exception != null) throw resolveTask.Exception.InnerException ?? resolveTask.Exception;
            List<TacticalEvent> tacticalEvents = resolveTask.Result;
            double resolveOnlyMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds - preResolveMs;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} : préparation (ordres) {preResolveMs:F1}ms réelles, TacticalResolver.Resolve() SEUL {resolveOnlyMs:F1}ms réelles ({tacticalEvents.Count} événement(s) générés).");

            var snapshots = BuildSnapshotsFromEventsPure(ms, tacticalEvents, unitsBeforeResolution);

            // Occupation de fenêtre PROPRE À CETTE PARTIE (voir MatchState.OccupiedWindows) — remplace
            // BuildingWindow.OccupyWindow/VacateWindow (composant de scène PARTAGÉ). Hauteur Y
            // cosmétique (voir MatchState.CurrentYById) mise à jour de la même façon que
            // unit.transform.position dans RunExecutionPhase.
            foreach (var tu in ms.World.units)
            {
                if (tu.isDead) continue;

                if (tu.outputY.HasValue) ms.CurrentYById[tu.id] = tu.outputY.Value;

                pendingWindowByUnitId.TryGetValue(tu.id, out var newWindowKey);
                bool wantsWindow = tu.isGarrisoned && newWindowKey.HasValue;
                bool hasCurrentWindow = ms.CurrentWindowByUnitId.TryGetValue(tu.id, out var currentWindowKey);

                if (hasCurrentWindow && (!wantsWindow || !currentWindowKey.Equals(newWindowKey.GetValueOrDefault())))
                {
                    ms.OccupiedWindows.Remove(currentWindowKey);
                    ms.CurrentWindowByUnitId.Remove(tu.id);
                    hasCurrentWindow = false;
                }
                if (wantsWindow && !hasCurrentWindow)
                {
                    ms.OccupiedWindows.Add(newWindowKey.Value);
                    ms.CurrentWindowByUnitId[tu.id] = newWindowKey.Value;
                }
            }

            // Dégâts de bâtiment déjà appliqués directement dans ms.World.buildings par Resolve() lui
            // -même (donnée pure PROPRE à cette partie) — AUCUNE écriture sur un DestructibleEnvironment
            // partagé ici, contrairement à RunExecutionPhase (Conquête) : c'est précisément le bug de
            // corruption qui a motivé ce chantier (2026-08-30).

            ms.PendingOrderNodes.Clear();

            var unitMetaById = ms.World.units.ToDictionary(u => u.id);
            var teamById = ms.World.units.ToDictionary(u => u.id, u => u.team);
            Snapshot[] snapshotsForTeam1 = FilterSnapshotsForTeam(snapshots, ms.World, unitMetaById, teamById, 1);
            Snapshot[] snapshotsForTeam2 = FilterSnapshotsForTeam(snapshots, ms.World, unitMetaById, teamById, 2);

            int maxEnemiesVisibleToTeam1 = snapshotsForTeam1.Length == 0 ? 0 : snapshotsForTeam1.Max(s => s.units.Count(u => u.team_id == 2));
            int maxEnemiesVisibleToTeam2 = snapshotsForTeam2.Length == 0 ? 0 : snapshotsForTeam2.Max(s => s.units.Count(u => u.team_id == 1));
            Debug.Log($"[Vision] [{ms.MatchId}] Tour {turnNumber} : {snapshots.Count} tick(s) — équipe1 a vu au maximum {maxEnemiesVisibleToTeam1} ennemi(s), équipe2 a vu au maximum {maxEnemiesVisibleToTeam2} ennemi(s).");

            if (!p1.IsDisconnected)
            {
                p1.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam1 });
            }
            if (!p2.IsDisconnected)
            {
                p2.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam2 });
            }

            double totalPhaseElapsedMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} : RunExecutionPhasePure entièrement terminé en {totalPhaseElapsedMs:F1}ms réelles.");

            yield break;
        }

        /// <summary>Équivalent pur de BuildUnitOrders — même logique/mêmes NodeAction exactement (voir
        /// BuildUnitOrders pour le détail commenté de chaque cas), mais lit une TacticalUnit + une
        /// liste de TacticalNode déjà résolue (ms.PendingOrderNodes) au lieu d'une UnitAI réelle, et
        /// consulte l'occupation de fenêtre PROPRE à cette partie (ms.OccupiedWindows) au lieu du
        /// champ partagé BuildingWindow.isOccupied.</summary>
        private UnitOrders BuildUnitOrdersPure(MatchState ms, TacticalUnit unit, List<TacticalPathManager.TacticalNode> path,
            List<Vector2> mortarStrikesOut, Dictionary<string, (int buildingIdx, int windowId)?> pendingWindowByUnitId)
        {
            var orders = new UnitOrders { unitId = unit.id };
            Vector2 previousPos = unit.position;
            bool wasOnRoof = unit.zStrata == ZStrata.Toit;
            bool climbsThisOrder = false;

            if (path != null)
            {
                foreach (var node in path)
                {
                    Vector2 pos2D = new Vector2(node.position.x, node.position.z);

                    if (node.action == TacticalPathManager.NodeAction.TirMortier)
                    {
                        mortarStrikesOut.Add(pos2D);
                        continue;
                    }

                    var checkpoint = new PathCheckpoint { position = pos2D };

                    switch (node.action)
                    {
                        case TacticalPathManager.NodeAction.Guetter:
                        case TacticalPathManager.NodeAction.Embuscade:
                            checkpoint.setGuarding = true;
                            checkpoint.enterOverwatchAtEnd = true;
                            checkpoint.overwatchToSet = BuildOverwatchTriggerPure(ms.World, pos2D, previousPos, node.action);
                            break;

                        case TacticalPathManager.NodeAction.GuetterPorte:
                            checkpoint.setGarrisonDoor = true;
                            checkpoint.enterOverwatchAtEnd = true;
                            checkpoint.overwatchToSet = BuildOverwatchTriggerPure(ms.World, pos2D, previousPos, node.action);
                            break;

                        case TacticalPathManager.NodeAction.SeCacher:
                            checkpoint.setCamouflaged = true;
                            break;

                        case TacticalPathManager.NodeAction.GarnisonFenetre:
                            {
                                int bIdx = MatchGeometry.FindBuildingAt(ms.World, pos2D);
                                TacticalBuilding building = bIdx >= 0 ? ms.World.GetBuilding(bIdx) : null;
                                if (building != null)
                                {
                                    TacticalWindow window = MatchGeometry.GetClosestWindow(building, pos2D,
                                        winId => !ms.OccupiedWindows.Contains((bIdx, winId)));
                                    if (window != null)
                                    {
                                        checkpoint.setGarrisonWindow = true;
                                        checkpoint.windowNormalToSet = window.outwardNormal;
                                        pendingWindowByUnitId[unit.id] = (bIdx, window.id);
                                    }
                                }
                                break;
                            }

                        case TacticalPathManager.NodeAction.EntrerBatiment:
                            {
                                int bIdx = MatchGeometry.FindBuildingAt(ms.World, pos2D);
                                if (bIdx >= 0) checkpoint.enterBuildingId = bIdx;
                                break;
                            }

                        case TacticalPathManager.NodeAction.SortirBatiment:
                            checkpoint.exitBuilding = true;
                            break;

                        case TacticalPathManager.NodeAction.Escalade:
                            {
                                int bIdx = MatchGeometry.FindBuildingAt(ms.World, pos2D);
                                TacticalBuilding building = bIdx >= 0 ? ms.World.GetBuilding(bIdx) : null;
                                checkpoint.setPositionY = building != null ? building.height : 3f;
                                climbsThisOrder = true;
                                break;
                            }
                    }

                    orders.checkpoints.Add(checkpoint);
                    previousPos = pos2D;
                }
            }

            if (wasOnRoof && !climbsThisOrder && orders.checkpoints.Count > 0)
            {
                orders.implicitDescentY = 0f;
            }

            return orders;
        }

        /// <summary>Équivalent pur de BuildSnapshotsFromEvents — même reconstruction du journal
        /// d'événements en Snapshots tick par tick, mais sans jamais écrire sur une UnitAI réelle
        /// (SetNetworkHealth/ApplyNetworkDeath/transform.position) : les dictionnaires locaux
        /// pos/health/dead/shooting SONT la seule source de vérité pour construire chaque Snapshot
        /// (voir CaptureTacticalSnapshotPure).</summary>
        private List<Snapshot> BuildSnapshotsFromEventsPure(MatchState ms, List<TacticalEvent> events, List<TacticalUnit> unitsBeforeResolution)
        {
            var pos = new Dictionary<string, Vector2>();
            var rotation = new Dictionary<string, float>();
            var health = new Dictionary<string, int>();
            var dead = new Dictionary<string, bool>();
            var shooting = new Dictionary<string, bool>();
            var teamOf = new Dictionary<string, int>();

            foreach (var u in unitsBeforeResolution)
            {
                pos[u.id] = u.position;
                rotation[u.id] = 0f;
                health[u.id] = u.health;
                dead[u.id] = u.isDead;
                shooting[u.id] = false;
                teamOf[u.id] = u.team;
            }

            var snapshots = new List<Snapshot> { CaptureTacticalSnapshotPure(ms, 0, pos, rotation, health, dead, shooting, teamOf) };

            var ticks = events.Select(e => e.tick).Distinct().OrderBy(t => t).ToList();
            foreach (int tick in ticks)
            {
                foreach (var e in events.Where(e => e.tick == tick))
                {
                    switch (e.kind)
                    {
                        case TacticalEvent.Kind.Move:
                            if (pos.TryGetValue(e.unitId, out Vector2 prev) && (e.position - prev).sqrMagnitude > 0.0001f)
                            {
                                Vector2 dir = e.position - prev;
                                rotation[e.unitId] = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                            }
                            pos[e.unitId] = e.position;
                            break;
                        case TacticalEvent.Kind.Shot:
                        case TacticalEvent.Kind.OverwatchTriggered:
                            if (e.targetUnitId != null && health.ContainsKey(e.targetUnitId)) health[e.targetUnitId] -= e.damage;
                            if (shooting.ContainsKey(e.unitId)) shooting[e.unitId] = true;
                            break;
                        case TacticalEvent.Kind.Death:
                            if (dead.ContainsKey(e.unitId)) dead[e.unitId] = true;
                            break;
                    }
                }

                snapshots.Add(CaptureTacticalSnapshotPure(ms, tick * TickDurationMs, pos, rotation, health, dead, shooting, teamOf));
                foreach (var id in shooting.Keys.ToList()) shooting[id] = false;
            }

            return snapshots;
        }

        /// <summary>Équivalent pur de CaptureTacticalSnapshot — lit unit_type/Y cosmétique depuis
        /// MatchState (voir UnitTypeById/CurrentYById) au lieu d'une UnitAI réelle, et fait avancer
        /// ms.Zone directement avec les positions DE CE TICK (pos/dead/teamOf), exactement comme
        /// l'original lisait les vraies UnitAI À CHAQUE tick (pas seulement en fin de résolution).</summary>
        private Snapshot CaptureTacticalSnapshotPure(MatchState ms, int t, Dictionary<string, Vector2> pos, Dictionary<string, float> rotation,
            Dictionary<string, int> health, Dictionary<string, bool> dead, Dictionary<string, bool> shooting, Dictionary<string, int> teamOf)
        {
            var states = new UnitState[pos.Count];
            int i = 0;
            foreach (var id in pos.Keys)
            {
                states[i++] = new UnitState
                {
                    unit_id = id,
                    x = pos[id].x,
                    y = ms.CurrentYById.TryGetValue(id, out float yVal) ? yVal : 0f,
                    z = pos[id].y,
                    ry = rotation[id],
                    health = health[id],
                    dead = dead[id],
                    shooting = shooting[id],
                    unit_type = ms.UnitTypeById.TryGetValue(id, out int ut) ? ut : (int)UnitSpawnerUI.UnitType.Fantassin,
                    team_id = teamOf[id]
                };
            }

            float zoneProgress1 = 0f, zoneProgress2 = 0f;
            if (ms.Zone != null)
            {
                var tickUnits = pos.Keys.Select(id => (id, teamOf[id], pos[id], dead[id])).ToList();
                ms.Zone.Tick(tickUnits);
                zoneProgress1 = ms.Zone.ProgressTeam1;
                zoneProgress2 = ms.Zone.ProgressTeam2;
            }

            return new Snapshot { t = t, units = states, zone_progress_team1 = zoneProgress1, zone_progress_team2 = zoneProgress2 };
        }

        private IEnumerator RunPlanningPhase(PlayerConnection p1, PlayerConnection p2, int turnNumber)
        {
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            float remaining = PlanningSeconds;
            int lastTick = -1;

            // Instrumentation temps réel (voir RunDeploymentPhase) : horodatage UTC réel au début du
            // tour et à chaque envoi de "turn_timer", pour pouvoir vérifier depuis un vrai log que le
            // compte à rebours annoncé aux clients correspond bien au temps RÉELLEMENT écoulé côté
            // serveur — pas seulement à une valeur théorique qui pourrait dériver.
            DateTime planningStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] Tour {turnNumber} — RunPlanningPhase démarré à {planningStartUtc:O} (max {PlanningSeconds}s).");

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    double realElapsed = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
                    Debug.Log($"[Timing] Tour {turnNumber} turn_timer: secondsLeft={secondsLeft} (théorique) après {realElapsed:F1}s réelles écoulées (théorique attendu ~{PlanningSeconds - secondsLeft}s).");
                    var tick = new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft };
                    if (!p1.IsDisconnected) p1.Send(tick);
                    if (!p2.IsDisconnected) p2.Send(tick);
                }

                remaining -= Time.deltaTime;
                yield return null;
            }

            double planningElapsedSec = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
            bool planningTimedOut = !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn);
            Debug.Log($"[Timing] Tour {turnNumber} — RunPlanningPhase terminé après {planningElapsedSec:F1}s réelles (attendu max {PlanningSeconds}s) — p1.HasSubmittedThisTurn={p1.HasSubmittedThisTurn}, p2.HasSubmittedThisTurn={p2.HasSubmittedThisTurn}, expiré par timeout={planningTimedOut}.");

            ApplyForPlayer(p1, p2);
            ApplyForPlayer(p2, p1);
        }

        private void DrainMessages(PlayerConnection conn, int turnNumber)
        {
            while (conn.TryDequeueMessage(out NetMessage msg))
            {
                if (msg.type == "submit_turn" && msg.turn_number == turnNumber)
                {
                    conn.PendingOrders = msg.orders ?? Array.Empty<UnitOrder>();
                    conn.HasSubmittedThisTurn = true;
                }
                else if (msg.type == "heartbeat")
                {
                    conn.LastHeartbeat = DateTime.UtcNow;
                }
                else if (msg.type == "submit_deployment")
                {
                    conn.PendingDeployment = msg.placements ?? Array.Empty<UnitPlacement>();
                    conn.HasSubmittedDeployment = true;
                }
                else if (msg.type == "deployment_ready")
                {
                    conn.MapReady = true;
                }
            }
        }

        private void ApplyForPlayer(PlayerConnection conn, PlayerConnection opponent)
        {
            var myUnits = UnitAI.AllLivingUnits.Where(u => u.teamID == conn.TeamId).ToList();
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                // Substitut IA (réutilise TacticalAIPlanner, exactement comme un ennemi Solo — voir
                // UnitAI.isGhosted) pour un joueur absent/déconnecté/en retard : sans ça, ses unités
                // restaient des mannequins totalement passifs pour le reste du match dès la première
                // absence, ce qui rendait la fin de partie triviale et peu satisfaisante pour
                // l'adversaire resté en ligne (voir rapport d'audit §1.3/§D).
                foreach (var u in myUnits)
                {
                    u.isGhosted = true;
                    u.PlanifierTourIA();
                }

                if (!opponent.IsDisconnected)
                {
                    opponent.Send(new NetMessage
                    {
                        type = "opponent_ghosted",
                        team_id = conn.TeamId,
                        reason = conn.IsDisconnected ? "disconnected" : "timeout"
                    });
                }
            }
            else
            {
                // Le joueur est de retour : ses unités reprennent immédiatement le contrôle humain,
                // isGhosted ne doit pas rester collé à true indéfiniment (voir UnitAI.isGhosted).
                foreach (var u in myUnits) u.isGhosted = false;
                ApplyOrdersToUnits(myUnits, conn.PendingOrders);
            }
        }

        private void ApplyOrdersToUnits(List<UnitAI> myUnits, UnitOrder[] orders)
        {
            if (orders == null) return;

            foreach (var order in orders)
            {
                UnitAI unit = myUnits.FirstOrDefault(u => u.gameObject.name == order.unit_id);
                if (unit == null) continue;

                unit.ClearTacticalPath();
                if (order.path == null) continue;
                // Un client modifié pourrait soumettre un chemin de milliers de points (toujours
                // sous la limite de 8 Mo du protocole) pour faire tourner la simulation de
                // mouvement en boucle inutilement, ou des coordonnées NaN/Infinity/une valeur
                // d'action hors de l'enum pour déclencher un comportement indéfini plus loin dans
                // TacticalPathManager/NavMesh — on rejette silencieusement ce qui est invalide
                // plutôt que de faire confiance au client.
                if (order.path.Length > MaxOrderPathNodes) continue;

                foreach (var node in order.path)
                {
                    if (float.IsNaN(node.x) || float.IsNaN(node.y) || float.IsNaN(node.z)) continue;
                    if (float.IsInfinity(node.x) || float.IsInfinity(node.y) || float.IsInfinity(node.z)) continue;
                    if (!Enum.IsDefined(typeof(TacticalPathManager.NodeAction), node.action)) continue;

                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
            }
        }

        // =====================================================================
        // Résolution de tour — moteur logique déterministe (Novgov.TacticalCore), 2026-08-30.
        // REMPLACE ENTIÈREMENT l'ancienne méthode (simulation Unity réelle : unit.ExecuterOrdres()
        // pilotant NavMeshAgent + Physics.RaycastAll pendant plusieurs secondes de temps réel,
        // échantillonnée en Snapshots). Le résultat officiel (positions, dégâts, morts, murs
        // détruits) est maintenant calculé UNE FOIS, instantanément, par TacticalResolver.Resolve()
        // — une fonction pure sans dépendance à un moteur physique, donc garantie déterministe
        // sur n'importe quel appareil. Le NavMeshAgent/PhysX ne servent plus qu'au rendu visuel
        // solo (mode Hors-Ligne) : ils n'influencent plus jamais un résultat multijoueur.
        //
        // Voir Assets/Scripts/TacticalCore/ pour le moteur lui-même, et la discussion
        // d'architecture (session du 2026-08-30) pour le pourquoi de ce choix.
        // =====================================================================
        private const int TickDurationMs = 250; // durée visuelle d'un pas de résolution (1m de déplacement), pour le rythme de lecture côté client

        private IEnumerator RunExecutionPhase(int turnNumber, PlayerConnection p1, PlayerConnection p2)
        {
            // Instrumentation temps réel + trajectoire (2026-08-30, test grandeur nature) : horodatage
            // UTC réel du début de la résolution (le calcul lui-même est une fonction pure, donc quasi
            // instantané — utile pour confirmer que ce n'est PAS là que le temps "de checkpoint" se
            // perd, contrairement aux phases de planification/déploiement qui, elles, attendent
            // volontairement un vrai timer).
            DateTime executionStartUtc = DateTime.UtcNow;

            var allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude).Where(u => !u.isDead).ToList();
            // Photo stable de BuildingStructure.AllBuildings au même instant que
            // TacticalGridBuilder.BuildFromScene() (qui énumère cette MÊME liste statique) : sert de
            // table id<->référence immunisée contre un retrait en cours de route (un bâtiment
            // détruit PENDANT cette méthode est retiré de la liste VIVANTE par DestructibleEnvironment,
            // ce qui décalerait les index restants si on relisait la liste statique après coup).
            var buildingsSnapshot = new List<BuildingStructure>(BuildingStructure.AllBuildings);
            var buildingIndex = new Dictionary<BuildingStructure, int>();
            for (int i = 0; i < buildingsSnapshot.Count; i++) buildingIndex[buildingsSnapshot[i]] = i;

            TacticalWorldState worldState = TacticalGridBuilder.BuildFromScene();
            double gridBuildMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] Tour {turnNumber} : TacticalGridBuilder.BuildFromScene() en {gridBuildMs:F1}ms réelles ({worldState.buildings.Count} bâtiment(s), {worldState.wallSegments.Count} mur(s), {worldState.barricades.Count} barricade(s)).");
            var originalBuildingHealth = worldState.buildings.ToDictionary(b => b.id, b => b.health);

            var unitById = new Dictionary<string, UnitAI>();
            // Référence RÉELLE de la fenêtre visée par un ordre GarnisonFenetre, par unité — la
            // normale de fenêtre (float2) transitant par TacticalCore ne suffit pas à elle seule à
            // retrouver quel BuildingStructure.BuildingWindow occuper pour de vrai (deux fenêtres
            // symétriques peuvent partager la même normale) : voir l'application après résolution.
            var pendingWindowByUnitId = new Dictionary<string, BuildingStructure.BuildingWindow>();
            var mortarStrikes = new List<Vector2>();
            var ordersTeam1 = new List<UnitOrders>();
            var ordersTeam2 = new List<UnitOrders>();

            foreach (var unit in allUnits)
            {
                string id = unit.gameObject.name;
                unitById[id] = unit;
                worldState.units.Add(BuildTacticalUnit(unit, buildingIndex));

                UnitOrders orders = BuildUnitOrders(unit, mortarStrikes, buildingIndex, pendingWindowByUnitId);
                if (orders.checkpoints.Count > 0)
                {
                    (unit.teamID == 1 ? ordersTeam1 : ordersTeam2).Add(orders);
                }
            }

            // Trajectoires envoyées au résolveur pour ce tour — vérifie depuis les logs que chaque
            // unité avec un ordre a bien un chemin de la bonne longueur (voir MaxOrderPathNodes côté
            // validation réseau) avant même de lancer le calcul.
            Debug.Log($"[Trajectoire] Tour {turnNumber} : équipe1={ordersTeam1.Count} unité(s) avec ordres ({string.Join(", ", ordersTeam1.Select(o => $"{o.unitId}:{o.checkpoints.Count}pt"))}), équipe2={ordersTeam2.Count} unité(s) avec ordres ({string.Join(", ", ordersTeam2.Select(o => $"{o.unitId}:{o.checkpoints.Count}pt"))}), {mortarStrikes.Count} frappe(s) de mortier.");

            double preResolveMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            List<TacticalEvent> tacticalEvents = TacticalResolver.Resolve(worldState, ordersTeam1, ordersTeam2, mortarStrikes);
            double resolveOnlyMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds - preResolveMs;

            // Décomposition du temps réel (2026-08-30, test grandeur nature — la mesure globale
            // précédente ("2s pour une résolution soi-disant instantanée") mélangeait la construction
            // de la géométrie ET le calcul pur ; on isole maintenant les deux pour savoir lequel coûte
            // réellement cher.
            Debug.Log($"[Timing] Tour {turnNumber} : préparation (grille+ordres) {preResolveMs:F1}ms réelles, TacticalResolver.Resolve() SEUL {resolveOnlyMs:F1}ms réelles ({tacticalEvents.Count} événement(s) générés).");

            // Applique le résultat officiel aux VRAIES UnitAI EN MÊME TEMPS que la capture des
            // snapshots (tick par tick), pas en un seul bloc à la toute fin : CaptureZone.Tick()
            // (mode Zone de Contrôle) lit les positions RÉELLES des UnitAI de la scène à chaque
            // pas — les appliquer seulement après coup lui aurait fait lire des positions figées
            // en début de tour pour la totalité de la résolution.
            var snapshots = BuildSnapshotsFromEvents(tacticalEvents, allUnits, unitById);

            // États persistants (garnison/guet/camouflage/bâtiment/hauteur) : contrairement à la
            // position/PV, ils n'influencent pas CaptureZone.Tick(), donc un seul passage en fin
            // de résolution suffit — voir le tableau de persistance du rapport d'audit §8.2
            // (isGuarding/isGarrisoned ne sont JAMAIS remis à false automatiquement).
            foreach (var tu in worldState.units)
            {
                if (!unitById.TryGetValue(tu.id, out UnitAI unit) || unit == null) continue;
                unit.isGuarding = tu.isGuarding;
                unit.isCamouflaged = tu.isCamouflaged;

                // Garnison de fenêtre : occupe/libère la VRAIE fenêtre (état partagé avec
                // BuildingStructure, lu par d'autres systèmes via BuildingWindow.isOccupied) plutôt
                // que de ne poser que le booléen isGarrisoned — sans ça, unit.currentWindow resterait
                // toujours null et la restriction de cône de tir (CanEngageTarget) disparaîtrait
                // silencieusement dès le tour suivant (voir BuildTacticalUnit qui relit currentWindow).
                pendingWindowByUnitId.TryGetValue(tu.id, out var newWindow);
                bool wantsWindow = tu.isGarrisoned && newWindow != null;
                if (unit.currentWindow != null && (!wantsWindow || unit.currentWindow != newWindow))
                {
                    unit.LeaveGarrison(); // libère l'ancienne fenêtre proprement (isGarrisoned repasse à false ici)
                }
                if (wantsWindow && unit.currentWindow != newWindow)
                {
                    BuildingStructure.FindBuildingAt(new Vector3(newWindow.position.x, 0f, newWindow.position.z))?.OccupyWindow(newWindow, unit);
                    unit.currentWindow = newWindow;
                }
                unit.isGarrisoned = tu.isGarrisoned;

                BuildingStructure newBuilding = tu.currentBuildingId >= 0 && tu.currentBuildingId < buildingsSnapshot.Count ? buildingsSnapshot[tu.currentBuildingId] : null;
                if (unit.currentBuilding != newBuilding)
                {
                    unit.currentBuilding?.UnregisterUnitInside(unit);
                    newBuilding?.RegisterUnitInside(unit);
                    unit.currentBuilding = newBuilding;
                }

                if (tu.outputY.HasValue) unit.transform.position = new Vector3(unit.transform.position.x, tu.outputY.Value, unit.transform.position.z);
            }

            // Répercute les dégâts de zone sur les VRAIS composants DestructibleEnvironment (§7 du
            // rapport) — TakeDamage() gère lui-même le passage à isDestroyed et la vraie
            // destruction de scène (colliders désactivés, retrait de AllBuildings, etc.) ; on ne
            // duplique jamais cette logique, seul le calcul du montant de dégâts vient de
            // TacticalResolver.
            foreach (var tb in worldState.buildings)
            {
                float dealt = originalBuildingHealth[tb.id] - tb.health;
                if (dealt <= 0f) continue;
                if (tb.id < 0 || tb.id >= buildingsSnapshot.Count) continue;
                DestructibleEnvironment env = buildingsSnapshot[tb.id]?.GetComponent<DestructibleEnvironment>();
                env?.TakeDamage(dealt);
            }

            foreach (var unit in allUnits)
            {
                unit.ResetOrderState();
            }

            // Brouillard de guerre réseau (2026-08-30, voir rapport d'audit) : jusqu'ici le serveur
            // envoyait l'état COMPLET des deux camps, identique, aux deux clients à chaque tick — un
            // joueur voyait donc la position exacte de chaque unité ennemie en permanence, repérée ou
            // non. On construit maintenant un message DISTINCT par destinataire, filtré sur ce que
            // son camp peut réellement voir à cet instant (ComputeVisibleUnitIds, même calcul que
            // IsUnitSpottedByTeam en mode Solo — voir LineOfSight.CanBeSpotted).
            var unitMetaById = worldState.units.ToDictionary(u => u.id);
            var teamById = unitById.ToDictionary(kv => kv.Key, kv => kv.Value.teamID);

            Snapshot[] snapshotsForTeam1 = FilterSnapshotsForTeam(snapshots, worldState, unitMetaById, teamById, 1);
            Snapshot[] snapshotsForTeam2 = FilterSnapshotsForTeam(snapshots, worldState, unitMetaById, teamById, 2);

            // Résumé de visibilité par tour (brouillard de guerre) : nombre MAX d'ennemis vus sur un
            // seul tick de ce tour, par équipe — volontairement agrégé (pas un log par paire
            // observateur/cible par tick comme le diagnostic temporaire du 2026-08-30 déjà retiré)
            // pour rester lisible sur un vrai test grandeur nature avec beaucoup de tours.
            int maxEnemiesVisibleToTeam1 = snapshotsForTeam1.Length == 0 ? 0 : snapshotsForTeam1.Max(s => s.units.Count(u => u.team_id == 2));
            int maxEnemiesVisibleToTeam2 = snapshotsForTeam2.Length == 0 ? 0 : snapshotsForTeam2.Max(s => s.units.Count(u => u.team_id == 1));
            Debug.Log($"[Vision] Tour {turnNumber} : {snapshots.Count} tick(s) — équipe1 a vu au maximum {maxEnemiesVisibleToTeam1} ennemi(s) sur un tick, équipe2 a vu au maximum {maxEnemiesVisibleToTeam2} ennemi(s) sur un tick.");

            if (!p1.IsDisconnected)
            {
                p1.Send(new NetMessage
                {
                    type = "turn_result",
                    turn_number = turnNumber,
                    snapshot_interval_ms = TickDurationMs,
                    snapshots = snapshotsForTeam1
                });
            }
            if (p2 != null && !p2.IsDisconnected)
            {
                p2.Send(new NetMessage
                {
                    type = "turn_result",
                    turn_number = turnNumber,
                    snapshot_interval_ms = TickDurationMs,
                    snapshots = snapshotsForTeam2
                });
            }

            double totalPhaseElapsedMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] Tour {turnNumber} : RunExecutionPhase entièrement terminé (calcul + envoi réseau) en {totalPhaseElapsedMs:F1}ms réelles.");

            yield break; // résolution instantanée : plus d'attente en temps réel côté serveur
        }

        /// <summary>Construit, pour UNE équipe destinataire, une copie de <paramref name="fullSnapshots"/>
        /// dont chaque tick ne contient plus que les unités que cette équipe peut réellement voir à
        /// cet instant (voir ComputeVisibleUnitIds) — ne recalcule PAS zone_progress_team1/2 (déjà
        /// figés sur chaque Snapshot source, un seul calcul par tick suffit, voir
        /// CaptureTacticalSnapshot/CaptureZone.Tick appelé une seule fois pendant BuildSnapshotsFromEvents).</summary>
        private static Snapshot[] FilterSnapshotsForTeam(List<Snapshot> fullSnapshots, TacticalWorldState staticGeometry,
            Dictionary<string, TacticalUnit> unitMetaById, Dictionary<string, int> teamById, int observingTeam)
        {
            var result = new Snapshot[fullSnapshots.Count];
            for (int i = 0; i < fullSnapshots.Count; i++)
            {
                Snapshot src = fullSnapshots[i];
                HashSet<string> visibleIds = ComputeVisibleUnitIds(staticGeometry, src.units, unitMetaById, teamById, observingTeam);
                result[i] = new Snapshot
                {
                    t = src.t,
                    zone_progress_team1 = src.zone_progress_team1,
                    zone_progress_team2 = src.zone_progress_team2,
                    units = src.units.Where(u => visibleIds.Contains(u.unit_id)).ToArray()
                };
            }
            return result;
        }

        /// <summary>Ensemble des identifiants d'unités visibles par <paramref name="observingTeam"/> à
        /// UN tick donné : ses propres unités toujours, plus toute unité adverse VIVANTE repérée par
        /// au moins une de ses unités vivantes (LineOfSight.CanBeSpotted — même calcul
        /// qu'IsUnitSpottedByTeam en Solo). Un adversaire mort reste visible (le résultat d'un combat
        /// ne doit pas disparaître). Approximation assumée : la géométrie des murs/barricades utilisée
        /// ici est celle de FIN de tour (staticGeometry, capturée après TacticalResolver.Resolve),
        /// même pour les premiers ticks de la relecture — un mur détruit PENDANT ce tour est donc
        /// traité comme déjà détruit pour TOUTE la relecture, jamais l'inverse : ça ne peut donc que
        /// masquer une unité un peu plus longtemps que la réalité, jamais révéler une position qui
        /// aurait dû rester cachée (l'erreur reste toujours du côté prudent).</summary>
        private static HashSet<string> ComputeVisibleUnitIds(TacticalWorldState staticGeometry, UnitState[] tickUnits,
            Dictionary<string, TacticalUnit> unitMetaById, Dictionary<string, int> teamById, int observingTeam)
        {
            var visible = new HashSet<string>();
            var observers = new List<TacticalUnit>();
            var enemies = new List<(string id, TacticalUnit unit)>();

            foreach (var us in tickUnits)
            {
                if (!teamById.TryGetValue(us.unit_id, out int team)) continue;
                if (!unitMetaById.TryGetValue(us.unit_id, out TacticalUnit meta)) continue;

                var tickUnit = new TacticalUnit
                {
                    id = us.unit_id,
                    team = team,
                    position = new Vector2(us.x, us.z),
                    zStrata = us.y > 2.2f ? ZStrata.Toit : ZStrata.Sol,
                    isDead = us.dead,
                    isCamouflaged = meta.isCamouflaged,
                    isMortar = meta.isMortar,
                };

                if (team == observingTeam)
                {
                    visible.Add(us.unit_id); // sa propre équipe est toujours visible pour elle-même
                    if (!tickUnit.isDead) observers.Add(tickUnit);
                }
                else
                {
                    enemies.Add((us.unit_id, tickUnit));
                }
            }

            // Vérifié en simulant une partie complète contre le serveur en production (2026-08-30,
            // deux escouades convergeant vers le même point) : le repérage se déclenche correctement
            // dès que la ligne de vue est réellement dégagée (voir LineOfSight.CanBeSpotted), et
            // reste correctement bloqué par un mur/bâtiment réel entre les deux camps sinon — ce
            // n'est PAS un bug, c'est le même comportement que le mode Solo (audit du 2026-08-30).
            foreach (var (id, enemy) in enemies)
            {
                if (enemy.isDead) { visible.Add(id); continue; } // un corps ne "réapparaît" pas soudainement, il reste visible
                foreach (var observer in observers)
                {
                    if (LineOfSight.CanBeSpotted(staticGeometry, observer, enemy)) { visible.Add(id); break; }
                }
            }

            return visible;
        }

        /// <summary>Extrait l'état logique (Novgov.TacticalCore.TacticalUnit) d'une UnitAI vivante
        /// — TOUTES les valeurs viennent de l'audit exhaustif du 2026-08-30 (UnitAI_Combat.cs,
        /// UnitAI.cs), rien n'est approximé.</summary>
        private static TacticalUnit BuildTacticalUnit(UnitAI unit, Dictionary<BuildingStructure, int> buildingIndex)
        {
            int buildingId = unit.currentBuilding != null && buildingIndex.TryGetValue(unit.currentBuilding, out int bid) ? bid : -1;

            return new TacticalUnit
            {
                id = unit.gameObject.name,
                team = unit.teamID,
                position = new Vector2(unit.transform.position.x, unit.transform.position.z),
                // Seuil exact de UnitAI.isRooftopSniper (UnitAI.cs:48-53) : Y > 2.2m = toit.
                zStrata = unit.transform.position.y > 2.2f ? ZStrata.Toit : ZStrata.Sol,
                health = unit.health,
                isDead = unit.isDead,
                isCamouflaged = unit.isCamouflaged,
                isGarrisoned = unit.isGarrisoned,
                isGuarding = unit.isGuarding,
                isMortar = unit.isMortar,
                currentBuildingId = buildingId,
                windowNormal = unit.currentWindow != null ? new Vector2(unit.currentWindow.outwardNormal.x, unit.currentWindow.outwardNormal.z) : (Vector2?)null,
                // Repérage (IsUnitSpottedByTeam, ligne 207) : 55m toit / 25m mortier / 35m sinon.
                spottingRange = unit.transform.position.y > 2.2f ? 55f : (unit.isMortar ? 25f : 35f),
                // Engagement personnel (GetVisibleEnemy, ligne 245) : porteeDetection (le bonus
                // toit +20m est appliqué dans LineOfSight.CanEngageTarget selon zStrata, jamais ici).
                engagementRange = unit.porteeDetection,
                // UnitAI_Combat.cs:409 — 15 infanterie / 75 canon-véhicule / 150 char lourd. Le
                // mortier n'a pas de tir direct (0) : ses dégâts viennent uniquement des frappes de
                // zone (mortarStrikes/TirMortier).
                weaponDamage = unit.isMortar ? 0 : (unit.isTank ? (unit.isCanonVehicle ? 75 : 150) : 15),
                // UnitAI_Combat.cs:87-91 — cadence de tir exacte par type.
                weaponCooldownSeconds = unit.isMortar ? 0f : (unit.isTank ? (unit.isCanonVehicle ? 1.4f : 1.8f) : 0.35f),
            };
        }

        // Cône d'Overwatch par défaut (NOUVELLE fonctionnalité, pas dans l'original — voir
        // TacticalTypes.cs en-tête) : 120° de large (cos(60°) = 0.5, précalculé — TacticalCore
        // n'appelle jamais de fonction trigonométrique), portée 30m.
        private const float OverwatchConeCosHalfAngle = 0.5f;
        private const float OverwatchRange = 30f;

        /// <summary>Convertit le chemin tactique déjà posé sur l'UnitAI (unit.tacticalPath, rempli
        /// par ApplyOrdersToUnits depuis les ordres réseau — inchangé) en UnitOrders pour
        /// TacticalResolver — miroir fidèle de chaque NodeAction (rapport d'audit §2.6-2.11).
        /// Simplification assumée : seule la DERNIÈRE action du chemin est appliquée (cas
        /// quasi-systématique en pratique — un ordre = un déplacement puis une posture finale) ;
        /// plusieurs actions à des points intermédiaires différents du même chemin ne sont pas
        /// toutes conservées, seule la dernière l'est.</summary>
        private static UnitOrders BuildUnitOrders(UnitAI unit, List<Vector2> mortarStrikesOut, Dictionary<BuildingStructure, int> buildingIndex, Dictionary<string, BuildingStructure.BuildingWindow> pendingWindowByUnitId)
        {
            var orders = new UnitOrders { unitId = unit.gameObject.name };
            Vector2 previousPos = new Vector2(unit.transform.position.x, unit.transform.position.z);
            bool wasOnRoof = unit.transform.position.y > 2.2f;
            bool climbsThisOrder = false;

            foreach (var node in unit.tacticalPath)
            {
                Vector2 pos2D = new Vector2(node.position.x, node.position.z);

                if (node.action == TacticalPathManager.NodeAction.TirMortier)
                {
                    mortarStrikesOut.Add(pos2D);
                    continue;
                }

                var checkpoint = new PathCheckpoint { position = pos2D };

                switch (node.action)
                {
                    case TacticalPathManager.NodeAction.Guetter:
                    case TacticalPathManager.NodeAction.Embuscade:
                        // NOUVEAU (Overwatch actif) EN PLUS de l'effet original (isGuarding, -50%
                        // dégâts reçus, jamais remis à false automatiquement — rapport §2.10/§8.2).
                        checkpoint.setGuarding = true;
                        checkpoint.enterOverwatchAtEnd = true;
                        checkpoint.overwatchToSet = BuildOverwatchTrigger(pos2D, previousPos, node.action);
                        break;

                    case TacticalPathManager.NodeAction.GuetterPorte:
                        // Garnison de porte (rapport §2.9) : -75% dégâts (comme une fenêtre), mais
                        // SANS restriction de cône (windowNormal reste null). Overwatch (nouveau)
                        // en plus, sur la porte elle-même.
                        checkpoint.setGarrisonDoor = true;
                        checkpoint.enterOverwatchAtEnd = true;
                        checkpoint.overwatchToSet = BuildOverwatchTrigger(pos2D, previousPos, node.action);
                        break;

                    case TacticalPathManager.NodeAction.SeCacher:
                        checkpoint.setCamouflaged = true;
                        break;

                    case TacticalPathManager.NodeAction.GarnisonFenetre:
                        {
                            BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(pos2D.x, 0f, pos2D.y));
                            BuildingStructure.BuildingWindow window = building?.GetClosestWindow(new Vector3(pos2D.x, 0f, pos2D.y));
                            if (window != null)
                            {
                                checkpoint.setGarrisonWindow = true;
                                checkpoint.windowNormalToSet = new Vector2(window.outwardNormal.x, window.outwardNormal.z);
                                pendingWindowByUnitId[unit.gameObject.name] = window;
                            }
                            break;
                        }

                    case TacticalPathManager.NodeAction.EntrerBatiment:
                        {
                            BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(pos2D.x, 0f, pos2D.y));
                            if (building != null && buildingIndex.TryGetValue(building, out int bid)) checkpoint.enterBuildingId = bid;
                            break;
                        }

                    case TacticalPathManager.NodeAction.SortirBatiment:
                        checkpoint.exitBuilding = true;
                        break;

                    case TacticalPathManager.NodeAction.Escalade:
                        {
                            // Hauteur exacte du parapet = hauteur du bâtiment escaladé (même
                            // ancrage que CreateRoofAccess côté rendu).
                            BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(pos2D.x, 0f, pos2D.y));
                            checkpoint.setPositionY = building != null ? building.height : 3f;
                            climbsThisOrder = true;
                            break;
                        }
                }

                orders.checkpoints.Add(checkpoint);
                previousPos = pos2D;
            }

            // Descente implicite (rapport §2.3/§2.5) : l'ancien code déclenche ExecuteClimbDown dès
            // que le prochain point demandé est nettement plus bas QUE la position actuelle, sans
            // action dédiée dans l'enum — reproduit ici en ramenant au sol toute unité déjà sur un
            // toit qui reçoit un nouvel ordre sans ré-escalader dans le même ordre. Appliquée une
            // fois pour tout l'ordre (pas liée à un checkpoint précis), voir UnitOrders.implicitDescentY.
            if (wasOnRoof && !climbsThisOrder && orders.checkpoints.Count > 0)
            {
                orders.implicitDescentY = 0f;
            }

            return orders;
        }

        private static OverwatchTrigger BuildOverwatchTrigger(Vector2 finalPos, Vector2 previousPos, TacticalPathManager.NodeAction action)
        {
            if (action == TacticalPathManager.NodeAction.GuetterPorte)
            {
                BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(finalPos.x, 0f, finalPos.y));
                BuildingStructure.BuildingDoor door = building?.GetClosestDoor(new Vector3(finalPos.x, 0f, finalPos.y));
                if (door != null)
                {
                    Vector2 doorPos = new Vector2(door.position.x, door.position.z);
                    // Perpendiculaire à la normale de la porte SANS trigonométrie (rotation 90° d'un
                    // vecteur 2D = simple permutation de composantes).
                    Vector2 tangent = new Vector2(-door.entryDirection.z, door.entryDirection.x);
                    float halfWidth = Mathf.Max(1f, door.width);
                    return new OverwatchTrigger { linePointA = doorPos - tangent * halfWidth, linePointB = doorPos + tangent * halfWidth };
                }
                // Repli sur un cône classique si aucune porte n'est trouvée à proximité.
            }

            Vector2 facing = finalPos - previousPos;
            facing = facing.sqrMagnitude < 0.0001f ? Vector2.up : facing.normalized;
            return new OverwatchTrigger { origin = finalPos, facing = facing, cosHalfAngle = OverwatchConeCosHalfAngle, range = OverwatchRange };
        }

        /// <summary>Équivalent pur de BuildOverwatchTrigger — résout la porte la plus proche depuis
        /// ms.World (voir MatchGeometry), jamais BuildingStructure.FindBuildingAt/GetClosestDoor
        /// (scène vivante). 2026-08-30, "des milliers de cartes".</summary>
        private static OverwatchTrigger BuildOverwatchTriggerPure(TacticalWorldState world, Vector2 finalPos, Vector2 previousPos, TacticalPathManager.NodeAction action)
        {
            if (action == TacticalPathManager.NodeAction.GuetterPorte)
            {
                int bIdx = MatchGeometry.FindBuildingAt(world, finalPos);
                TacticalBuilding building = bIdx >= 0 ? world.GetBuilding(bIdx) : null;
                TacticalDoor door = building != null ? MatchGeometry.GetClosestDoor(building, finalPos) : null;
                if (door != null)
                {
                    Vector2 tangent = new Vector2(-door.entryDirection.y, door.entryDirection.x);
                    float halfWidth = Mathf.Max(1f, door.width);
                    return new OverwatchTrigger { linePointA = door.position - tangent * halfWidth, linePointB = door.position + tangent * halfWidth };
                }
            }

            Vector2 facing = finalPos - previousPos;
            facing = facing.sqrMagnitude < 0.0001f ? Vector2.up : facing.normalized;
            return new OverwatchTrigger { origin = finalPos, facing = facing, cosHalfAngle = OverwatchConeCosHalfAngle, range = OverwatchRange };
        }

        /// <summary>Rejoue le journal d'événements de TacticalResolver en une série de Snapshots —
        /// même format réseau qu'avant (NetMessage.Snapshot/UnitState), donc AUCUN changement côté
        /// client : MultiplayerMatchController.PlaySnapshotsCoroutine continue de simplement
        /// afficher ce qu'on lui envoie, sans jamais recalculer quoi que ce soit lui-même. Seule la
        /// SOURCE des positions change (TacticalResolver au lieu d'une simulation Unity live).</summary>
        private List<Snapshot> BuildSnapshotsFromEvents(List<TacticalEvent> events, List<UnitAI> unitsBeforeResolution, Dictionary<string, UnitAI> unitById)
        {
            var pos = new Dictionary<string, Vector2>();
            var rotation = new Dictionary<string, float>();
            var health = new Dictionary<string, int>();
            var dead = new Dictionary<string, bool>();
            var shooting = new Dictionary<string, bool>();

            foreach (var u in unitsBeforeResolution)
            {
                string id = u.gameObject.name;
                pos[id] = new Vector2(u.transform.position.x, u.transform.position.z);
                rotation[id] = u.transform.eulerAngles.y;
                health[id] = u.health;
                dead[id] = u.isDead;
                shooting[id] = false;
            }

            var snapshots = new List<Snapshot> { CaptureTacticalSnapshot(0, pos, rotation, health, dead, shooting, unitsBeforeResolution) };

            var ticks = events.Select(e => e.tick).Distinct().OrderBy(t => t).ToList();
            foreach (int tick in ticks)
            {
                foreach (var e in events.Where(e => e.tick == tick))
                {
                    switch (e.kind)
                    {
                        case TacticalEvent.Kind.Move:
                            // Rotation cosmétique uniquement (voir en-tête de méthode) : dérivée de
                            // la direction de déplacement via Atan2 — jamais utilisé pour décider
                            // du résultat officiel, donc aucun souci de déterminisme inter-appareil ici.
                            if (pos.TryGetValue(e.unitId, out Vector2 prev) && (e.position - prev).sqrMagnitude > 0.0001f)
                            {
                                Vector2 dir = e.position - prev;
                                rotation[e.unitId] = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                            }
                            pos[e.unitId] = e.position;
                            break;
                        case TacticalEvent.Kind.Shot:
                        case TacticalEvent.Kind.OverwatchTriggered:
                            if (e.targetUnitId != null && health.ContainsKey(e.targetUnitId)) health[e.targetUnitId] -= e.damage;
                            if (shooting.ContainsKey(e.unitId)) shooting[e.unitId] = true;
                            break;
                        case TacticalEvent.Kind.Death:
                            if (dead.ContainsKey(e.unitId)) dead[e.unitId] = true;
                            break;
                    }
                }

                // Applique le résultat de CE pas aux vraies UnitAI AVANT de capturer le snapshot :
                // SetNetworkHealth/ApplyNetworkDeath (jamais TakeDamage, voir RunExecutionPhase),
                // pour que CaptureZone.Tick() (mode Zone de Contrôle, appelé depuis
                // CaptureTacticalSnapshot) lise des positions à jour.
                foreach (var id in pos.Keys)
                {
                    if (!unitById.TryGetValue(id, out UnitAI unit) || unit == null) continue;
                    unit.transform.position = new Vector3(pos[id].x, unit.transform.position.y, pos[id].y);
                    unit.SetNetworkHealth(health[id]);
                    if (dead[id] && !unit.isDead) unit.ApplyNetworkDeath();
                }

                snapshots.Add(CaptureTacticalSnapshot(tick * TickDurationMs, pos, rotation, health, dead, shooting, unitsBeforeResolution));
                foreach (var id in shooting.Keys.ToList()) shooting[id] = false; // le "flash" de tir ne dure qu'un instant visuel
            }

            return snapshots;
        }

        private Snapshot CaptureTacticalSnapshot(int t, Dictionary<string, Vector2> pos, Dictionary<string, float> rotation,
            Dictionary<string, int> health, Dictionary<string, bool> dead, Dictionary<string, bool> shooting, List<UnitAI> units)
        {
            var states = new UnitState[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                string id = units[i].gameObject.name;
                Vector2 p = pos[id];
                states[i] = new UnitState
                {
                    unit_id = id,
                    x = p.x,
                    y = units[i].transform.position.y, // hauteur cosmétique : voir la limite notée dans BuildTacticalUnit
                    z = p.y,
                    ry = rotation[id],
                    health = health[id],
                    dead = dead[id],
                    shooting = shooting[id],
                    unit_type = InferUnitType(units[i]),
                    team_id = units[i].teamID
                };
            }

            float zoneProgress1 = 0f, zoneProgress2 = 0f;
            if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)
            {
                CaptureZone.Instance.Tick();
                zoneProgress1 = CaptureZone.Instance.ProgressTeam1;
                zoneProgress2 = CaptureZone.Instance.ProgressTeam2;
            }

            return new Snapshot { t = t, units = states, zone_progress_team1 = zoneProgress1, zone_progress_team2 = zoneProgress2 };
        }

        // =====================================================================
        // Persistance minimale via PostgREST (rôle service_role, contourne RLS).
        // Le log détaillé par tour (match_turn_orders / match_event_log) n'est pas
        // encore branché ici — non nécessaire pour le test à 2 téléphones, à ajouter
        // plus tard si un historique de partie détaillé est requis.
        // =====================================================================

        private IEnumerator FetchUsername(PlayerConnection conn)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{conn.UserId}&select=username";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                try
                {
                    var parsed = JsonUtility.FromJson<UsernameQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0 && !string.IsNullOrEmpty(parsed.items[0].username))
                        conn.Username = parsed.items[0].username;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing username échoué : {ex.Message}");
                }
            }
        }

        /// <summary>p2 peut être null (garnison IA de conquête, voir RunConquestSkirmish) : dans ce
        /// cas aucune ligne n'est insérée pour l'équipe 2 (une IA n'a pas de user_id).</summary>
        private IEnumerator CreateMatchRecord(string matchId, PlayerConnection p1, PlayerConnection p2, string mode)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string matchJson = "{\"id\":\"" + matchId + "\",\"status\":\"active\",\"mode\":\"" + mode + "\",\"started_at\":\"" + nowIso + "\"}";
            yield return PostgrestPost("/matches", matchJson);

            string participantsJson = p2 != null
                ? "[" +
                    "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p1.UserId + "\",\"team_id\":1}," +
                    "{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p2.UserId + "\",\"team_id\":2}" +
                  "]"
                : "[{\"match_id\":\"" + matchId + "\",\"user_id\":\"" + p1.UserId + "\",\"team_id\":1}]";
            yield return PostgrestPost("/match_participants", participantsJson);
        }

        private IEnumerator CloseMatchRecord(string matchId, int winnerTeam)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            string patchJson = "{\"status\":\"finished\",\"winner_team\":" + winnerTeam + ",\"ended_at\":\"" + nowIso + "\"}";
            yield return PostgrestPatch($"/matches?id=eq.{matchId}", patchJson);
        }

        private IEnumerator PostgrestPost(string path, string jsonBody)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Écriture DB échouée ({path}) : {req.error} — {req.downloadHandler.text}");
        }

        private IEnumerator PostgrestPatch(string path, string jsonBody)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "PATCH");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Mise à jour DB échouée ({path}) : {req.error} — {req.downloadHandler.text}");
        }

        /// <summary>Comme PostgrestPost, mais avec "Prefer: resolution=merge-duplicates" pour écraser
        /// une ligne existante au lieu d'échouer en conflit — utilisé pour le heartbeat
        /// "server_instances" (ReportInstanceStatus), où chaque instance écrase sans risque sa PROPRE
        /// ligne à chaque battement. Ne PAS réutiliser pour la capture de Zones : voir CaptureZoneInDb,
        /// qui a besoin d'une écriture conditionnelle, pas d'un simple écrasement (rapport d'audit §1.1).</summary>
        private IEnumerator PostgrestUpsert(string path, string jsonBody)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "resolution=merge-duplicates,return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Upsert échoué ({path}) : {req.error} — {req.downloadHandler.text}");
        }

        [Serializable] private class UsernameEntry { public string username; }
        [Serializable] private class UsernameQueryResult { public UsernameEntry[] items; }

        // =====================================================================
        // Classement ELO — profiles.rating existe déjà (schema.sql), jamais mis à jour avant.
        // =====================================================================
        private const float EloKFactor = 32f;

        private IEnumerator UpdateRatings(PlayerConnection p1, PlayerConnection p2, int winnerTeam)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=in.({p1.UserId},{p2.UserId})&select=id,rating";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture des ratings échouée : {req.error}");
                yield break;
            }

            int rating1 = 1000, rating2 = 1000;
            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<RatingQueryResult>(wrapped);
                if (parsed?.items != null)
                {
                    foreach (var entry in parsed.items)
                    {
                        if (entry.id == p1.UserId) rating1 = entry.rating;
                        else if (entry.id == p2.UserId) rating2 = entry.rating;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchSessionManager] Parsing ratings échoué : {ex.Message}");
                yield break;
            }

            // winnerTeam == 0 : match nul (anéantissement mutuel ou double déconnexion) -> 0.5 partout.
            float score1 = winnerTeam == 0 ? 0.5f : (winnerTeam == p1.TeamId ? 1f : 0f);
            float score2 = winnerTeam == 0 ? 0.5f : (winnerTeam == p2.TeamId ? 1f : 0f);

            float expected1 = 1f / (1f + Mathf.Pow(10f, (rating2 - rating1) / 400f));
            float expected2 = 1f / (1f + Mathf.Pow(10f, (rating1 - rating2) / 400f));

            // Mathf.Max(0, ...) : sans plafond bas, un joueur en série de défaites pouvait voir son
            // rating calculé descendre sous 0 (voir rapport d'audit §1.9) — un score ELO négatif n'a
            // pas de sens affiché sur un classement.
            int newRating1 = Mathf.Max(0, Mathf.RoundToInt(rating1 + EloKFactor * (score1 - expected1)));
            int newRating2 = Mathf.Max(0, Mathf.RoundToInt(rating2 + EloKFactor * (score2 - expected2)));

            p1.NewRating = newRating1;
            p1.RatingDelta = newRating1 - rating1;
            p2.NewRating = newRating2;
            p2.RatingDelta = newRating2 - rating2;

            yield return PostgrestPatch($"/profiles?id=eq.{p1.UserId}", "{\"rating\":" + newRating1 + "}");
            yield return PostgrestPatch($"/profiles?id=eq.{p2.UserId}", "{\"rating\":" + newRating2 + "}");

            Debug.Log($"[MatchSessionManager] Ratings mis à jour : {p1.UserId} {rating1}->{newRating1}, {p2.UserId} {rating2}->{newRating2}");
        }

        [Serializable] private class RatingEntry { public string id; public int rating; }
        [Serializable] private class RatingQueryResult { public RatingEntry[] items; }

        private const int ConquestCaptureRatingGain = 8;
        private const int ConquestFailedAttackRatingLoss = 4;

        /// <summary>Petit ajustement de classement pour un combat de conquête (NOUVEAU, 2026-08-30) —
        /// le calcul ELO complet (UpdateRatings) suppose un adversaire humain noté ; ici
        /// l'"adversaire" est une garnison IA sans rating propre, donc un delta fixe plutôt qu'un
        /// calcul ELO entre deux joueurs. Objectif : donner une raison visible (le classement déjà
        /// affiché sur le leaderboard) de jouer ce mode, qui n'avait jusqu'ici AUCUNE récompense
        /// mesurable (voir rapport d'audit §2.B) — capturer une Zone ne faisait qu'écrire une ligne
        /// invisible en base, sans aucun impact sur rien de visible par le joueur.</summary>
        private IEnumerator ApplyConquestRatingDelta(PlayerConnection attacker, bool captured, bool won)
        {
            // "won mais pas captured" = combat gagné, mais un autre attaquant a pris la Zone entre
            // temps (voir le champ reason="zone_lost_race" dans RunConquestSkirmish) : ni récompense
            // ni pénalité, ce n'est pas la faute du joueur.
            int delta = captured ? ConquestCaptureRatingGain : (won ? 0 : -ConquestFailedAttackRatingLoss);
            if (delta == 0)
            {
                attacker.NewRating = 0;
                attacker.RatingDelta = 0;
                yield break;
            }

            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{attacker.UserId}&select=rating";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            int currentRating = 1000;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<RatingQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0) currentRating = parsed.items[0].rating;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing rating (conquête) échoué : {ex.Message}");
                }
            }

            int newRating = Mathf.Max(0, currentRating + delta);
            attacker.NewRating = newRating;
            attacker.RatingDelta = newRating - currentRating;
            yield return PostgrestPatch($"/profiles?id=eq.{attacker.UserId}", "{\"rating\":" + newRating + "}");
        }

        // =====================================================================
        // Conquête territoriale — Zones de Conquête (grille Slippy Map fixe, Zoom
        // CityGenerator.ZONE_ZOOM). Portée V1 (voir tête de classe) :
        //   - Zone neutre (jamais capturée) -> capture INSTANTANÉE, pas de combat.
        //   - Zone déjà possédée par un autre joueur -> combat contre une garnison IA
        //     (TacticalAIPlanner, même mécanisme que les ennemis du mode Solo), PAS contre le
        //     propriétaire réel en direct : celui-ci n'est pas forcément connecté au moment de
        //     l'attaque, et notifier/faire patienter un joueur en ligne pour qu'il défende en
        //     direct nécessiterait un système de siège asynchrone qui n'existe pas encore
        //     (à ajouter plus tard, voir 08-known-issues-and-todo.md).
        //   - Contrairement à deathmatch/zone_control (qui gardent la carte par défaut fixe,
        //     chargée une fois par SetupWorldOnce), un combat de conquête charge la géométrie
        //     RÉELLE de la Zone attaquée via CityGenerator/MapTileLoader — exactement le même
        //     (tileX, tileY) que celui utilisé côté client, ce qui garantit un NavMesh et des
        //     bâtiments identiques sans jamais transmettre la géométrie sur le réseau.
        // =====================================================================

        private void HandleConquestMessage(PlayerConnection conn, NetMessage msg)
        {
            if (matchInProgress)
            {
                // Un seul combat/emplacement de simulation à la fois sur ce serveur (voir "Portée
                // V1" en tête de classe) — le client peut retenter une nouvelle demande plus tard.
                conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_busy", zone_tile_x = msg.zone_tile_x, zone_tile_y = msg.zone_tile_y });
                conn.Close();
                return;
            }

            matchInProgress = true;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunConquestRequestGuarded(conn, msg.zone_tile_x, msg.zone_tile_y));
        }

        /// <summary>Même principe que RunMatchGuarded : une exception non prévue ne doit jamais
        /// laisser matchInProgress bloqué à "true" pour toujours.</summary>
        private IEnumerator RunConquestRequestGuarded(PlayerConnection conn, int tileX, int tileY)
        {
            IEnumerator inner = RunConquestRequest(conn, tileX, tileY);
            while (true)
            {
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant une conquête — abandon : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    try { if (!conn.IsDisconnected) conn.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_error", zone_tile_x = tileX, zone_tile_y = tileY }); } catch { }
                    try { conn.Close(); } catch { }
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }
                yield return inner.Current;
            }
        }

        /// <summary>Vrai si (bx,by) est adjacente à (ax,ay) au sens des 4 directions cardinales
        /// (Nord/Sud/Est/Ouest, jamais diagonale — voir ZoneManager.AttackNorth/South/East/West).</summary>
        private static bool IsAdjacentTile(int ax, int ay, int bx, int by)
        {
            return (ax == bx && Mathf.Abs(ay - by) == 1) || (ay == by && Mathf.Abs(ax - bx) == 1);
        }

        private IEnumerator RunConquestRequest(PlayerConnection attacker, int tileX, int tileY)
        {
            // Le serveur ne faisait jusqu'ici AUCUNE vérification que (tileX,tileY) est réellement
            // adjacente au territoire de l'attaquant — un client modifié pouvait attaquer/capturer
            // n'importe quelle Zone du monde instantanément (voir rapport d'audit §1.2). On exige
            // désormais l'adjacence à une Zone déjà possédée, SAUF pour la toute première Zone d'un
            // joueur qui n'en possède encore aucune (bootstrap : il n'a par définition aucun
            // territoire dont être adjacent).
            List<(int x, int y)> ownedByAttacker = null;
            yield return FetchOwnedZones(attacker.UserId, list => ownedByAttacker = list);

            if (ownedByAttacker.Count > 0 && !ownedByAttacker.Exists(z => IsAdjacentTile(z.x, z.y, tileX, tileY)))
            {
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "not_adjacent", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            string ownerId = null;
            yield return FetchZoneOwner(tileX, tileY, id => ownerId = id);

            if (!string.IsNullOrEmpty(ownerId) && ownerId == attacker.UserId)
            {
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "already_owned", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                bool captured = false;
                // expectedPriorOwner=null : INSERT strict (pas d'upsert) — si un autre joueur/une
                // autre instance du pool a capturé cette même Zone neutre entre notre lecture
                // ci-dessus et cette écriture, la contrainte de clé primaire fait échouer l'insertion
                // au lieu d'écraser silencieusement son résultat (voir rapport d'audit §1.1).
                yield return CaptureZoneInDb(tileX, tileY, attacker.UserId, null, ok => captured = ok);
                if (captured)
                {
                    // Capture d'une Zone neutre = victoire de conquête au même titre qu'un combat
                    // gagné contre une garnison : doit rapporter le même delta de classement, sinon
                    // le geste le plus fréquent du mode Conquête (capturer du neutre) reste sans
                    // aucune récompense visible (voir rapport d'audit jouabilité, défaut bloquant #3).
                    yield return ApplyConquestRatingDelta(attacker, captured: true, won: true);
                    attacker.Send(new NetMessage { type = "zone_captured", success = true, zone_tile_x = tileX, zone_tile_y = tileY, your_new_rating = attacker.NewRating, rating_delta = attacker.RatingDelta });
                }
                else
                    attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "zone_taken", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            yield return RunConquestSkirmish(attacker, tileX, tileY, ownerId);
        }

        private IEnumerator RunConquestSkirmish(PlayerConnection attacker, int tileX, int tileY, string defenderOwnerId)
        {
            attacker.TeamId = 1;
            string matchId = Guid.NewGuid().ToString();

            yield return FetchUsername(attacker);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                attacker.Send(new NetMessage { type = "zone_attack_result", success = false, reason = "server_error", zone_tile_x = tileX, zone_tile_y = tileY });
                attacker.Close();
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            yield return LoadZoneOnServer(tileX, tileY);

            attacker.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = "Garnison ennemie", mode = "conquest", zone_tile_x = tileX, zone_tile_y = tileY });

            yield return CreateMatchRecord(matchId, attacker, null, "conquest");

            yield return RunConquestDeploymentPhase(attacker, defenderOwnerId);

            // Le camp attaquant est piloté par un vrai joueur. La garnison IA (équipe 2) GARDE
            // isPlayerControlled à sa valeur par défaut (false) : TacticalAIPlanner.PlanTurnForUnit()
            // planifie donc pour elle exactement comme pour un ennemi en mode Solo (voir
            // RunConquestPlanningPhase et UnitAI_Movement.PlanifierTourIA).
            foreach (var unit in UnitAI.AllLivingUnits.Where(u => u.teamID == 1)) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunConquestPlanningPhase(attacker, turnNumber);

                if (attacker.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhase(turnNumber, attacker, null);

                int attackerAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
                int garrisonAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);

                if (attackerAlive == 0 || garrisonAlive == 0)
                {
                    matchOver = true;
                    winnerTeam = (attackerAlive == 0 && garrisonAlive == 0) ? 0 : (attackerAlive == 0 ? 2 : 1);
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    matchOver = true;
                    int attackerHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                    int garrisonHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
                    if (attackerAlive != garrisonAlive) winnerTeam = attackerAlive > garrisonAlive ? 1 : 2;
                    else if (attackerHealth != garrisonHealth) winnerTeam = attackerHealth > garrisonHealth ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            bool won = winnerTeam == 1;
            bool captureConfirmed = false;
            if (won)
            {
                // expectedPriorOwner=defenderOwnerId : si la Zone a changé de propriétaire entre le
                // début de ce combat et maintenant (un autre combat contre la même garnison, lancé en
                // parallèle sur une autre instance du pool, a capturé la Zone en premier — voir
                // rapport d'audit §1.1), cette écriture conditionnelle échoue proprement au lieu
                // d'écraser silencieusement le résultat de l'autre combat.
                yield return CaptureZoneInDb(tileX, tileY, attacker.UserId, defenderOwnerId, ok => captureConfirmed = ok);
            }
            bool captured = won && captureConfirmed;

            yield return ApplyConquestRatingDelta(attacker, captured, won);

            if (!attacker.IsDisconnected)
            {
                attacker.Send(new NetMessage
                {
                    type = "match_over",
                    winner_team = winnerTeam,
                    zone_tile_x = tileX,
                    zone_tile_y = tileY,
                    success = captured,
                    // "zone_lost_race" : le combat a été GAGNÉ (winnerTeam==1) mais un autre
                    // attaquant a capturé cette Zone entre-temps — distinct d'une simple défaite
                    // contre la garnison, le client doit afficher un message différent.
                    reason = (won && !captureConfirmed) ? "zone_lost_race" : "",
                    your_new_rating = attacker.NewRating,
                    rating_delta = attacker.RatingDelta
                });
            }

            yield return CloseMatchRecord(matchId, winnerTeam);
            attacker.Close();
        }

        // Renfort de garnison IA en fonction de la taille du territoire du défenseur (NOUVEAU,
        // 2026-08-30 — voir rapport d'audit §2.B "rich get richer sans mécanique de retour") :
        // jusqu'ici la garnison défendant une Zone était TOUJOURS la même composition fixe, quelle
        // que soit la valeur stratégique de la Zone ou la taille de l'empire du propriétaire — un
        // joueur assidu pouvait donc étendre son territoire indéfiniment sans jamais rencontrer de
        // résistance croissante. Un empire plus grand devient maintenant mécaniquement plus difficile
        // à continuer d'agrandir, sans avoir besoin d'un système d'économie/ressources complet.
        private static int GarrisonExtraInfantryForZoneCount(int zoneCount)
        {
            if (zoneCount >= 10) return 3;
            if (zoneCount >= 6) return 2;
            if (zoneCount >= 3) return 1;
            return 0;
        }

        /// <summary>Comme RunDeploymentPhase, mais un seul vrai joueur (l'attaquant, équipe 1) : la
        /// garnison IA (équipe 2) est auto-déployée immédiatement, sans attendre — elle n'a pas de
        /// timer de joueur à respecter. Sa force est renforcée selon le nombre de Zones déjà
        /// possédées par le défenseur (voir GarrisonExtraInfantryForZoneCount).</summary>
        private IEnumerator RunConquestDeploymentPhase(PlayerConnection attacker, string defenderOwnerId)
        {
            attacker.HasSubmittedDeployment = false;
            attacker.PendingDeployment = null;
            attacker.MapReady = false;

            DateTime conquestDeployStartUtc = DateTime.UtcNow;

            // Même filet que RunDeploymentPhase : LoadZoneOnServer a déjà attendu la génération
            // CÔTÉ SERVEUR, mais le client attaquant doit aussi charger cette même Zone de son côté
            // (LoadConquestZoneThenOpenDeployment) avant de voir son dock — sans attendre son
            // "deployment_ready", le timer de 45s pouvait s'écouler entièrement pendant ce chargement.
            float mapWait = MapReadyMaxWaitSeconds;
            while (mapWait > 0f && !attacker.MapReady)
            {
                DrainMessages(attacker, 0);
                if (attacker.IsDisconnected) yield break;
                mapWait -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête — MapReady terminé après {(DateTime.UtcNow - conquestDeployStartUtc).TotalSeconds:F1}s réelles.");

            DateTime conquestCountdownStartUtc = DateTime.UtcNow;
            float remaining = DeploymentSeconds;
            while (remaining > 0f && !attacker.HasSubmittedDeployment)
            {
                DrainMessages(attacker, 0);
                if (attacker.IsDisconnected) yield break;
                remaining -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête — compte à rebours de déploiement terminé après {(DateTime.UtcNow - conquestCountdownStartUtc).TotalSeconds:F1}s réelles (attendu {DeploymentSeconds}s), HasSubmittedDeployment={attacker.HasSubmittedDeployment}.");

            var attackerUnits = ResolveDeployment(attacker, 1);

            int extraGarrisonInfantry = 0;
            if (!string.IsNullOrEmpty(defenderOwnerId))
            {
                List<(int x, int y)> defenderZones = null;
                yield return FetchOwnedZones(defenderOwnerId, list => defenderZones = list);
                extraGarrisonInfantry = GarrisonExtraInfantryForZoneCount(defenderZones.Count);
            }
            UnitSpawnerUI.Instance.AutoDeployTeamFallback(2, extraGarrisonInfantry);

            // Brouillard de guerre au déploiement (voir RunDeploymentPhase) : l'attaquant ne voit que
            // SA PROPRE escouade avant le premier tour — la garnison IA n'existera côté client qu'une
            // fois repérée en jeu (voir PlaySnapshotsCoroutine).
            if (!attacker.IsDisconnected)
                attacker.Send(new NetMessage { type = "deployment_result", deployed_units = attackerUnits.ToArray() });
        }

        /// <summary>Comme RunPlanningPhase, mais la garnison IA planifie immédiatement (pas
        /// d'attente : elle n'a pas de timer de joueur), exactement comme un ennemi en mode Solo.</summary>
        private IEnumerator RunConquestPlanningPhase(PlayerConnection attacker, int turnNumber)
        {
            attacker.HasSubmittedThisTurn = false;

            foreach (var u in UnitAI.AllLivingUnits.Where(u => u.teamID == 2 && !u.isDead))
            {
                u.PlanifierTourIA();
            }

            DateTime conquestPlanningStartUtc = DateTime.UtcNow;
            float remaining = PlanningSeconds;
            int lastTick = -1;

            while (remaining > 0f && !attacker.HasSubmittedThisTurn)
            {
                DrainMessages(attacker, turnNumber);
                if (attacker.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    attacker.Send(new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft });
                }

                remaining -= Time.deltaTime;
                yield return null;
            }
            Debug.Log($"[Timing] Conquête tour {turnNumber} — RunConquestPlanningPhase terminé après {(DateTime.UtcNow - conquestPlanningStartUtc).TotalSeconds:F1}s réelles (attendu max {PlanningSeconds}s), HasSubmittedThisTurn={attacker.HasSubmittedThisTurn}.");

            if (attacker.HasSubmittedThisTurn)
            {
                ApplyOrdersToUnits(UnitAI.AllLivingUnits.Where(u => u.teamID == 1).ToList(), attacker.PendingOrders);
            }
            else
            {
                foreach (var u in UnitAI.AllLivingUnits.Where(u => u.teamID == 1)) u.ClearTacticalPath();
            }
        }

        // =====================================================================
        // Entraînement contre l'IA (2026-09-02) — permet à un joueur SEUL en file d'attente
        // Deathmatch/Zone de Contrôle de jouer une partie immédiate contre une garnison IA sur la
        // carte par défaut, en attendant qu'un vrai adversaire se présente, plutôt que de patienter
        // les bras croisés. Réutilise TEL QUEL le moteur "vivant" 1-joueur-contre-garnison déjà
        // éprouvé par la Conquête (RunConquestDeploymentPhase/RunConquestPlanningPhase/
        // RunExecutionPhase, TacticalAIPlanner) — jamais le moteur "Option B" en donnée pure
        // (RunMatch), qui ne sait pas piloter d'IA. Contrainte héritée de la Conquête, inchangée :
        // un seul combat "vivant" (Conquête OU Entraînement) à la fois par instance de serveur (voir
        // matchInProgress) — un joueur qui déclenche l'entraînement pendant qu'un combat vivant
        // tourne déjà reçoit un refus immédiat plutôt que d'attendre indéfiniment ; le client peut
        // simplement retenter. Partie hors-score, sans effet sur le classement ni sur la table
        // "zones" : uniquement pour s'entraîner/patienter.
        // =====================================================================

        private void HandlePracticeAiMessage(PlayerConnection conn)
        {
            if (matchInProgress)
            {
                conn.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_busy" });
                conn.Close();
                return;
            }

            matchInProgress = true;
            StartCoroutine(ReportInstanceStatus());
            StartCoroutine(RunPracticeVsAIGuarded(conn));
        }

        /// <summary>Même principe que RunMatchGuarded/RunConquestRequestGuarded : une exception non
        /// prévue ne doit jamais laisser matchInProgress bloqué à "true" pour toujours.</summary>
        private IEnumerator RunPracticeVsAIGuarded(PlayerConnection player)
        {
            IEnumerator inner = RunPracticeVsAI(player);
            while (true)
            {
                bool moved = false;
                bool crashed = false;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MatchSessionManager] Exception non gérée pendant un entraînement IA — abandon : {e}");
                    crashed = true;
                }

                if (crashed)
                {
                    try { if (!player.IsDisconnected) player.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_error" }); } catch { }
                    try { player.Close(); } catch { }
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }

                if (!moved)
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                    yield break;
                }
                yield return inner.Current;
            }
        }

        private IEnumerator RunPracticeVsAI(PlayerConnection player)
        {
            player.TeamId = 1;
            string matchId = Guid.NewGuid().ToString();

            yield return FetchUsername(player);

            if (UnitSpawnerUI.Instance == null)
            {
                Debug.LogError("[MatchSessionManager] UnitSpawnerUI.Instance introuvable — la scène serveur est-elle correctement chargée ?");
                player.Send(new NetMessage { type = "match_over", winner_team = 0, reason = "server_error" });
                player.Close();
                yield break;
            }

            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            // Toujours la carte par défaut (jamais une vraie Zone/tuile GPS) — un entraînement n'a
            // pas de territoire réel à charger, contrairement à la Conquête (LoadZoneOnServer).
            yield return RestoreDefaultMapOnServer();

            player.Send(new NetMessage { type = "match_found", match_id = matchId, team_id = 1, opponent_username = "IA (Entraînement)", mode = "practice_ai" });

            yield return CreateMatchRecord(matchId, player, null, "practice_ai");

            // defenderOwnerId=null : pas de renfort de garnison lié à un territoire (voir
            // GarrisonExtraInfantryForZoneCount) — l'entraînement n'a pas de notion de "propriétaire
            // de Zone", la garnison reste toujours à sa force de base.
            yield return RunConquestDeploymentPhase(player, null);

            foreach (var unit in UnitAI.AllLivingUnits.Where(u => u.teamID == 1)) unit.isPlayerControlled = true;

            int turnNumber = 1;
            bool matchOver = false;
            int winnerTeam = 0;

            while (!matchOver)
            {
                yield return RunConquestPlanningPhase(player, turnNumber);

                if (player.IsDisconnected)
                {
                    matchOver = true;
                    winnerTeam = 0;
                    break;
                }

                yield return RunExecutionPhase(turnNumber, player, null);

                int playerAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
                int garrisonAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);

                if (playerAlive == 0 || garrisonAlive == 0)
                {
                    matchOver = true;
                    winnerTeam = (playerAlive == 0 && garrisonAlive == 0) ? 0 : (playerAlive == 0 ? 2 : 1);
                }
                else if (turnNumber >= DeathmatchTurnCap)
                {
                    matchOver = true;
                    int playerHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                    int garrisonHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
                    if (playerAlive != garrisonAlive) winnerTeam = playerAlive > garrisonAlive ? 1 : 2;
                    else if (playerHealth != garrisonHealth) winnerTeam = playerHealth > garrisonHealth ? 1 : 2;
                    else winnerTeam = 0;
                }

                turnNumber++;
            }

            if (!player.IsDisconnected)
            {
                player.Send(new NetMessage { type = "match_over", winner_team = winnerTeam, reason = "practice" });
            }

            yield return CloseMatchRecord(matchId, winnerTeam);
            player.Close();
        }

        /// <summary>Charge la géométrie réelle (bâtiments Overpass + sol OSM + NavMesh) d'une Zone
        /// de Conquête sur le serveur, en attendant qu'elle soit ENTIÈREMENT prête (voir
        /// CityGenerator.IsCityReady) avant de continuer — sans ça, le déploiement pourrait démarrer
        /// sur un NavMesh pas encore baké.</summary>
        private IEnumerator LoadZoneOnServer(int tileX, int tileY)
        {
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            if (cityGen == null)
            {
                Debug.LogError("[MatchSessionManager] CityGenerator introuvable — impossible de charger la Zone de Conquête.");
                yield break;
            }

            defaultMapLoaded = false;
            cityGen.zoneTileX = tileX;
            cityGen.zoneTileY = tileY;
            cityGen.GenerateCity();
            if (mapLoader != null) mapLoader.LoadMap();
            TacticalGridBuilder.InvalidateCache(); // nouvelle Zone = bâtiments totalement différents

            float maxWait = 60f; // Filet de sécurité : Overpass peut être lent, mais jamais infini.
            while (!cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            if (!cityGen.IsCityReady)
            {
                Debug.LogWarning($"[MatchSessionManager] Chargement de la Zone ({tileX},{tileY}) trop long (>60s) — poursuite avec l'état actuel.");
            }
        }

        /// <summary>Recharge la carte par défaut hors-ligne (deathmatch/zone_control) après qu'un
        /// combat de conquête a temporairement remplacé la géométrie de la scène — voir
        /// defaultMapLoaded. Mêmes appels que SetupWorldOnce(), avec une attente de complétion en plus.</summary>
        private IEnumerator RestoreDefaultMapOnServer()
        {
            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (mapLoader != null) mapLoader.ApplyDefaultOfflineMap();
            if (cityGen != null) cityGen.LoadDefaultOfflineCity();
            TacticalGridBuilder.InvalidateCache(); // retour à la carte par défaut = bâtiments différents de la Zone quittée

            float maxWait = 30f;
            while (cityGen != null && !cityGen.IsCityReady && maxWait > 0f)
            {
                maxWait -= Time.deltaTime;
                yield return null;
            }

            defaultMapLoaded = true;
        }

        // =====================================================================
        // Persistance des Zones (table "zones", schema.sql) via PostgREST.
        // =====================================================================

        private IEnumerator FetchZoneOwner(int tileX, int tileY, Action<string> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{CityGenerator.ZONE_ZOOM}&select=owner_user_id";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture de la Zone ({tileX},{tileY}) échouée : {req.error}");
                onResult(null);
                yield break;
            }

            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<ZoneQueryResult>(wrapped);
                string owner = (parsed?.items != null && parsed.items.Length > 0) ? parsed.items[0].owner_user_id : null;
                onResult(string.IsNullOrEmpty(owner) ? null : owner);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchSessionManager] Parsing Zone échoué : {ex.Message}");
                onResult(null);
            }
        }

        /// <summary>Capture ATOMIQUE d'une Zone — corrige la course entre deux instances du pool qui
        /// combattraient pour la même Zone en même temps (voir rapport d'audit §1.1) : l'ancien
        /// upsert "merge-duplicates" écrasait silencieusement le résultat de l'autre combat, quel que
        /// soit l'état réel de la Zone au moment de l'écriture. <paramref name="expectedPriorOwner"/>
        /// doit être EXACTEMENT ce qui a été lu par FetchZoneOwner avant de lancer le combat/la
        /// capture : null pour une Zone neutre (-> INSERT strict, échoue si quelqu'un a capturé entre
        /// temps grâce à la contrainte de clé primaire), l'ID du propriétaire vu à ce moment-là pour
        /// une Zone contestée (-> PATCH conditionnel sur ce même owner_user_id, n'affecte aucune ligne
        /// si la Zone a changé de mains depuis). <paramref name="onResult"/> reçoit true seulement si
        /// CETTE écriture a réellement pris effet.</summary>
        private IEnumerator CaptureZoneInDb(int tileX, int tileY, string newOwnerUserId, string expectedPriorOwner, Action<bool> onResult)
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            if (expectedPriorOwner == null)
            {
                string json = "{\"tile_x\":" + tileX + ",\"tile_y\":" + tileY + ",\"zoom\":" + CityGenerator.ZONE_ZOOM +
                              ",\"owner_user_id\":\"" + newOwnerUserId + "\",\"captured_at\":\"" + nowIso + "\"}";
                yield return PostgrestPostChecked("/zones", json, onResult);
            }
            else
            {
                string path = $"/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{CityGenerator.ZONE_ZOOM}&owner_user_id=eq.{expectedPriorOwner}";
                string json = "{\"owner_user_id\":\"" + newOwnerUserId + "\",\"captured_at\":\"" + nowIso + "\"}";
                yield return PostgrestPatchChecked(path, json, onResult);
            }
        }

        /// <summary>INSERT strict (jamais d'upsert) — un conflit de clé primaire (409, quelqu'un a
        /// inséré la même ligne entre temps) est une issue ATTENDUE ici, pas une vraie erreur : voir
        /// CaptureZoneInDb. onResult ne reçoit true que sur un succès HTTP franc.</summary>
        private IEnumerator PostgrestPostChecked(string path, string jsonBody, Action<bool> onResult)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=minimal");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogWarning($"[MatchSessionManager] Capture de Zone refusée (déjà prise entre-temps ?) ({path}) : {req.error} — {req.downloadHandler.text}");
            onResult(req.result == UnityWebRequest.Result.Success);
        }

        /// <summary>PATCH conditionné par le filtre de la requête (owner_user_id=eq.&lt;attendu&gt;
        /// dans <paramref name="path"/>) — "Prefer: return=representation" permet de distinguer "0
        /// ligne affectée" (corps "[]", la Zone a changé de propriétaire depuis notre lecture) d'une
        /// vraie mise à jour, ce qu'un simple code HTTP 200/204 ne permettrait pas ici : PostgREST
        /// répond 200 avec un tableau vide dans les deux cas (échec conditionnel silencieux ou succès)
        /// sans ce header. Voir CaptureZoneInDb.</summary>
        private IEnumerator PostgrestPatchChecked(string path, string jsonBody, Action<bool> onResult)
        {
            using var req = new UnityWebRequest(GameServerBootstrap.RestUrl + path, "PATCH");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Prefer", "return=representation");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MatchSessionManager] Mise à jour conditionnelle de Zone échouée ({path}) : {req.error} — {req.downloadHandler.text}");
                onResult(false);
                yield break;
            }
            string body = req.downloadHandler.text?.Trim();
            onResult(!string.IsNullOrEmpty(body) && body != "[]");
        }

        /// <summary>Toutes les Zones actuellement possédées par <paramref name="userId"/> — sert à la
        /// fois à valider l'adjacence d'une nouvelle attaque (RunConquestRequest) et à renforcer la
        /// garnison d'un défenseur selon la taille de son territoire (RunConquestDeploymentPhase). En
        /// cas d'échec réseau, retourne une liste VIDE (pas null) : voir les appelants, qui traitent
        /// alors le joueur comme n'ayant aucune Zone (comportement "fail-open" déjà utilisé ailleurs
        /// dans cette classe, ex. FetchZoneOwner traite un échec comme "Zone neutre").</summary>
        private IEnumerator FetchOwnedZones(string userId, Action<List<(int x, int y)>> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?owner_user_id=eq.{userId}&select=tile_x,tile_y";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            var result = new List<(int x, int y)>();
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<ZoneCoordQueryResult>(wrapped);
                    if (parsed?.items != null)
                        foreach (var e in parsed.items) result.Add((e.tile_x, e.tile_y));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MatchSessionManager] Parsing des Zones possédées échoué : {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning($"[MatchSessionManager] Lecture des Zones possédées échouée : {req.error}");
            }
            onResult(result);
        }

        [Serializable] private class ZoneEntry { public string owner_user_id; }
        [Serializable] private class ZoneQueryResult { public ZoneEntry[] items; }
        [Serializable] private class ZoneCoordEntry { public int tile_x; public int tile_y; }
        [Serializable] private class ZoneCoordQueryResult { public ZoneCoordEntry[] items; }
    }
}
