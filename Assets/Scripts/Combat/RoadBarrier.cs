using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Système de Barricade Routière Tactique (Road_barrier).
/// Bloque le passage des véhicules et de l'infanterie (Carving NavMesh),
/// procure une couverture lourde (-60% de dégâts) aux soldats postés derrière,
/// et peut être détruite par des obus de chars ou de mortiers (250 PV).
/// </summary>
public class RoadBarrier : MonoBehaviour
{
    public static List<RoadBarrier> AllBarriers = new List<RoadBarrier>();

    [Header("Statistiques Barricade")]
    public int teamID = 1; // 1 = Joueur (Bleu), 2 = Ennemi (Rouge), 0 = Neutre
    public float health = 250f;
    public float maxHealth = 250f;

    private NavMeshObstacle obstacle;
    private BoxCollider boxCol;
    private Transform healthBarFill;
    private GameObject healthBarBg;

    void Awake()
    {
        if (!AllBarriers.Contains(this)) AllBarriers.Add(this);
    }

    void Start()
    {
        // 1. Détection des dimensions via les Renderers
        Bounds bounds = new Bounds(transform.position, Vector3.zero);
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        bool hasBounds = false;
        foreach (Renderer r in renderers)
        {
            if (r.gameObject.name.Contains("Health")) continue;
            if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
            else { bounds.Encapsulate(r.bounds); }
        }

        Vector3 localCenter = hasBounds ? transform.InverseTransformPoint(bounds.center) : new Vector3(0, 0.75f, 0);
        Vector3 size = new Vector3(
            Mathf.Max(2.0f, bounds.size.x / Mathf.Max(0.01f, transform.lossyScale.x)),
            Mathf.Max(1.2f, bounds.size.y / Mathf.Max(0.01f, transform.lossyScale.y)),
            Mathf.Max(1.0f, bounds.size.z / Mathf.Max(0.01f, transform.lossyScale.z))
        );

        // 2. Configuration Physique (BoxCollider)
        boxCol = GetComponent<BoxCollider>();
        if (boxCol == null) boxCol = gameObject.AddComponent<BoxCollider>();
        boxCol.center = localCenter;
        boxCol.size = size;

        // 3. Blocage NavMesh Dynamique (NavMeshObstacle Carving)
        obstacle = GetComponent<NavMeshObstacle>();
        if (obstacle == null) obstacle = gameObject.AddComponent<NavMeshObstacle>();
        obstacle.shape = NavMeshObstacleShape.Box;
        obstacle.center = localCenter;
        obstacle.size = size;
        obstacle.carving = true;
        obstacle.carvingMoveThreshold = 0.1f;
        obstacle.carveOnlyStationary = false;

        // 4. Teinte et Shader URP
        ApplyTacticalMaterial();

        // 5. Barre de Vie Billboard 3D
        SetupHealthBar();
    }

    void Update()
    {
        if (healthBarBg != null && Camera.main != null)
        {
            healthBarBg.transform.LookAt(healthBarBg.transform.position + Camera.main.transform.rotation * Vector3.forward,
                                         Camera.main.transform.rotation * Vector3.up);
        }
    }

