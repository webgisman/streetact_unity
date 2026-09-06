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
    [Header("Health Settings")]
    public float maxHealth = 500f;
    public float health;
    public bool isDestroyed = false;
    public bool isHQ = false;

    /// <summary>
    /// Une zone de ruines franchissable librement. Décrite par l'EMPREINTE RÉELLE du bâtiment
    /// (polygone OSM) et non par un AABB monde : Collider.bounds reste aligné sur les axes du monde
    /// même pour un bâtiment tourné, si bien qu'un immeuble à ~45° produisait un rectangle bien plus
    /// large que sa silhouette. Ce fichier documentait déjà ce piège pour le calcul des victimes (voir
    /// localFootprint plus bas), mais la liste de franchissement était restée sur l'AABB brut, encore
    /// gonflé de 3m de chaque côté et sans aucun test de hauteur. Conséquence : dès le premier
    /// bâtiment détruit, toute unité passant à proximité voyait son NavMeshAgent coupé et glissait en
    /// ligne droite à travers les murs intacts, les barricades et les autres blindés.
    /// </summary>
    private class RubbleZone
    {
        public List<Vector2> footprint; // empreinte réelle en XZ ; null si le bâtiment n'en avait pas
        public Bounds worldBounds;      // repli quand footprint est null
        public float groundY;           // altitude des gravats
    }

    private static readonly List<RubbleZone> AllRubble = new List<RubbleZone>();

    // Marge horizontale : de quoi accepter une unité posée pile sur la ligne de l'ancien mur, sans
    // déborder sur la rue ni sur le bâtiment voisin (l'ancienne valeur était de 3m).
    private const float RubbleEdgeMargin = 1.0f;
    // Bande verticale : au-dessus, on n'est plus dans les gravats mais sur un toit ou un étage voisin.
    private const float RubbleHeightBand = 2.5f;

    private BuildingStructure buildingStructure;
    private NavMeshObstacle navObstacle;

    void Start()
    {
        buildingStructure = GetComponent<BuildingStructure>();
        navObstacle = GetComponent<NavMeshObstacle>();
    }

    /// <summary>Vide la liste des ruines. INDISPENSABLE entre deux parties : la liste est statique et
    /// n'était jamais purgée, donc elle survivait au rechargement de scène de l'écran de fin comme à
    /// l'enchaînement des matchs côté serveur — sur la carte suivante, toute neuve, des unités
    /// traversaient les murs dès le premier tour sans qu'aucun bâtiment n'ait été détruit.</summary>
    public static void ResetRubble()
    {
        AllRubble.Clear();
    }

    public static bool IsPositionInRubble(Vector3 pos)
    {
        for (int i = 0; i < AllRubble.Count; i++)
        {
            RubbleZone zone = AllRubble[i];

            // Un toit voisin ou un étage en surplomb n'est pas "dans les gravats".
            if (pos.y > zone.groundY + RubbleHeightBand) continue;

            Vector2 p = new Vector2(pos.x, pos.z);

            if (zone.footprint != null && zone.footprint.Count >= 3)
            {
                if (PointInPolygon(zone.footprint, p)) return true;
                if (DistanceToPolygonEdge(zone.footprint, p) <= RubbleEdgeMargin) return true;
                continue;
            }

            Bounds b = zone.worldBounds;
            if (p.x >= b.min.x - RubbleEdgeMargin && p.x <= b.max.x + RubbleEdgeMargin &&
                p.y >= b.min.z - RubbleEdgeMargin && p.y <= b.max.z + RubbleEdgeMargin)
            {
                return true;
            }
        }
        return false;
    }

    private static bool PointInPolygon(List<Vector2> polygon, Vector2 pt)
    {
        bool inside = false;
        int count = polygon.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 pi = polygon[i];
            Vector2 pj = polygon[j];
            if (pi.y != pj.y && ((pi.y > pt.y) != (pj.y > pt.y)) &&
                (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static float DistanceToPolygonEdge(List<Vector2> polygon, Vector2 pt)
    {
        float best = float.MaxValue;
        int count = polygon.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 a = polygon[j], b = polygon[i];
            Vector2 ab = b - a;
            float sqrLen = ab.x * ab.x + ab.y * ab.y;
            Vector2 proj = (sqrLen < 0.0001f)
                ? a
                : a + ab * Mathf.Clamp01(((pt.x - a.x) * ab.x + (pt.y - a.y) * ab.y) / sqrLen);
            float d = Vector2.Distance(pt, proj);
            if (d < best) best = d;
        }
        return best;
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

        if (isHQ && Novgov.Network.MultiplayerMatchController.Instance != null && Novgov.Network.MultiplayerMatchController.IsFlowActive)
        {
            Debug.Log($"<color=magenta><b>🚨 LE QUARTIER GÉNÉRAL A ÉTÉ DÉTRUIT ! 🚨</b></color>");
            Novgov.Server.MatchSessionManager.IsHQDestroyedThisMatch = true;
        }

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
        // Empreinte réelle si le bâtiment en a une (cas normal, générée depuis OSM) ; sinon repli sur
        // l'AABB, faute de mieux, mais avec une marge et une bande de hauteur strictes.
        BuildingStructure bs = buildingStructure != null ? buildingStructure : GetComponent<BuildingStructure>();
        AllRubble.Add(new RubbleZone
        {
            footprint = (bs != null && bs.polygonFootprint != null && bs.polygonFootprint.Count >= 3)
                ? new List<Vector2>(bs.polygonFootprint)
                : null,
            worldBounds = totalBounds,
            groundY = totalBounds.min.y
        });

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

            // Empreinte locale (alignée sur la rotation réelle du bâtiment) plutôt que l'AABB monde
            // de totalBounds : Collider.bounds est TOUJOURS un AABB monde même pour un collider
            // tourné, donc pour un bâtiment à ~45° ce test surestimait largement la silhouette
            // réelle et tuait des unités clairement à l'extérieur des décombres visuels.
            Bounds localFootprint = new Bounds(Vector3.zero, Vector3.zero);
            bool hasLocalFootprint = false;
            foreach (var c in allCols)
            {
                if (c == null) continue;
                Bounds wb = c.bounds;
                for (int cx = 0; cx <= 1; cx++)
                for (int cy = 0; cy <= 1; cy++)
                for (int cz = 0; cz <= 1; cz++)
                {
                    Vector3 corner = new Vector3(
                        cx == 0 ? wb.min.x : wb.max.x,
                        cy == 0 ? wb.min.y : wb.max.y,
                        cz == 0 ? wb.min.z : wb.max.z);
                    Vector3 localCorner = transform.InverseTransformPoint(corner);
                    if (!hasLocalFootprint) { localFootprint = new Bounds(localCorner, Vector3.zero); hasLocalFootprint = true; }
                    else localFootprint.Encapsulate(localCorner);
                }
            }

            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && !u.isDead && !casualties.Contains(u))
                {
                    Vector3 localPos = transform.InverseTransformPoint(u.transform.position);
                    if (hasLocalFootprint &&
                        localPos.x >= localFootprint.min.x - 1.0f && localPos.x <= localFootprint.max.x + 1.0f &&
                        localPos.z >= localFootprint.min.z - 1.0f && localPos.z <= localFootprint.max.z + 1.0f)
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
            if (r != null && !r.gameObject.name.Contains("Health") && litOrUnlit != null)
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
