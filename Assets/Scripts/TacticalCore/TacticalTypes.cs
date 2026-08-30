using System.Collections.Generic;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Cœur logique du jeu : structures de données PURES (aucune dépendance à NavMeshAgent,
    /// Physics, ou tout composant Unity vivant en scène). Tout ce namespace doit rester
    /// déterministe sur n'importe quel appareil : seules les opérations IEEE 754 garanties
    /// correctement arrondies par la norme sont utilisées (+, -, *, /, sqrt, comparaisons) —
    /// JAMAIS Mathf.Sin/Cos/Atan2/Tan (non garantis identiques d'une puce à l'autre), jamais
    /// NavMesh/PhysX (Unity ne garantit aucun déterminisme inter-plateforme pour ces deux-là).
    /// Les cônes de vision utilisent un produit scalaire contre un seuil, pas un angle.
    ///
    /// Ce module calcule le RÉSULTAT OFFICIEL (déplacement, ligne de vue, dégâts) — le rendu
    /// Unity (NavMeshAgent, animations, particules) reste purement cosmétique et rejoue ce
    /// résultat sans jamais le recalculer, voir TacticalResolver.
    ///
    /// Toutes les valeurs numériques ci-dessous sont extraites AU MOT PRÈS de l'ancien code solo
    /// (UnitAI_Combat.cs, UnitAI_Movement.cs, UnitAI.cs, RoadBarrier.cs, MortarShell.cs,
    /// DestructibleEnvironment.cs, TacticalAIPlanner.cs — audit du 2026-08-30), jamais devinées.
    /// Écarts assumés par rapport à l'original, documentés explicitement où ils apparaissent :
    ///   - Overwatch à interruption (tir déclenché par franchissement d'un cône/d'une ligne) est
    ///     une VRAIE NOUVELLE fonctionnalité demandée par le concepteur — l'ancien "Guetter"/
    ///     "Embuscade" solo ne faisait que réduire les dégâts reçus (isGuarding, -50%), sans
    ///     jamais interrompre activement un déplacement ennemi. Les deux mécaniques coexistent
    ///     ici : la réduction de dégâts ET le déclenchement actif.
    /// </summary>

    /// <summary>Strate verticale discrète d'une unité — pas une hauteur continue.</summary>
    public enum ZStrata
    {
        Sol = 0,
        Fenetre = 1, // 1er étage / poste de tir en fenêtre (isGarrisoned)
        Toit = 2     // isRooftopSniper
    }

    /// <summary>Filtre appliqué au test de ligne de vue — permet des règles de vision spéciales
    /// (thermique, nocturne) sans dupliquer la logique de raycast elle-même.</summary>
    public enum VisionType
    {
        Normale,
        Thermique // ignore certains obstacles (fumée) — voir LineOfSight.Blocks
    }

    /// <summary>Un segment d'obstacle à la ligne de vue / au déplacement — un mur de bâtiment ou
    /// une barricade. La destruction est décidée au niveau du BÂTIMENT entier (voir
    /// TacticalBuilding), jamais segment par segment : c'est exactement le comportement de
    /// DestructibleEnvironment.cs (un seul pool de PV pour tout le bâtiment, tous les murs
    /// deviennent franchissables EN MÊME TEMPS quand ce pool tombe à 0).</summary>
    public class WallSegment
    {
        public Vector2 p1;
        public Vector2 p2;
        public bool isDoorGap;   // segment correspondant à une ouverture de porte : ne bloque jamais
        public int buildingId;   // -1 si hors bâtiment (ne devrait pas arriver)

        // Renvoie l'état de destruction du bâtiment parent — jamais une valeur locale au segment.
        public bool Destroyed(TacticalWorldState state) => state.GetBuilding(buildingId)?.destroyed ?? false;
        public bool BlocksSight(TacticalWorldState state) => !isDoorGap && !Destroyed(state);
        public bool BlocksMovement(TacticalWorldState state) => !isDoorGap && !Destroyed(state);
    }

    /// <summary>Un bâtiment entier — un seul pool de PV partagé par tous ses murs, exactement
    /// comme DestructibleEnvironment.cs (300 PV par défaut, jamais de destruction partielle
    /// mur-par-mur). Détruit = tous les WallSegment de ce buildingId cessent de bloquer d'un coup,
    /// et toute unité dont la position 2D tombe dans le footprint est écrasée (9999 dégâts, voir
    /// TacticalResolver.ApplyAreaDamage) — même règle que DestructibleEnvironment.DestroyEnvironment.</summary>
    /// <summary>Copie fidèle, purement géométrique, de BuildingStructure.BuildingDoor — sans l'état
    /// live (BuildingStructure.cs n'a pas d'état d'occupation pour les portes). Ajouté le 2026-08-30
    /// ("des milliers de cartes") : nécessaire pour que MatchSessionManager.BuildUnitOrdersPure
    /// résolve les portes depuis les données figées de la partie (MatchState.World) au lieu de
    /// BuildingStructure.FindBuildingAt/GetClosestDoor (scène VIVANTE, dangereux dès que deux
    /// parties sur deux tuiles différentes peuvent tourner en même temps sur le même processus — voir
    /// TacticalGridBuilder.BuildFromScene). Ni ce champ ni TacticalWindow ci-dessous ne sont jamais
    /// lus par TacticalResolver.cs (calcul de combat) — usage exclusivement côté construction d'ordres.</summary>
    public class TacticalDoor
    {
        public Vector2 position;
        public Vector2 entryDirection;
        public float width = 1.4f; // BuildingStructure.BuildingDoor.width — largeur par défaut exacte
    }

    /// <summary>Copie fidèle, purement géométrique, de BuildingStructure.BuildingWindow — MOINS
    /// isOccupied/occupant (état live, géré côté partie par MatchState.OccupiedWindows/
    /// CurrentWindowByUnitId, jamais partagé entre parties). Voir TacticalDoor pour le pourquoi.</summary>
    public class TacticalWindow
    {
        public int id;
        public Vector2 position;
        public Vector2 outwardNormal;
        public int floorLevel;
    }

    public class TacticalBuilding
    {
        public int id;
        public List<Vector2> footprint; // pour le test "écrasé sous les décombres"
        public float health = 300f;     // DestructibleEnvironment.cs:12-13 — valeur par défaut exacte
        public bool destroyed;
        public List<TacticalDoor> doors = new List<TacticalDoor>();
        public List<TacticalWindow> windows = new List<TacticalWindow>();
        // Hauteur réelle (BuildingStructure.height) — ajouté le 2026-08-30 ("des milliers de cartes")
        // pour que MatchSessionManager.BuildUnitOrdersPure résolve l'Escalade (orders.setPositionY)
        // depuis ms.World, jamais depuis BuildingStructure.height (scène vivante).
        public float height = 6f;
    }

    public class Barricade
    {
        public Vector2 p1;
        public Vector2 p2;
        public int ownerTeam;
        public float hp = 250f; // RoadBarrier.cs:17-18 — PV par défaut exacts
        public bool Destroyed => hp <= 0f;

        public bool BlocksSight => !Destroyed;
        public bool BlocksMovement => !Destroyed;
    }

    public class TacticalUnit
    {
        public string id;
        public int team;
        public Vector2 position;
        public ZStrata zStrata;
        public int health;
        public bool isDead;

        // --- Portées (UnitAI_Combat.cs) ---
        // Portée de REPÉRAGE (IsUnitSpottedByTeam) : 55m toit, 25m mortier, 35m sinon (ligne 207).
        public float spottingRange = 35f;
        // Portée d'ENGAGEMENT personnelle (GetVisibleEnemy) : porteeDetection (+20 si toit), 120
        // sans test de ligne de vue si mortier (ligne 245-264) — mortier neutralisé ici (voir
        // isMortar, l'engagement de zone se fait via TirMortier/mortarStrikes, pas ce champ).
        public float engagementRange = 15f;

        // --- Armement ---
        public int weaponDamage = 15;         // UnitAI_Combat.cs:409 : 15 infanterie / 75 canon-véhicule / 150 char lourd
        public float weaponCooldownSeconds;   // ligne 87-91 : 0.35 infanterie / 1.4 canon-véhicule / 1.8 char lourd, 0 = pas de tir direct (mortier)
        public float cooldownRemaining;       // état d'exécution — décrémenté à chaque tick, tir permis quand <= 0
        public bool isMortar;                 // le mortier n'engage JAMAIS directement (ligne 42-54/248-264) — uniquement via mortarStrikes

        public VisionType visionType = VisionType.Normale;

        // --- États de couverture/posture (UnitAI.cs) — mutuellement exclusifs dans TakeDamage,
        // priorité stricte garnison > barricade > guet (voir TacticalResolver.ComputeCoverMultiplier). ---
        public bool isGarrisoned;   // -75% dégâts reçus (UnitAI.cs:668-671), fenêtre ou porte gardée
        public bool isGuarding;     // -50% dégâts reçus (UnitAI.cs:678-681) — Guetter/Embuscade
        public bool isCamouflaged;  // invisible au ciblage (ne réduit PAS les dégâts reçus, casse au premier impact)

        // Bâtiment courant (garnison ou intérieur) — sert aux exceptions de ligne de vue
        // "tirer depuis/vers une ouverture" (GetVisibleEnemy CAS1/2/3). -1 si aucun.
        public int currentBuildingId = -1;
        // Normale de la fenêtre occupée (direction vers la rue) si isGarrisoned via une fenêtre —
        // null si garnison de porte (GuetterPorte, pas de restriction de cône, voir le rapport §2.9).
        public Vector2? windowNormal;

        // Overwatch (NOUVELLE fonctionnalité, voir en-tête de fichier) : actif tant que
        // watchTrigger != null.
        public OverwatchTrigger watchTrigger;

        // Hauteur Y RÉELLE (cosmétique, pour le rendu Unity uniquement) — null = inchangée depuis
        // avant ce tour. TacticalCore ne raisonne JAMAIS en Y continu pour ses calculs (voir
        // zStrata), ce champ ne sert qu'à positionner correctement le GameObject réel après une
        // montée/descente de toit (Escalade), voir TacticalResolver.ApplyEndOfPathEffects.
        public float? outputY;
    }

    /// <summary>Déclencheur de tir d'interruption (Overwatch) — soit un cône (surveillance de
    /// zone), soit une ligne (surveillance d'un couloir/d'une porte précise).</summary>
    public class OverwatchTrigger
    {
        public Vector2 origin;
        public Vector2 facing;       // normalisé — direction du cône
        public float cosHalfAngle;   // seuil de produit scalaire (évite tout appel trigonométrique)
        public float range;

        // Alternative "ligne" (surveillance de couloir/porte) : si non nulle, prioritaire sur le cône.
        public Vector2? linePointA;
        public Vector2? linePointB;
    }

    /// <summary>État tactique complet à un instant donné — sérialisable, indépendant de la scène
    /// Unity vivante. Un TacticalResolver.Resolve() en consomme un et en produit un nouveau.</summary>
    public class TacticalWorldState
    {
        public List<TacticalBuilding> buildings = new List<TacticalBuilding>();
        public List<WallSegment> wallSegments = new List<WallSegment>();
        public List<Barricade> barricades = new List<Barricade>();
        public List<TacticalUnit> units = new List<TacticalUnit>();
        public TacticalGrid grid;

        public TacticalBuilding GetBuilding(int id)
        {
            for (int i = 0; i < buildings.Count; i++) if (buildings[i].id == id) return buildings[i];
            return null;
        }
    }

    /// <summary>Un ordre de tour pour une unité : chemin voulu (liste de points), et un
    /// comportement de fin (Overwatch, tir sur cible, rien).</summary>
    public class UnitOrders
    {
        public string unitId;
        public List<Vector2> path = new List<Vector2>();
        public bool enterOverwatchAtEnd;
        public OverwatchTrigger overwatchToSet; // rempli seulement si enterOverwatchAtEnd

        // Postures de fin de chemin — miroir direct des NodeAction du rapport d'audit (§2.10) :
        // priorité garnison > guet > camouflage si plusieurs sont demandées (ne devrait pas arriver
        // en pratique, un seul NodeAction de posture par ordre).
        public bool setGuarding;      // Guetter / Embuscade -> isGuarding = true (persiste, jamais remis à false automatiquement — fidèle à l'original, voir rapport §8.2)
        public bool setCamouflaged;   // SeCacher -> isCamouflaged = true
        public bool setGarrisonWindow; // GarnisonFenetre -> isGarrisoned = true + windowNormal défini
        public Vector2? windowNormalToSet;
        public bool setGarrisonDoor;   // GuetterPorte -> isGarrisoned = true, PAS de windowNormal (pas de cône, rapport §2.9)
        public int enterBuildingId = -1;  // EntrerBatiment -> currentBuildingId
        public bool exitBuilding;         // SortirBatiment -> currentBuildingId = -1 ET LeaveGarrison (rapport §2.8)
        public float? setPositionY;       // Escalade (monte sur le toit) / descente implicite (voir MatchSessionManager.BuildUnitOrders)
    }

    /// <summary>Un événement produit par la résolution — la même granularité que
    /// NetMessage.Snapshot/UnitState, en amont : le protocole réseau existant peut être rempli
    /// directement depuis cette liste plutôt que depuis une simulation Unity de plusieurs
    /// secondes.</summary>
    public class TacticalEvent
    {
        public enum Kind { Move, Shot, Death, WallDestroyed, OverwatchTriggered }
        public Kind kind;
        public string unitId;
        public string targetUnitId;
        public Vector2 position;
        public int damage;
        public int buildingId;

        // Index de "pas" de résolution (incrémenté une fois par itération de la boucle de
        // déplacement dans TacticalResolver.Resolve) — sert uniquement à reconstruire une
        // chronologie pour l'animation de rejeu côté client (voir MatchSessionManager.
        // BuildSnapshotsFromEvents), aucun rôle dans le calcul du résultat lui-même.
        public int tick;
    }
}
