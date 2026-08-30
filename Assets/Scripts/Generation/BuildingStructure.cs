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
        public int edgeIndex;          // Index de l'arête du polygone portant cette porte
        public float edgeDistance;     // Distance le long de l'arête depuis son premier sommet
        public float width = 1.4f;     // Largeur de l'ouverture à découper dans le mur
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

    public List<Vector2> polygonFootprint = new List<Vector2>();
    public Vector3 centroid = Vector3.zero;
    public float height = 6.0f;
    public Bounds bounds2D;
    private bool boundsComputed = false;

    public List<BuildingDoor> doors = new List<BuildingDoor>();
    public List<BuildingWindow> windows = new List<BuildingWindow>();
    public List<Novgov.Interaction.DoorInteraction> doorInteractions = new List<Novgov.Interaction.DoorInteraction>();
    public List<Novgov.Interaction.WindowInteraction> windowInteractions = new List<Novgov.Interaction.WindowInteraction>();
    public List<UnitAI> unitsInside = new List<UnitAI>();
    public List<UnitAI> unitsOnRoof = new List<UnitAI>();
    public TacticalVisibility tacticalVisibility;
    private bool isRegistered = false;

    public static readonly List<BuildingStructure> AllBuildings = new List<BuildingStructure>();

    public void InitPolygon(List<Vector2> footprint, float buildingHeight)
    {
        polygonFootprint = new List<Vector2>(footprint);
        height = buildingHeight;
        ComputeCentroidAndBounds();
    }

    public void ComputeCentroidAndBounds()
    {
        if (polygonFootprint == null || polygonFootprint.Count == 0) return;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float sumX = 0f, sumZ = 0f;

        for (int i = 0; i < polygonFootprint.Count; i++)
        {
            Vector2 p = polygonFootprint[i];
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.y > maxZ) maxZ = p.y;
            sumX += p.x;
            sumZ += p.y;
        }

        centroid = new Vector3(sumX / polygonFootprint.Count, 0f, sumZ / polygonFootprint.Count);
        bounds2D = new Bounds(new Vector3((minX + maxX) * 0.5f, height * 0.5f, (minZ + maxZ) * 0.5f),
                              new Vector3(Mathf.Max(1f, maxX - minX), height, Mathf.Max(1f, maxZ - minZ)));
        boundsComputed = true;
    }

    /// <summary>
    /// Test Point-dans-Polygone 2D ultra-rapide (Algorithme Ray-Casting XZ).
    /// </summary>
    public bool ContainsPoint2D(Vector3 worldPos)
    {
        return ContainsPoint2D(new Vector2(worldPos.x, worldPos.z));
    }

    public bool ContainsPoint2D(Vector2 pt)
    {
        if (!boundsComputed) ComputeCentroidAndBounds();
        if (boundsComputed)
        {
            if (pt.x < bounds2D.min.x - 0.5f || pt.x > bounds2D.max.x + 0.5f ||
                pt.y < bounds2D.min.z - 0.5f || pt.y > bounds2D.max.z + 0.5f)
            {
                return false;
            }
        }

        if (polygonFootprint == null || polygonFootprint.Count < 3) return false;

        bool inside = false;
        int count = polygonFootprint.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 pi = polygonFootprint[i];
            Vector2 pj = polygonFootprint[j];

            if (pi.y != pj.y && ((pi.y > pt.y) != (pj.y > pt.y)) &&
                (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Recherche statique instantanée du bâtiment sous une coordonnée du monde XZ.
    /// </summary>
    public static BuildingStructure FindBuildingAt(Vector3 worldPos)
    {
        Vector2 pt2D = new Vector2(worldPos.x, worldPos.z);
        for (int i = 0; i < AllBuildings.Count; i++)
        {
            BuildingStructure b = AllBuildings[i];
            if (b != null && b.ContainsPoint2D(pt2D))
            {
                return b;
            }
        }
        return null;
    }

    void OnEnable()
    {
        if (!isRegistered) 
        {
            AllBuildings.Add(this);
            isRegistered = true;
        }
    }

    void OnDisable()
    {
        if (isRegistered) 
        {
            AllBuildings.Remove(this);
            isRegistered = false;
        }
    }

    void OnDestroy()
    {
        if (isRegistered) 
        {
            AllBuildings.Remove(this);
            isRegistered = false;
        }
    }

    void Start()
    {
        if (!isRegistered) 
        {
            AllBuildings.Add(this);
            isRegistered = true;
        }
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
    public Novgov.Interaction.DoorInteraction GetClosestDoorInteraction(Vector3 fromPos, float maxDist = 3.5f)
    {
        if (doorInteractions == null || doorInteractions.Count == 0) return null;
        Novgov.Interaction.DoorInteraction best = null;
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
    /// Variante de GetClosestWindow qui NE LIT JAMAIS win.isOccupied (champ partagé sur ce
    /// GameObject, valide pour une seule partie à la fois) — l'appelant fournit son propre filtre de
    /// disponibilité (voir MatchState.OccupiedWindows, 2026-08-30) : plusieurs parties concurrentes
    /// utilisant la même carte gèrent chacune leur propre occupation de fenêtre en dehors de ce
    /// composant, sans jamais se marcher dessus.
    /// </summary>
    public BuildingWindow GetClosestWindowIgnoringOccupancy(Vector3 fromPos, System.Func<int, bool> isFree)
    {
        if (windows == null || windows.Count == 0) return null;

        BuildingWindow closest = null;
        float minDist = float.MaxValue;

        foreach (var win in windows)
        {
            if (isFree != null && !isFree(win.id)) continue;

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
