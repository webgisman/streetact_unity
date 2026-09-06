using System.Collections.Generic;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// État complet d'UNE partie Deathmatch/Zone de Contrôle concurrente ("Option B", 2026-08-30 —
    /// voir la discussion d'architecture "des milliers de parties simultanées sur un seul VPS").
    /// Contrairement au mode Conquête (RunConquestSkirmish, INCHANGÉ, toujours basé sur de vraies
    /// UnitAI/BuildingStructure de scène), ce type ne référence JAMAIS un GameObject : le serveur est
    /// headless (aucune caméra, personne ne "regarde" la scène pendant un match multijoueur), donc
    /// rien n'est perdu à garder l'intégralité de l'état en données pures (Novgov.TacticalCore),
    /// portées d'un tour à l'autre EN MÉMOIRE plutôt que reconstruites depuis la scène à chaque tour.
    ///
    /// C'est ce qui permet à un seul processus de faire tourner des centaines/milliers de parties à
    /// la fois : un MatchState ne coûte que quelques Ko de RAM (une poignée d'objets TacticalUnit/
    /// Barricade), contre ~155 Mo pour une partie basée sur de vraies unités/bâtiments Unity
    /// (Resources.Load + Instantiate de prefabs avec Rigidbody/Collider/Animator/NavMeshAgent).
    ///
    /// Géométrie (murs/grille) : prise UNE SEULE FOIS par TacticalGridBuilder.BuildFromScene() au
    /// tout début du match (voir MatchSessionManager.RunMatch), jamais rechargée depuis la scène
    /// ensuite — c'est ce qui rend chaque partie totalement indépendante des autres après ce court
    /// instant initial (protégé par matchInProgress contre un combat de Conquête concurrent qui
    /// échangerait la géométrie de scène AU MÊME MOMENT, voir RunMatch).
    /// </summary>
    public class MatchState
    {
        public string MatchId;
        public string Mode; // "deathmatch" | "zone_control"
        public PlayerConnection P1;
        public PlayerConnection P2;

        public TacticalWorldState World;
        public MatchZoneState Zone; // non-null uniquement en zone_control — remplace CaptureZone.Instance

        // Chemin tactique en attente pour LE PROCHAIN tour, par identifiant d'unité — remplace
        // UnitAI.tacticalPath (qui n'existe plus : aucune UnitAI réelle n'est jamais créée pour ces
        // deux modes). Vidé après chaque résolution de tour (voir RunExecutionPhasePure) — un ordre
        // ne vaut que pour le tour où il a été soumis, exactement comme unit.ResetOrderState()
        // aujourd'hui.
        public Dictionary<string, List<TacticalPathManager.TacticalNode>> PendingOrderNodes = new Dictionary<string, List<TacticalPathManager.TacticalNode>>();

        // Occupation de fenêtre PROPRE À CETTE PARTIE — remplace BuildingStructure.BuildingWindow.
        // isOccupied (champ MUTABLE sur le VRAI GameObject partagé entre TOUTES les parties utilisant
        // la même carte) : sans cette séparation, une partie garnissant une fenêtre empêcherait
        // silencieusement une AUTRE partie concurrente d'utiliser "la même" fenêtre sur SA propre
        // carte logique — bug de corruption trouvé pendant la conception de ce chantier (2026-08-30),
        // même famille que celui déjà identifié pour la santé des bâtiments. Clé = (index du bâtiment
        // dans BuildingsSnapshot/BuildingIndex, BuildingWindow.id).
        public HashSet<(int buildingIdx, int windowId)> OccupiedWindows = new HashSet<(int, int)>();
        public Dictionary<string, (int buildingIdx, int windowId)> CurrentWindowByUnitId = new Dictionary<string, (int, int)>();

        // Identité réseau par unité — TacticalUnit (Novgov.TacticalCore) ne porte ni le type d'arme
        // (Fantassin/CharLeopard/...) ni une hauteur Y cosmétique continue (voir TacticalUnit.zStrata,
        // discret, pas assez précis pour le rendu client) : ces deux informations sont nécessaires
        // pour reconstituer les messages réseau (DeployedUnit/UnitState) sans jamais lire de
        // GameObject, donc suivies séparément ici.
        public Dictionary<string, int> UnitTypeById = new Dictionary<string, int>(); // valeur brute UnitSpawnerUI.UnitType
        public Dictionary<string, float> CurrentYById = new Dictionary<string, float>();

        public int NextUnitSequence = 0;

        // Tuile Slippy Map réellement utilisée par cette partie ("Z17_{x}_{y}" ou "Default") — voir
        // MatchGeometry/TacticalGridBuilder. Gardé pour les logs/diagnostics uniquement.
        public string CacheKey = "Default";

        // Largeur (demi-largeur, mètres) d'une barricade déployée — mesurée UNE SEULE FOIS au
        // démarrage du serveur depuis le vrai prefab Road_barrier (voir MatchSessionManager.
        // SetupWorldOnce/MeasureBarricadeHalfWidth) plutôt que devinée : une barricade posée en
        // donnée pure (jamais de RoadBarrier/BoxCollider réel pour ces deux modes) doit occuper
        // exactement le même espace au sol que la version Conquête/Solo pour un blocage de ligne de
        // vue/déplacement cohérent.
        public static float BarricadeHalfWidthMeters = 2f;
    }

    /// <summary>Remplace CaptureZone (composant de scène UNIQUE et PARTAGÉ, voir CaptureZone.cs) par
    /// une instance de donnée indépendante par partie — même logique exacte (ProgressPerTick=5,
    /// DecayPerTick=1, CaptureRadius=12, voir CaptureZone.cs pour le détail commenté de chaque règle),
    /// mais plus aucune UnitAI.AllLivingUnits/transform.position lus : Tick() reçoit directement les
    /// positions du tick en cours (voir MatchSessionManager.CaptureTacticalSnapshotPure).</summary>
    public class MatchZoneState
    {
        public const float CaptureRadius = 12f;
        private const float ProgressPerTick = 5f;
        private const float DecayPerTick = 1f;

        public Vector3 Center;
        public float ProgressTeam1 { get; private set; }
        public float ProgressTeam2 { get; private set; }

        public void Tick(List<(string id, int team, Vector2 position, bool isDead)> units)
        {
            bool team1Present = false, team2Present = false;

            foreach (var u in units)
            {
                if (u.isDead) continue;
                float dx = u.position.x - Center.x;
                float dz = u.position.y - Center.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) > CaptureRadius) continue;
                if (u.team == 1) team1Present = true;
                else if (u.team == 2) team2Present = true;
            }

            if (team1Present && team2Present)
            {
                // Zone contestée : aucune équipe n'avance.
            }
            else if (team1Present)
            {
                ProgressTeam1 = Mathf.Clamp(ProgressTeam1 + ProgressPerTick, 0f, 100f);
                ProgressTeam2 = Mathf.Clamp(ProgressTeam2 - DecayPerTick, 0f, 100f);
            }
            else if (team2Present)
            {
                ProgressTeam2 = Mathf.Clamp(ProgressTeam2 + ProgressPerTick, 0f, 100f);
                ProgressTeam1 = Mathf.Clamp(ProgressTeam1 - DecayPerTick, 0f, 100f);
            }
            else
            {
                ProgressTeam1 = Mathf.Clamp(ProgressTeam1 - DecayPerTick, 0f, 100f);
                ProgressTeam2 = Mathf.Clamp(ProgressTeam2 - DecayPerTick, 0f, 100f);
            }
        }

        /// <summary>0 = personne n'a atteint 100% ; sinon l'équipe gagnante.</summary>
        public int GetWinningTeamIfComplete()
        {
            if (ProgressTeam1 >= 100f) return 1;
            if (ProgressTeam2 >= 100f) return 2;
            return 0;
        }
    }

    /// <summary>Requêtes géométriques PURES sur un TacticalWorldState de partie — remplacent
    /// BuildingStructure.FindBuildingAt/GetClosestWindowIgnoringOccupancy/GetClosestDoor et
    /// UnitSpawnerUI.FindGroundLevelNavPoint (2026-08-30, "des milliers de cartes") : ces méthodes-là
    /// lisent la scène VIVANTE (BuildingStructure.AllBuildings global, vrai NavMesh), ce qui est
    /// dangereux dès qu'une AUTRE tuile que celle de CETTE partie peut être chargée en scène au même
    /// instant (deux parties concurrentes sur deux tuiles différentes partagent les MÊMES coordonnées
    /// locales — voir CityGenerator, chaque tuile est générée centrée sur l'origine Unity — donc une
    /// lecture de la scène vivante pendant le tour d'une partie dont ce n'est pas la tuile renverrait
    /// silencieusement le MAUVAIS bâtiment, pas une erreur visible). Ces méthodes ne lisent QUE
    /// ms.World, jamais un GameObject.</summary>
    public static class MatchGeometry
    {
        /// <summary>Équivalent pur de BuildingStructure.FindBuildingAt — renvoie l'id du bâtiment
        /// (== son index dans world.buildings, invariant garanti par TacticalGridBuilder) ou -1.</summary>
        public static int FindBuildingAt(TacticalWorldState world, Vector2 pos)
        {
            for (int i = 0; i < world.buildings.Count; i++)
            {
                var b = world.buildings[i];
                if (b.footprint != null && b.footprint.Count >= 3 && Novgov.TacticalCore.GeometryMath.PointInPolygon(b.footprint, pos))
                    return b.id;
            }
            return -1;
        }

        /// <summary>Équivalent pur de BuildingStructure.GetClosestWindow(requireFree) — le filtre de
        /// disponibilité est fourni par l'appelant (voir MatchState.OccupiedWindows), jamais lu depuis
        /// BuildingWindow.isOccupied (état partagé entre parties).</summary>
        public static TacticalWindow GetClosestWindow(TacticalBuilding building, Vector2 fromPos, System.Func<int, bool> isFree)
        {
            TacticalWindow closest = null;
            float minDist = float.MaxValue;
            foreach (var win in building.windows)
            {
                if (isFree != null && !isFree(win.id)) continue;
                float dist = Vector2.Distance(fromPos, win.position);
                if (dist < minDist) { minDist = dist; closest = win; }
            }
            return closest;
        }

        /// <summary>Équivalent pur de BuildingStructure.GetClosestDoor.</summary>
        public static TacticalDoor GetClosestDoor(TacticalBuilding building, Vector2 fromPos)
        {
            TacticalDoor closest = null;
            float minDist = float.MaxValue;
            foreach (var door in building.doors)
            {
                float dist = Vector2.Distance(fromPos, door.position);
                if (dist < minDist) { minDist = dist; closest = door; }
            }
            return closest;
        }

        /// <summary>Équivalent pur de UnitSpawnerUI.FindGroundLevelNavPoint — même stratégie de
        /// recherche (le point visé, puis un anneau de points à distances/angles croissants), mais
        /// interroge la grille de marche déjà en cache (ms.World.grid) au lieu du vrai NavMesh vivant.
        /// Corrige au passage un bug latent déjà présent en production (2026-08-30) : l'ancien appel
        /// avait lieu PENDANT le déploiement, donc APRÈS que le verrou protégeant la géométrie de la
        /// partie ait été relâché — si un combat de Conquête démarrait entre-temps et échangeait la
        /// scène, le déploiement se calait sur le MAUVAIS NavMesh. Préfère une cellule au SOL
        /// (ZStrata.Sol, jamais un toit) parmi les candidates praticables, la plus proche du point
        /// visé plutôt que la plus basse en Y (pas de Y continu dans la grille) — même intention que
        /// l'original : ne jamais faire apparaître une unité sur un toit.</summary>
        public static Vector2 FindGroundLevelInGrid(TacticalGrid grid, Vector2 desired, float searchRadius)
        {
            if (grid == null) return desired;

            var candidates = new List<Vector2> { desired };
            const int ringSteps = 8;
            for (int ring = 1; ring <= 3; ring++)
            {
                float radius = searchRadius * ring / 3f;
                for (int i = 0; i < ringSteps; i++)
                {
                    float angle = i * (360f / ringSteps) * Mathf.Deg2Rad;
                    candidates.Add(desired + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
                }
            }

            foreach (var candidate in candidates)
            {
                if (grid.TryWorldToCell(candidate, out int cx, out int cz)
                    && grid.IsWalkable(cx, cz)
                    && grid.GetElevation(cx, cz) == ZStrata.Sol)
                {
                    return grid.CellToWorld(cx, cz);
                }
            }
            return desired; // repli : aucune cellule au sol praticable trouvée dans le rayon
        }
    }

    /// <summary>Statistiques par type d'unité — extraites AU MOT PRÈS de UnitAI.cs (Start(), lignes
    /// 352-438 : santé/portée par type) et de MatchSessionManager.BuildTacticalUnit (dégâts/cadence de
    /// tir, déjà en dur là-bas, jamais lus depuis un prefab). Nécessaire ici car le déploiement en
    /// donnée pure (voir MatchSessionManager.PlaceCombatUnitPure) ne passe plus jamais par
    /// UnitSpawnerUI.SpawnUnitAt (qui n'aurait fait qu'Instantier un prefab pour relire ces mêmes
    /// valeurs) : un seul et même barème, jamais dupliqué ailleurs.</summary>
    public static class UnitTypeStats
    {
        public static void Get(UnitSpawnerUI.UnitType type, out int health, out float porteeDetection, out int weaponDamage, out float weaponCooldownSeconds, out bool isMortar, out bool isTank)
        {
            isTank = type == UnitSpawnerUI.UnitType.CharLeopard;
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard:
                    health = 500; porteeDetection = 45f; weaponDamage = 150; weaponCooldownSeconds = 1.8f; isMortar = false;
                    break;
                case UnitSpawnerUI.UnitType.VehiculeCanon:
                    health = 250; porteeDetection = 45f; weaponDamage = 75; weaponCooldownSeconds = 1.4f; isMortar = false;
                    break;
                case UnitSpawnerUI.UnitType.Mortier:
                    health = 350; porteeDetection = 120f; weaponDamage = 0; weaponCooldownSeconds = 0f; isMortar = true;
                    break;
                default: // Fantassin
                    health = 100; porteeDetection = 15f; weaponDamage = 15; weaponCooldownSeconds = 0.35f; isMortar = false;
                    break;
            }
        }

        /// <summary>Distance maximale parcourue en un tour, en mètres. Source UNIQUE, partagée par les
        /// deux moteurs de résolution : le moteur pur codait la valeur en dur et le moteur vivant
        /// lisait le champ d'instance de l'UnitAI, deux chemins qui donnaient la même chose aujourd'hui
        /// mais qui auraient divergé silencieusement dès qu'un type d'unité aurait eu sa propre valeur.
        ///
        /// Les blindés sont un peu plus lents que l'infanterie, ce qui correspond à leurs vitesses de
        /// déplacement respectives (UnitAI.OnNavMeshReady : 3.8 contre 4.2 m/s pour la traversée
        /// directe) et donne une raison tactique de plus de faire avancer l'infanterie en tête.</summary>
        public static float MovementBudget(UnitSpawnerUI.UnitType type)
        {
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard: return 42f;
                case UnitSpawnerUI.UnitType.VehiculeCanon: return 46f;
                case UnitSpawnerUI.UnitType.Mortier: return 34f; // pièce lourde à remettre en batterie
                default: return UnitAI.DefaultMaxMovementPerTurn; // Fantassin : 50m, la référence
            }
        }

        public static string TypeName(UnitSpawnerUI.UnitType type)
        {
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard: return "CharLeopard";
                case UnitSpawnerUI.UnitType.VehiculeCanon: return "VehiculeCanon";
                case UnitSpawnerUI.UnitType.Mortier: return "Mortier";
                case UnitSpawnerUI.UnitType.BarricadeRoutiere: return "Barricade";
                default: return "Fantassin";
            }
        }

        /// <summary>Coût en points de déploiement d'une unité de combat (voir
        /// MatchSessionManager.CombatPointBudget). Le combat n'a aucun aléa de précision/esquive
        /// (TacticalResolver.ApplyDamage applique des dégâts fixes), donc la composition la plus
        /// lourde autorisée gagnait systématiquement tant que seul le NOMBRE d'unités était plafonné
        /// (jusqu'à 4 CharLeopard, 2000 PV/~330 DPS cumulés, contre 1050 PV pour le repli par défaut
        /// mixte) — aucune raison rationnelle de jamais varier sa composition (voir rapport d'audit
        /// jouabilité, défaut bloquant #1). Barème approximatif basé sur (PV + DPS×10)/50, arrondi :
        /// Fantassin 100PV/43DPS→1, VehiculeCanon 250PV/54DPS→2, Mortier 350PV (portée/utilité
        /// indirecte)→2, CharLeopard 500PV/83DPS→3.</summary>
        public static int DeploymentCost(UnitSpawnerUI.UnitType type)
        {
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard: return 3;
                case UnitSpawnerUI.UnitType.VehiculeCanon: return 2;
                case UnitSpawnerUI.UnitType.Mortier: return 2;
                case UnitSpawnerUI.UnitType.BarricadeRoutiere: return 0;
                default: return 1; // Fantassin
            }
        }
    }
}
