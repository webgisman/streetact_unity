using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// SEUL pont entre la scène Unity vivante (BuildingStructure, DestructibleEnvironment,
    /// RoadBarrier, UnitAI) et le TacticalWorldState pur — extrait les données une fois, puis
    /// TacticalCore n'a plus jamais besoin de retoucher un composant de scène. Construit à partir
    /// de ce que CityGenerator a déjà généré, pas d'une nouvelle géométrie.
    /// </summary>
    public static class TacticalGridBuilder
    {
        // Cache de géométrie STATIQUE (2026-08-30, test grandeur nature — mesuré ~2050ms réels par
        // tour, sur TOUS les tours, pour reconstruire 1538 segments de mur + la grille de marche à
        // partir de 236 bâtiments, alors que TacticalResolver.Resolve() lui-même ne prend que
        // 10-70ms). Les murs et bâtiments ne BOUGENT jamais après génération de la ville — seul leur
        // état "détruit" change, ce qui est déjà lu EN DIRECT par WallSegment.BlocksSight(state) (voir
        // TacticalTypes.cs, jamais une valeur baked dans le segment) : les segments de mur et la
        // grille de marche peuvent donc être calculés UNE FOIS par ville chargée et réutilisés à
        // chaque tour tant qu'aucun bâtiment n'a réellement basculé "détruit" depuis le dernier
        // calcul — seule la grille (qui décide si l'intérieur d'un bâtiment est praticable) a besoin
        // d'être reconstruite quand ça arrive, les segments de mur restent valables indéfiniment.
        private static List<WallSegment> cachedWallSegments;
        private static List<TacticalBuilding> cachedBuildingTemplates; // footprint/id seulement, health/destroyed rafraîchis à chaque appel
        private static TacticalGrid cachedGrid;
        private static int cachedDestroyedCount = -1;
        private static int cachedBuildingCount = -1;
        private static string cachedCacheKey; // "Z17_{tileX}_{tileY}" ou "Default" — voir CityGenerator.CurrentGridCacheKey

        /// <summary>À appeler chaque fois que la géométrie de la scène change réellement (nouvelle
        /// ville générée, nouvelle Zone chargée) — sans ça, un cache construit pour l'ancienne carte
        /// resterait utilisé par erreur pour la nouvelle. Voir MatchSessionManager (SetupWorldOnce,
        /// RestoreDefaultMapOnServer, LoadZoneOnServer) et CityGenerator.CancelActiveGenerationAndClearCity
        /// (invalide aussi côté client). N'efface QUE le cache mémoire (L1) — le cache disque (L2, voir
        /// plus bas) n'a pas besoin d'être invalidé : il ne stocke que le gabarit géométrique immuable
        /// d'une carte/tuile donnée, jamais un état "détruit" qui pourrait devenir périmé.</summary>
        public static void InvalidateCache()
        {
            cachedWallSegments = null;
            cachedBuildingTemplates = null;
            cachedGrid = null;
            cachedDestroyedCount = -1;
            cachedBuildingCount = -1;
            cachedCacheKey = null;
        }

        /// <summary>Construit un TacticalWorldState complet (bâtiments + murs + barricades +
        /// grille) à partir de l'état ACTUEL de la scène — à appeler une fois au début de la phase
        /// d'exécution d'un tour, après tout placement/déploiement. Réutilise le cache de géométrie
        /// statique EN MÉMOIRE (L1, voir ci-dessus) tant qu'aucun bâtiment supplémentaire n'est passé
        /// à l'état détruit depuis le dernier appel ; à défaut, tente le cache SUR DISQUE (L2, voir
        /// TryLoadFromDisk) — partagé entre processus et survit à un redémarrage, contrairement à L1
        /// qui est un champ statique propre à CE process. Seule la reconstruction complète depuis la
        /// scène Unity (ni L1 ni L2 disponibles) coûte ~2000ms ; les PV/état "détruit" de chaque
        /// bâtiment et les barricades sont TOUJOURS relus frais depuis la scène, jamais mis en cache.
        /// <paramref name="cacheKey"/> identifie la carte/tuile actuelle ("Z17_{tileX}_{tileY}" ou
        /// "Default", voir CityGenerator.CurrentGridCacheKey) — si omis, déduit automatiquement du
        /// CityGenerator de la scène courante (fonctionne aussi bien côté serveur que côté client,
        /// chacun ayant sa propre instance).</summary>
        public static TacticalWorldState BuildFromScene(float radius = 120f, string cacheKey = null)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                CityGenerator activeCity = UnityEngine.Object.FindAnyObjectByType<CityGenerator>();
                cacheKey = activeCity != null ? activeCity.CurrentGridCacheKey : "Default";
            }

            int destroyedCount = 0;
            for (int i = 0; i < BuildingStructure.AllBuildings.Count; i++)
            {
                var env = BuildingStructure.AllBuildings[i]?.GetComponent<DestructibleEnvironment>();
                if (env != null && env.isDestroyed) destroyedCount++;
            }

            bool l1Valid = cachedWallSegments != null
                && cachedCacheKey == cacheKey
                && cachedBuildingCount == BuildingStructure.AllBuildings.Count
                && cachedDestroyedCount == destroyedCount;

            var state = new TacticalWorldState();

            if (l1Valid)
            {
                // Géométrie inchangée depuis le dernier tour (aucun bâtiment fraîchement détruit) :
                // réutilise les segments de mur ET la grille tels quels, ne reconstruit que la liste
                // de bâtiments (santé/destroyed frais depuis la scène — cheap, 236 lectures simples).
                state.wallSegments = cachedWallSegments;
                state.grid = cachedGrid;
                for (int b = 0; b < cachedBuildingTemplates.Count; b++)
                {
                    var template = cachedBuildingTemplates[b];
                    BuildingStructure building = template.id < BuildingStructure.AllBuildings.Count ? BuildingStructure.AllBuildings[template.id] : null;
                    DestructibleEnvironment env = building?.GetComponent<DestructibleEnvironment>();
                    state.buildings.Add(new TacticalBuilding
                    {
                        id = template.id,
                        footprint = template.footprint,
                        health = env != null ? env.health : 300f,
                        destroyed = env != null && env.isDestroyed,
                        doors = template.doors,
                        windows = template.windows,
                        height = template.height
                    });
                }
                return AttachBarricades(state);
            }

            // L2 (disque) : seulement valable si AUCUN bâtiment n'est encore détruit — le cache
            // disque ne stocke que le gabarit géométrique "ville neuve", jamais un état endommagé
            // (un match précédent ayant déjà abîmé cette même carte partagée doit repasser par une
            // reconstruction complète pour refléter les vrais trous dans les murs).
            if (destroyedCount == 0
                && TryLoadFromDisk(cacheKey, out List<WallSegment> diskWalls, out List<TacticalBuilding> diskTemplates, out TacticalGrid diskGrid)
                && diskTemplates.Count == BuildingStructure.AllBuildings.Count)
            {
                state.wallSegments = diskWalls;
                state.grid = diskGrid;
                for (int b = 0; b < diskTemplates.Count; b++)
                {
                    var template = diskTemplates[b];
                    BuildingStructure building = template.id < BuildingStructure.AllBuildings.Count ? BuildingStructure.AllBuildings[template.id] : null;
                    DestructibleEnvironment env = building?.GetComponent<DestructibleEnvironment>();
                    state.buildings.Add(new TacticalBuilding
                    {
                        id = template.id,
                        footprint = template.footprint,
                        health = env != null ? env.health : 300f,
                        destroyed = env != null && env.isDestroyed,
                        doors = template.doors,
                        windows = template.windows,
                        height = template.height
                    });
                }

                cachedWallSegments = diskWalls;
                cachedBuildingTemplates = diskTemplates;
                cachedGrid = diskGrid;
                cachedDestroyedCount = 0;
                cachedBuildingCount = BuildingStructure.AllBuildings.Count;
                cachedCacheKey = cacheKey;
                return AttachBarricades(state);
            }

            // Reconstruction complète depuis la scène Unity (premier accès JAMAIS vu à cette carte
            // sur AUCUN processus, ou un bâtiment vient tout juste de basculer "détruit" ce tour-ci)
            // — même logique qu'avant, mais alimente maintenant L1 ET L2 pour la suite.
            var walkableFootprints = new List<List<Vector2>>(); // seulement les bâtiments PAS déjà détruits
            var freshWallSegments = new List<WallSegment>();
            var freshTemplates = new List<TacticalBuilding>();

            for (int b = 0; b < BuildingStructure.AllBuildings.Count; b++)
            {
                BuildingStructure building = BuildingStructure.AllBuildings[b];
                if (building == null || building.polygonFootprint == null || building.polygonFootprint.Count < 3) continue;

                DestructibleEnvironment env = building.GetComponent<DestructibleEnvironment>();
                List<TacticalDoor> doors = ExtractDoors(building);
                List<TacticalWindow> windows = ExtractWindows(building);
                var tacticalBuilding = new TacticalBuilding
                {
                    id = b,
                    footprint = building.polygonFootprint,
                    health = env != null ? env.health : 300f,
                    destroyed = env != null && env.isDestroyed,
                    doors = doors,
                    windows = windows,
                    height = building.height
                };
                state.buildings.Add(tacticalBuilding);
                freshTemplates.Add(new TacticalBuilding { id = b, footprint = building.polygonFootprint, doors = doors, windows = windows, height = building.height });

                if (!tacticalBuilding.destroyed) walkableFootprints.Add(building.polygonFootprint);
                AddWallSegmentsForBuilding(freshWallSegments, building, b);
            }

            state.wallSegments = freshWallSegments;
            state.grid = BuildGrid(walkableFootprints, radius);

            cachedWallSegments = freshWallSegments;
            cachedBuildingTemplates = freshTemplates;
            cachedGrid = state.grid;
            cachedDestroyedCount = destroyedCount;
            cachedBuildingCount = BuildingStructure.AllBuildings.Count;
            cachedCacheKey = cacheKey;

            // N'écrit le cache disque que pour une ville neuve (aucun bâtiment détruit) : comme pour
            // la lecture L2 ci-dessus, on ne veut jamais persister un gabarit qui reflète des dégâts
            // d'un match précédent partageant la même carte.
            if (destroyedCount == 0) SaveToDisk(cacheKey, freshWallSegments, freshTemplates, state.grid);

            return AttachBarricades(state);
        }

        private static TacticalWorldState AttachBarricades(TacticalWorldState state)
        {
            foreach (RoadBarrier barrier in RoadBarrier.AllBarriers)
            {
                if (barrier == null || barrier.health <= 0f) continue;
                AddBarricadeSegment(state, barrier);
            }
            return state;
        }

        /// <summary>Un segment de mur par arête du polygone du bâtiment — une arête portant une
        /// porte reçoit un WallSegment marqué isDoorGap plutôt que d'être découpée en deux (le vrai
        /// découpage géométrique existe déjà côté rendu, voir CityGenerator.CreateWallsMesh ; ici on
        /// ne modélise que le comportement logique : cette arête ne bloque pas). L'état "détruit"
        /// n'est jamais stocké sur le segment lui-même — voir WallSegment.Destroyed(state), qui lit
        /// le TacticalBuilding parent (pool de PV partagé, pas de destruction mur par mur).</summary>
        private static void AddWallSegmentsForBuilding(List<WallSegment> wallSegments, BuildingStructure building, int buildingId)
        {
            var footprint = building.polygonFootprint;
            int n = footprint.Count;

            bool[] hasDoor = new bool[n];
            if (building.doors != null)
            {
                foreach (var door in building.doors)
                {
                    if (door.edgeIndex >= 0 && door.edgeIndex < n) hasDoor[door.edgeIndex] = true;
                }
            }

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                wallSegments.Add(new WallSegment
                {
                    p1 = footprint[i],
                    p2 = footprint[next],
                    isDoorGap = hasDoor[i],
                    buildingId = buildingId
                });
            }
        }

        /// <summary>Copie purement géométrique de BuildingStructure.doors — voir TacticalDoor
        /// (TacticalTypes.cs) pour le pourquoi (2026-08-30, "des milliers de cartes").</summary>
        private static List<TacticalDoor> ExtractDoors(BuildingStructure building)
        {
            var result = new List<TacticalDoor>();
            if (building.doors == null) return result;
            foreach (var door in building.doors)
            {
                result.Add(new TacticalDoor
                {
                    position = new Vector2(door.position.x, door.position.z),
                    entryDirection = new Vector2(door.entryDirection.x, door.entryDirection.z),
                    width = door.width
                });
            }
            return result;
        }

        /// <summary>Copie purement géométrique de BuildingStructure.windows (MOINS isOccupied/occupant,
        /// état live jamais mis en cache) — voir TacticalWindow (TacticalTypes.cs).</summary>
        private static List<TacticalWindow> ExtractWindows(BuildingStructure building)
        {
            var result = new List<TacticalWindow>();
            if (building.windows == null) return result;
            foreach (var win in building.windows)
            {
                result.Add(new TacticalWindow
                {
                    id = win.id,
                    position = new Vector2(win.position.x, win.position.z),
                    outwardNormal = new Vector2(win.outwardNormal.x, win.outwardNormal.z),
                    floorLevel = win.floorLevel
                });
            }
            return result;
        }

        /// <summary>Vrai si cette tuile/carte a déjà une géométrie en cache (mémoire OU disque) — ne
        /// désérialise rien, juste une existence de fichier pour le cas disque : sert au matchmaking
        /// (voir MatchSessionManager.TryStartMatch, 2026-08-30) pour préférer démarrer une partie sur
        /// une tuile déjà "chaude" plutôt que d'en déclencher une génération neuve coûteuse.</summary>
        public static bool IsTileCached(string cacheKey)
        {
            if (cachedCacheKey == cacheKey && cachedWallSegments != null) return true;
            return File.Exists(DiskCachePath(cacheKey));
        }

        /// <summary>Construit un TacticalWorldState UNIQUEMENT depuis le cache (mémoire L1 puis disque
        /// L2) — ne touche JAMAIS la scène vivante, ne fait AUCUNE hypothèse sur ce qui y est
        /// actuellement chargé. Renvoie null si absent des deux niveaux (voir MatchSessionManager.
        /// RunMatch : dans ce cas, la tuile doit être générée depuis zéro — voir LoadZoneOnServer/
        /// BuildFromScene). Les bâtiments renvoyés sont TOUJOURS à l'état neuf (santé pleine, jamais
        /// détruits) : sans scène vivante, il n'existe aucune notion de dégâts déjà infligés à cette
        /// tuile — cohérent avec le fait qu'une nouvelle partie démarre toujours sur une carte
        /// intacte (2026-08-30, "des milliers de cartes").</summary>
        public static TacticalWorldState BuildFromCacheOnly(string cacheKey)
        {
            List<WallSegment> walls;
            List<TacticalBuilding> templates;
            TacticalGrid grid;

            if (cachedCacheKey == cacheKey && cachedWallSegments != null)
            {
                walls = cachedWallSegments;
                templates = cachedBuildingTemplates;
                grid = cachedGrid;
            }
            else if (!TryLoadFromDisk(cacheKey, out walls, out templates, out grid))
            {
                return null;
            }

            var state = new TacticalWorldState { wallSegments = walls, grid = grid };
            foreach (var template in templates)
            {
                state.buildings.Add(new TacticalBuilding
                {
                    id = template.id,
                    footprint = template.footprint,
                    health = 300f,
                    destroyed = false,
                    doors = template.doors,
                    windows = template.windows,
                    height = template.height
                });
            }
            return state; // pas de barricades : aucune n'a encore été posée sur une partie qui démarre
        }

        private static void AddBarricadeSegment(TacticalWorldState state, RoadBarrier barrier)
        {
            Vector3 pos = barrier.transform.position;
            Vector3 right = barrier.transform.right;
            BoxCollider col = barrier.GetComponent<BoxCollider>();
            float halfWidth = col != null ? (col.size.x * barrier.transform.lossyScale.x) * 0.5f : 2f;

            Vector2 center = new Vector2(pos.x, pos.z);
            Vector2 dir2D = new Vector2(right.x, right.z);
            if (dir2D.sqrMagnitude < 0.0001f) dir2D = Vector2.right; else dir2D.Normalize();

            state.barricades.Add(new Barricade
            {
                p1 = center - dir2D * halfWidth,
                p2 = center + dir2D * halfWidth,
                ownerTeam = barrier.teamID,
                hp = barrier.health // RoadBarrier.cs:17 — PV réels actuels, pas la valeur par défaut
            });
        }

        private static TacticalGrid BuildGrid(List<List<Vector2>> walkableFootprints, float radius)
        {
            float min = -radius, max = radius;
            int cells = Mathf.CeilToInt((max - min) / TacticalGrid.CellSize);
            var grid = new TacticalGrid(min, min, cells, cells);
            grid.CarveBuildingInteriors(walkableFootprints);
            return grid;
        }

        // =====================================================================
        // Cache disque L2 (2026-08-30) — même géométrie que le cache mémoire L1 ci-dessus, mais
        // sérialisée sur disque pour survivre à un redémarrage de processus ET être partagée entre
        // les différentes instances du pool (game-server-1/2/3) qui chargent la même carte/tuile :
        // sans ça, chaque processus payait séparément les ~2000ms de reconstruction au moins une
        // fois, même pour une carte déjà construite par un AUTRE processus juste avant. DTOs dédiés
        // (pas les classes TacticalCore elles-mêmes) car JsonUtility ne sérialise ni Vector2 "nu" en
        // tant que champ de List<T>, ni les tableaux d'enum directement.
        // =====================================================================

        [Serializable] private class Vector2Dto { public float x, y; }
        [Serializable] private class WallSegmentDto { public Vector2Dto p1; public Vector2Dto p2; public bool isDoorGap; public int buildingId; }
        [Serializable] private class DoorDto { public Vector2Dto position; public Vector2Dto entryDirection; public float width; }
        [Serializable] private class WindowDto { public int id; public Vector2Dto position; public Vector2Dto outwardNormal; public int floorLevel; }
        [Serializable] private class BuildingTemplateDto { public int id; public List<Vector2Dto> footprint; public List<DoorDto> doors; public List<WindowDto> windows; public float height; }
        [Serializable] private class GridDto { public float minX, minZ; public int width, height; public bool[] walkable; public int[] elevation; }
        [Serializable] private class GeometryCacheFile { public List<WallSegmentDto> wallSegments; public List<BuildingTemplateDto> buildingTemplates; public GridDto grid; }

        private static string CacheDirectory => Path.Combine(Application.persistentDataPath, "TacticalGridCache");
        // Version du FORMAT/CONTENU du cache disque. À incrémenter dès qu'un changement de génération
        // rend les fichiers déjà écrits incorrects — un ancien fichier n'est alors simplement plus
        // trouvé, donc régénéré, au lieu d'être relu avec des valeurs périmées.
        //   v2 (2026-09-03) : les hauteurs de lot proviennent désormais d'un hachage déterministe de
        //   la géométrie (CityGenerator.DeterministicLotHeight) et non d'un tirage aléatoire non
        //   initialisé. Tout cache antérieur contient des hauteurs de toit que ni le client ni le
        //   serveur ne reproduiraient aujourd'hui.
        //   v3 (2026-09-05) : ce hachage déterministe est passé d'un hash TRIGONOMÉTRIQUE
        //   (Mathf.Sin(x*a+y*b) * grand_facteur puis Mathf.Floor — non garanti bit-identique entre la
        //   libm Android/ARM et la glibc Linux du serveur dédié) à un hash ENTIER pur
        //   (Novgov.Core.DeterministicHash, uniquement XOR/shift/multiplication sur des uint 32 bits).
        //   Exactement la même règle que pour v2 : tout fichier écrit AVANT ce bump contient des
        //   hauteurs calculées avec l'ANCIENNE formule, que le nouveau pipeline client
        //   (CityGenerator.LoadZoneFromServerData, qui rejoue le JSON serveur avec le NOUVEAU hash)
        //   ne reproduirait plus — laisser DiskCacheVersion à 2 aurait fait resservir indéfiniment ces
        //   hauteurs périmées par TacticalGridBuilder pendant que les clients calculent la nouvelle
        //   valeur, recréant exactement le bug "unité perchée qui flotte au-dessus du toit" que le
        //   changement de hash visait à éliminer — mais par la staleness du cache, pas par sin().
        private const int DiskCacheVersion = 3;

        private static string DiskCachePath(string cacheKey) => Path.Combine(CacheDirectory, $"GridCache_v{DiskCacheVersion}_{cacheKey}.json");

        /// <summary>Écrit le gabarit géométrique (murs + grille + bâtiments) sur disque pour cette
        /// clé de carte/tuile — jamais appelée avec un bâtiment déjà détruit (voir BuildFromScene).
        /// Un échec d'écriture (permissions, disque plein) ne doit JAMAIS faire échouer la résolution
        /// d'un tour en cours : capturé et journalisé, rien de plus — le pire cas est de perdre le
        /// gain de vitesse la prochaine fois, jamais de casser une partie.</summary>
        private static void SaveToDisk(string cacheKey, List<WallSegment> wallSegments, List<TacticalBuilding> buildingTemplates, TacticalGrid grid)
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) Directory.CreateDirectory(CacheDirectory);

                // Ménage des versions précédentes (correctif 2026-09-04). Le bump vers
                // GridCache_v2_* laisse les anciens GridCache_<clé>.json / GridCache_v1_* sur disque
                // pour toujours — TryLoadFromDisk ne les trouve plus (nom différent) mais rien ne les
                // supprime. Sans conséquence sur la partie en cours (capturé comme le reste de cette
                // méthode) : au pire, l'espace disque n'est pas repris.
                try
                {
                    string currentPrefix = $"GridCache_v{DiskCacheVersion}_";
                    foreach (string stale in Directory.GetFiles(CacheDirectory, "GridCache_*.json"))
                    {
                        if (!Path.GetFileName(stale).StartsWith(currentPrefix)) File.Delete(stale);
                    }
                }
                catch { /* purge best-effort, jamais bloquante */ }

                var file = new GeometryCacheFile
                {
                    wallSegments = wallSegments.Select(w => new WallSegmentDto
                    {
                        p1 = new Vector2Dto { x = w.p1.x, y = w.p1.y },
                        p2 = new Vector2Dto { x = w.p2.x, y = w.p2.y },
                        isDoorGap = w.isDoorGap,
                        buildingId = w.buildingId
                    }).ToList(),
                    buildingTemplates = buildingTemplates.Select(t => new BuildingTemplateDto
                    {
                        id = t.id,
                        footprint = t.footprint.Select(p => new Vector2Dto { x = p.x, y = p.y }).ToList(),
                        doors = t.doors.Select(d => new DoorDto
                        {
                            position = new Vector2Dto { x = d.position.x, y = d.position.y },
                            entryDirection = new Vector2Dto { x = d.entryDirection.x, y = d.entryDirection.y },
                            width = d.width
                        }).ToList(),
                        windows = t.windows.Select(w => new WindowDto
                        {
                            id = w.id,
                            position = new Vector2Dto { x = w.position.x, y = w.position.y },
                            outwardNormal = new Vector2Dto { x = w.outwardNormal.x, y = w.outwardNormal.y },
                            floorLevel = w.floorLevel
                        }).ToList(),
                        height = t.height
                    }).ToList(),
                    grid = new GridDto
                    {
                        minX = grid.minX,
                        minZ = grid.minZ,
                        width = grid.width,
                        height = grid.height,
                        walkable = grid.GetWalkableArrayForSerialization(),
                        elevation = grid.GetElevationArrayForSerialization().Select(e => (int)e).ToArray()
                    }
                };

                string json = JsonUtility.ToJson(file);
                string finalPath = DiskCachePath(cacheKey);
                string tempPath = finalPath + ".tmp";
                // Écriture dans un fichier temporaire puis remplacement — évite qu'un autre process
                // du pool lisant EN MÊME TEMPS ne tombe sur un fichier à moitié écrit si deux
                // instances construisent la même carte pour la première fois au même instant.
                File.WriteAllText(tempPath, json);
                // File.Move (renommage), pas Copy+Delete : Copy réécrit tout le contenu une seconde
                // fois sur le disque pour rien, Move est en principe une opération de métadonnées.
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tempPath, finalPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TacticalGridBuilder] Échec d'écriture du cache disque ({cacheKey}) : {ex.Message} — sans conséquence, juste pas de gain la prochaine fois.");
            }
        }

        /// <summary>Tente de charger le gabarit géométrique depuis le cache disque — false si absent
        /// OU si la moindre incohérence est détectée (JsonUtility ne lève PAS d'exception sur un DTO
        /// mal formé, il produit silencieusement des données par défaut/vides : on vérifie donc
        /// explicitement que chaque section attendue est bien présente et non vide avant de faire
        /// confiance au résultat, plutôt que de risquer des murs fantômes ou une grille tronquée).</summary>
        private static bool TryLoadFromDisk(string cacheKey, out List<WallSegment> wallSegments, out List<TacticalBuilding> buildingTemplates, out TacticalGrid grid)
        {
            wallSegments = null;
            buildingTemplates = null;
            grid = null;

            string path = DiskCachePath(cacheKey);
            if (!File.Exists(path)) return false;

            try
            {
                string json = File.ReadAllText(path);
                GeometryCacheFile file = JsonUtility.FromJson<GeometryCacheFile>(json);
                if (file?.grid == null || file.wallSegments == null || file.buildingTemplates == null) return false;
                if (file.wallSegments.Count == 0 || file.buildingTemplates.Count == 0) return false;
                if (file.grid.width <= 0 || file.grid.height <= 0) return false;
                if (file.grid.walkable == null || file.grid.walkable.Length != file.grid.width * file.grid.height) return false;
                if (file.grid.elevation == null || file.grid.elevation.Length != file.grid.width * file.grid.height) return false;

                wallSegments = file.wallSegments.Select(w => new WallSegment
                {
                    p1 = new Vector2(w.p1.x, w.p1.y),
                    p2 = new Vector2(w.p2.x, w.p2.y),
                    isDoorGap = w.isDoorGap,
                    buildingId = w.buildingId
                }).ToList();
                buildingTemplates = file.buildingTemplates.Select(t => new TacticalBuilding
                {
                    id = t.id,
                    footprint = t.footprint.Select(p => new Vector2(p.x, p.y)).ToList(),
                    doors = (t.doors ?? new List<DoorDto>()).Select(d => new TacticalDoor
                    {
                        position = new Vector2(d.position.x, d.position.y),
                        entryDirection = new Vector2(d.entryDirection.x, d.entryDirection.y),
                        width = d.width
                    }).ToList(),
                    windows = (t.windows ?? new List<WindowDto>()).Select(w => new TacticalWindow
                    {
                        id = w.id,
                        position = new Vector2(w.position.x, w.position.y),
                        outwardNormal = new Vector2(w.outwardNormal.x, w.outwardNormal.y),
                        floorLevel = w.floorLevel
                    }).ToList(),
                    height = t.height > 0f ? t.height : 6f
                }).ToList();
                ZStrata[] elevation = file.grid.elevation.Select(e => (ZStrata)e).ToArray();
                grid = new TacticalGrid(file.grid.minX, file.grid.minZ, file.grid.width, file.grid.height, file.grid.walkable, elevation);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TacticalGridBuilder] Échec de lecture du cache disque ({cacheKey}) : {ex.Message} — reconstruction depuis la scène.");
                wallSegments = null;
                buildingTemplates = null;
                grid = null;
                return false;
            }
        }
    }
}
