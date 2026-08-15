using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Composant léger attaché à chaque bâtiment pour identifier et gérer les portes et fenêtres
/// pour l'infiltration et les postes de tir de l'infanterie (Garnison & Heavy Cover).
/// </summary>
public class BuildingStructure : MonoBehaviour
{
    [System.Serializable]
    public class BuildingDoor
    {
        public Vector3 position;       // Position au sol de la porte
        public Vector3 entryDirection; // Normale orientée vers la rue
    }

    [System.Serializable]
    public class BuildingWindow
    {
        public int id;
        public Vector3 position;       // Position 3D de la fenêtre (à hauteur de tir)
        public Vector3 outwardNormal;  // Direction face à la rue (axe de tir)
        public int floorLevel;         // 0 = RDC, 1 = 1er étage, etc.
        public bool isOccupied;
        public UnitAI occupant;
    }

    public List<BuildingDoor> doors = new List<BuildingDoor>();
    public List<BuildingWindow> windows = new List<BuildingWindow>();
    public List<StreetAct.Interaction.DoorInteraction> doorInteractions = new List<StreetAct.Interaction.DoorInteraction>();
    public List<StreetAct.Interaction.WindowInteraction> windowInteractions = new List<StreetAct.Interaction.WindowInteraction>();
    public List<UnitAI> unitsInside = new List<UnitAI>();
    public List<UnitAI> unitsOnRoof = new List<UnitAI>();
    public TacticalVisibility tacticalVisibility;

    public static readonly List<BuildingStructure> AllBuildings = new List<BuildingStructure>();

    void OnEnable()
    {
        if (!AllBuildings.Contains(this)) AllBuildings.Add(this);
    }

    void OnDisable()
    {
        AllBuildings.Remove(this);
    }

    void OnDestroy()
    {
        AllBuildings.Remove(this);
    }

    void Start()
    {
        if (!AllBuildings.Contains(this)) AllBuildings.Add(this);
        if (GetComponent<DestructibleEnvironment>() == null)
        {
            gameObject.AddComponent<DestructibleEnvironment>();
        }
    }

    public void RegisterUnitInside(UnitAI unit)
    {
        if (unit != null && !unitsInside.Contains(unit))
        {
            unitsInside.Add(unit);
            if (tacticalVisibility != null) tacticalVisibility.OnUnitsChanged();
        }
    }

    public void UnregisterUnitInside(UnitAI unit)
    {
        if (unit != null && unitsInside.Contains(unit))
        {
            unitsInside.Remove(unit);
            if (tacticalVisibility != null) tacticalVisibility.OnUnitsChanged();
        }
    }

    public bool IsAnyUnitInside()
    {
        unitsInside.RemoveAll(u => u == null || u.isDead);
        return unitsInside.Count > 0;
    }

    public void RegisterUnitOnRoof(UnitAI unit)
    {
        if (unit != null && !unitsOnRoof.Contains(unit))
        {
            unitsOnRoof.Add(unit);
            if (tacticalVisibility != null) tacticalVisibility.OnUnitsChanged();
        }
    }

    public void UnregisterUnitOnRoof(UnitAI unit)
    {
        if (unit != null && unitsOnRoof.Contains(unit))
        {
            unitsOnRoof.Remove(unit);
            if (tacticalVisibility != null) tacticalVisibility.OnUnitsChanged();
        }
    }

    public bool IsAnyUnitOnRoof()
    {
        unitsOnRoof.RemoveAll(u => u == null || u.isDead);
        return unitsOnRoof.Count > 0;
    }

    /// <summary>
    /// Trouve le composant d'interaction de porte le plus proche.
    /// </summary>
    public StreetAct.Interaction.DoorInteraction GetClosestDoorInteraction(Vector3 fromPos, float maxDist = 3.5f)
    {
        if (doorInteractions == null || doorInteractions.Count == 0) return null;
        StreetAct.Interaction.DoorInteraction best = null;
        float minDist = maxDist;
        foreach (var di in doorInteractions)
        {
            if (di == null || di.doorData == null) continue;
            float d = Vector3.Distance(fromPos, di.doorData.position);
            if (d < minDist)
            {
                minDist = d;
                best = di;
            }
        }
        return best;
    }

    /// <summary>
    /// Trouve la porte d'entrée la plus proche d'une position donnée.
    /// </summary>
    public BuildingDoor GetClosestDoor(Vector3 fromPos)
    {
        if (doors == null || doors.Count == 0) return null;

        BuildingDoor closest = null;
        float minDist = float.MaxValue;

        foreach (var door in doors)
        {
            float dist = Vector3.Distance(fromPos, door.position);
            if (dist < minDist)
            {
                minDist = dist;
                closest = door;
            }
        }
        return closest;
    }

    /// <summary>
    /// Trouve la fenêtre disponible la plus proche d'une position de clic ou de cible.
    /// </summary>
    public BuildingWindow GetClosestWindow(Vector3 fromPos, bool requireFree = true)
    {
        if (windows == null || windows.Count == 0) return null;

        BuildingWindow closest = null;
        float minDist = float.MaxValue;

        foreach (var win in windows)
        {
            if (requireFree && win.isOccupied) continue;

            float dist = Vector3.Distance(fromPos, win.position);
            if (dist < minDist)
            {
                minDist = dist;
                closest = win;
            }
        }
        return closest;
    }

    /// <summary>
    /// Assigne une unité à une fenêtre de tir.
    /// </summary>
    public bool OccupyWindow(BuildingWindow window, UnitAI unit)
    {
        if (window == null || window.isOccupied) return false;

        window.isOccupied = true;
        window.occupant = unit;
        return true;
    }

    /// <summary>
    /// Libère une fenêtre lorsqu'une unité quitte son poste.
    /// </summary>
    public void VacateWindow(BuildingWindow window)
    {
        if (window == null) return;
        window.isOccupied = false;
        window.occupant = null;
    }

    /// <summary>
    /// Libère toutes les fenêtres occupées par une unité donnée.
    /// </summary>
    public void VacateAllForUnit(UnitAI unit)
    {
        if (windows == null) return;
        foreach (var win in windows)
        {
            if (win.occupant == unit)
            {
                win.isOccupied = false;
                win.occupant = null;
            }
        }
    }
}
