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
