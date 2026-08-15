using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Gère la destruction des bâtiments et décors suite aux tirs d'artillerie / mortiers / canons.
/// À la destruction, le bâtiment s'effondre en ruines calcinées au ras du sol, élimine les occupants
/// et libère totalement le passage pour que l'infanterie et les blindés puissent le traverser sans obstacle.
/// </summary>
public class DestructibleEnvironment : MonoBehaviour
{
    public float health = 300f;
    public float maxHealth = 300f;
    public bool isDestroyed = false;

    public static readonly List<Bounds> AllRubbleBounds = new List<Bounds>();

    private BuildingStructure buildingStructure;
    private NavMeshObstacle navObstacle;

    void Start()
    {
        buildingStructure = GetComponent<BuildingStructure>();
        navObstacle = GetComponent<NavMeshObstacle>();
    }

    public static bool IsPositionInRubble(Vector3 pos)
    {
        for (int i = 0; i < AllRubbleBounds.Count; i++)
        {
            Bounds b = AllRubbleBounds[i];
            if (pos.x >= b.min.x - 3.0f && pos.x <= b.max.x + 3.0f &&
                pos.z >= b.min.z - 3.0f && pos.z <= b.max.z + 3.0f)
            {
                return true;
            }
        }
        return false;
    }

    public void TakeDamage(float damage)
    {
        if (isDestroyed) return;

        health -= damage;
        Debug.Log($"<color=orange>[DestructibleEnvironment] {gameObject.name} prend {damage:F0} dégâts (PV: {Mathf.Max(0, health):F0}/{maxHealth})</color>");

        if (health <= 0)
        {
            DestroyEnvironment();
        }
    }

    private void DestroyEnvironment()
    {
        isDestroyed = true;
        
        Debug.Log($"<color=red><b>💥 EFFONDREMENT DU BÂTIMENT : {gameObject.name} EST PULVÉRISÉ EN RUINES TRAVERSABLES !</b></color>");

        // 1. Tremblement de caméra
        if (Camera.main != null)
        {
            TacticalCamera tCam = Camera.main.GetComponent<TacticalCamera>();
            if (tCam != null) tCam.ShakeCamera(1.2f, 1.2f);
        }

        // 2. Traitement des occupants et victimes du bâtiment
        Bounds totalBounds = new Bounds(transform.position, new Vector3(18f, 2f, 18f));
        Collider[] allCols = GetComponentsInChildren<Collider>(true);
        if (allCols.Length > 0)
        {
            totalBounds = allCols[0].bounds;
            foreach (var c in allCols)
            {
                if (c != null)
                {
                    totalBounds.Encapsulate(c.bounds);
                    c.enabled = false; // CRITIQUE : Supprimer toute collision bloquante
                }
            }
        }
        AllRubbleBounds.Add(totalBounds);

        // Désactiver tous les NavMeshObstacle
        NavMeshObstacle obs = GetComponent<NavMeshObstacle>();
        if (obs != null) obs.enabled = false;
        foreach (var no in GetComponentsInChildren<NavMeshObstacle>(true))
        {
            if (no != null) no.enabled = false;
        }

        if (buildingStructure != null)
        {
            List<UnitAI> casualties = new List<UnitAI>();

            if (buildingStructure.windows != null)
            {
                foreach (var win in buildingStructure.windows)
                {
                    if (win.isOccupied && win.occupant != null && !win.occupant.isDead)
                    {
                        if (!casualties.Contains(win.occupant)) casualties.Add(win.occupant);
                    }
                }
            }

            if (buildingStructure.unitsInside != null)
            {
                foreach (var u in buildingStructure.unitsInside)
                {
                    if (u != null && !u.isDead && !casualties.Contains(u)) casualties.Add(u);
                }
            }

            if (buildingStructure.unitsOnRoof != null)
            {
                foreach (var u in buildingStructure.unitsOnRoof)
                {
                    if (u != null && !u.isDead && !casualties.Contains(u)) casualties.Add(u);
                }
            }

            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && !u.isDead && !casualties.Contains(u))
                {
                    Vector3 pos = u.transform.position;
                    if (pos.x >= totalBounds.min.x - 1.0f && pos.x <= totalBounds.max.x + 1.0f &&
                        pos.z >= totalBounds.min.z - 1.0f && pos.z <= totalBounds.max.z + 1.0f)
                    {
                        casualties.Add(u);
                    }
                }
            }

            foreach (var victim in casualties)
            {
                if (victim != null && !victim.isDead)
                {
                    Debug.Log($"<color=red><b>💀 {victim.gameObject.name} est écrasé sous les gravats du bâtiment !</b></color>");
                    victim.TakeDamage(9999f, Vector3.up);
                }
            }

            // Désactiver le toit et la visibilité tactique
            if (buildingStructure.tacticalVisibility != null)
            {
                if (buildingStructure.tacticalVisibility.roofObject != null)
                {
                    buildingStructure.tacticalVisibility.roofObject.SetActive(false);
                }
                buildingStructure.tacticalVisibility.enabled = false;
            }

            // Désactiver toutes les interactions de portes et fenêtres
            foreach (var d in buildingStructure.doorInteractions)
            {
                if (d != null) d.gameObject.SetActive(false);
            }
            foreach (var w in buildingStructure.windowInteractions)
            {
                if (w != null) w.gameObject.SetActive(false);
            }

            BuildingStructure.AllBuildings.Remove(buildingStructure);
        }

        // 3. Transformation visuelle complète : Aplatissement de TOUS les MeshFilters au ras du sol
        MeshFilter[] allMeshFilters = GetComponentsInChildren<MeshFilter>();
        foreach (var mf in allMeshFilters)
        {
            if (mf != null && mf.sharedMesh != null)
            {
                Mesh mesh = Instantiate(mf.sharedMesh);
                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i].y = Mathf.Min(vertices[i].y, 0.08f); // Gravats plats au sol
                }
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                mf.sharedMesh = mesh;
            }
        }

        // 4. Libération totale du passage : DÉSACTIVER TOUS LES COLLIDERS DU BÂTIMENT ET DES ENFANTS
        Collider[] allColliders = GetComponentsInChildren<Collider>();
        foreach (var c in allColliders)
        {
            if (c != null) c.enabled = false;
        }

        // Désactiver tout NavMeshObstacle
        NavMeshObstacle[] allObstacles = GetComponentsInChildren<NavMeshObstacle>();
        foreach (var obstacleComp in allObstacles)
        {
            if (obstacleComp != null) obstacleComp.enabled = false;
        }

        // 5. Matériaux noirs calcinés (Ruines fumantes)
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        Shader litOrUnlit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Unlit");
        
        foreach (var r in renderers)
        {
            if (r != null && !r.gameObject.name.Contains("Health"))
            {
                Material m = new Material(litOrUnlit);
                Color rubbleColor = new Color(0.10f, 0.09f, 0.08f, 1f);
                m.color = rubbleColor;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", rubbleColor);
                m.enableInstancing = true;
                r.sharedMaterial = m;
            }
        }
    }
}