    private void ApplyTacticalMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) return;
        Color teamColor = (teamID == 2) ? new Color(1f, 0.25f, 0.2f) : (teamID == 1 ? new Color(0.2f, 0.6f, 1f) : new Color(0.9f, 0.7f, 0.1f));

        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r.gameObject.name.Contains("Health")) continue;
            Material mat = new Material(shader);
            mat.color = teamColor;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", teamColor);
            r.sharedMaterial = mat;
        }
    }

    public void SetupHealthBar()
    {
        healthBarBg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        healthBarBg.name = "Barrier_HealthBarBG";
        Destroy(healthBarBg.GetComponent<Collider>());
        healthBarBg.transform.SetParent(this.transform);
        healthBarBg.transform.localPosition = new Vector3(0, 1.8f, 0);
        healthBarBg.transform.localScale = new Vector3(2.0f, 0.25f, 1f);

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        if (unlitShader != null)
        {
            Material bgMat = new Material(unlitShader);
            if (bgMat.HasProperty("_BaseColor")) bgMat.SetColor("_BaseColor", new Color(0f, 0f, 0f, 0.8f));
            else bgMat.color = new Color(0f, 0f, 0f, 0.8f);
            healthBarBg.GetComponent<Renderer>().material = bgMat;
        }

        GameObject fg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fg.name = "Barrier_HealthBarFill";
        Destroy(fg.GetComponent<Collider>());
        fg.transform.SetParent(healthBarBg.transform);
        fg.transform.localPosition = new Vector3(0, 0, -0.02f);
        fg.transform.localScale = Vector3.one;

        if (unlitShader != null)
        {
            Material fgMat = new Material(unlitShader);
            Color fgCol = (teamID == 2) ? new Color(1f, 0.3f, 0.3f) : new Color(0.3f, 0.8f, 1f);
            if (fgMat.HasProperty("_BaseColor")) fgMat.SetColor("_BaseColor", fgCol);
            else fgMat.color = fgCol;
            fg.GetComponent<Renderer>().material = fgMat;
        }
        healthBarFill = fg.transform;
    }

    public void TakeDamage(float amount)
    {
        health -= amount;
        Debug.Log($"<color=orange>[RoadBarrier] Barricade touchée : -{amount:F0} PV (Reste: {Mathf.Max(0, health):F0}/{maxHealth})</color>");

        if (healthBarFill != null)
        {
            float pct = Mathf.Clamp01(health / maxHealth);
            healthBarFill.localScale = new Vector3(pct, 1f, 1f);
            healthBarFill.localPosition = new Vector3((pct - 1f) * 0.5f, 0, -0.02f);
        }

        if (health <= 0f)
        {
            DestroyBarrier();
        }
    }

    private void DestroyBarrier()
    {
        Debug.Log($"<color=red><b>[RoadBarrier] 💥 Barricade routière pulvérisée ! Voie de circulation libérée.</b></color>");

        // Son d'écroulement / explosion
        AudioClip sound = ProceduralAudioBuilder.CreateMortarExplosionSound();
        if (sound != null) AudioSource.PlayClipAtPoint(sound, transform.position, 0.9f);

        // Flash & Fumée de débris
        GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.transform.position = transform.position + Vector3.up * 0.8f;
        flash.transform.localScale = Vector3.one * 2.5f;
        Destroy(flash.GetComponent<Collider>());
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
        if (unlit != null)
        {
            Material fMat = new Material(unlit);
            fMat.color = new Color(1f, 0.6f, 0.1f, 0.8f);
            flash.GetComponent<MeshRenderer>().sharedMaterial = fMat;
        }
        Destroy(flash, 0.25f);

        AllBarriers.Remove(this);
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (AllBarriers.Contains(this)) AllBarriers.Remove(this);
    }

    /// <summary>
    /// Vérifie si une position d'unité est à proximité immédiate d'une barricade pour bénéficier de couverture.
    /// </summary>
    public static bool IsUnitNearBarrier(Vector3 unitPos, float maxDistance = 2.2f)
    {
        for (int i = 0; i < AllBarriers.Count; i++)
        {
            RoadBarrier b = AllBarriers[i];
            if (b != null)
            {
                if (Vector3.Distance(b.transform.position, unitPos) <= maxDistance)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Applique des dégâts de souffle de zone aux barricades dans le rayon d'une explosion d'obus/mortier.
    /// </summary>
    public static void ApplySplashDamageToBarriers(Vector3 center, float radius, float maxDamage)
    {
        for (int i = AllBarriers.Count - 1; i >= 0; i--)
        {
            RoadBarrier b = AllBarriers[i];
            if (b != null)
            {
                float dist = Vector3.Distance(b.transform.position, center);
                if (dist <= radius)
                {
                    float factor = 1f - Mathf.Clamp01(dist / radius);
                    float dmg = maxDamage * factor;
                    b.TakeDamage(dmg);
                }
            }
        }
    }
}
