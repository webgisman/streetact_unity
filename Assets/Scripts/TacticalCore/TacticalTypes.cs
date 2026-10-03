using System.Collections.Generic;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Géométrie tactique PURE de la carte (bâtiments, portes, fenêtres, murs, barricades, grille),
    /// sans aucun composant Unity de scène : construite par TacticalGridBuilder.BuildFromScene et
    /// utilisée pour la géométrie de référence du serveur (vérification "city_verify", resynchro
    /// des clients). Le combat lui-même est simulé par le vrai moteur Unity (UnitAI, NavMesh) depuis
    /// le 2026-09-13. Seules des opérations IEEE 754 exactement arrondies (+, -, *, /, sqrt) : le
    /// résultat doit être identique sur Android et sur le serveur Linux.
    /// </summary>

    /// <summary>Strate verticale discrète d'une unité — pas une hauteur continue.</summary>
    public enum ZStrata
    {
        Sol = 0,
        Fenetre = 1, // 1er étage / poste de tir en fenêtre (isGarrisoned)
        Toit = 2     // isRooftopSniper
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

    /// <summary>Copie purement géométrique de BuildingStructure.BuildingDoor (transmise aux clients
    /// pour la resynchronisation de la carte, voir NetMessage.DoorGeometryDto).</summary>
    public class TacticalDoor
    {
        public Vector2 position;
        public Vector2 entryDirection;
        public float width = 1.4f; // BuildingStructure.BuildingDoor.width — largeur par défaut exacte
    }

    /// <summary>Copie purement géométrique de BuildingStructure.BuildingWindow (sans l'occupation).</summary>
    public class TacticalWindow
    {
        public int id;
        public Vector2 position;
        public Vector2 outwardNormal;
        public int floorLevel;
    }

    /// <summary>Un bâtiment : empreinte, hauteur, portes, fenêtres, et un seul pool de PV pour tous
    /// ses murs (comme DestructibleEnvironment).</summary>
    public class TacticalBuilding
    {
        public int id;
        public List<Vector2> footprint; // pour le test "écrasé sous les décombres"
        public float health = 300f;     // DestructibleEnvironment.cs:12-13 — valeur par défaut exacte
        public bool destroyed;
        public List<TacticalDoor> doors = new List<TacticalDoor>();
        public List<TacticalWindow> windows = new List<TacticalWindow>();
        public float height = 6f; // BuildingStructure.height
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

    /// <summary>Géométrie tactique de la carte à un instant donné, indépendante de la scène Unity.</summary>
    public class TacticalWorldState
    {
        public List<TacticalBuilding> buildings = new List<TacticalBuilding>();
        public List<WallSegment> wallSegments = new List<WallSegment>();
        public List<Barricade> barricades = new List<Barricade>();
        public TacticalGrid grid;

        public TacticalBuilding GetBuilding(int id)
        {
            for (int i = 0; i < buildings.Count; i++) if (buildings[i].id == id) return buildings[i];
            return null;
        }
    }

}
