using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Gère la visibilité en coupe et la transparence relative des toits de bâtiments
/// dès qu'un soldat cible une porte, s'infiltre à l'intérieur ou occupe une fenêtre,
/// tout en préservant le plancher solide pour tout fantassin sur le toit.
/// </summary>
public class TacticalVisibility : MonoBehaviour
{
    public GameObject roofObject;
    public BuildingStructure structure;
    
    private MeshRenderer roofRenderer;
    private Collider roofCollider;
    private Material originalOpaqueMaterial;
    private Material cutawayMaterial;

    public enum RoofRenderState { Opaque, CutawaySemiTransparent }
    public RoofRenderState currentState = RoofRenderState.Opaque;

    public bool isPlanificationPreview = false;
    private float checkTimer = 0f;

    void Awake()
    {
        EnsureComponents();
    }

    void Start()
    {
        EnsureComponents();
    }

    private void EnsureComponents()
    {
        if (roofObject == null)
        {
            Transform rT = transform.Find("Roof");
            if (rT != null) roofObject = rT.gameObject;
        }

        if (roofObject != null)
        {
            if (roofRenderer == null) roofRenderer = roofObject.GetComponent<MeshRenderer>();
            if (roofCollider == null) roofCollider = roofObject.GetComponent<Collider>();
            if (roofRenderer != null && originalOpaqueMaterial == null)
            {
                originalOpaqueMaterial = roofRenderer.sharedMaterial;
                CreateCutawayMaterial();
            }
        }

        if (structure == null)
        {
            structure = GetComponent<BuildingStructure>();
        }
        if (structure != null)
        {
            structure.tacticalVisibility = this;
        }
    }

    private void CreateCutawayMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Transparent")
                     ?? Shader.Find("Universal Render Pipeline/Lit");

        cutawayMaterial = new Material(shader);
        cutawayMaterial.name = "Roof_Cutaway_80Percent_Transparent";

        // 80% de transparence (Alpha = 0.20f) : Teinte fumée neutre et élégante
        Color cutawayColor = new Color(0.18f, 0.22f, 0.26f, 0.20f);
        
        if (cutawayMaterial.HasProperty("_BaseColor")) cutawayMaterial.SetColor("_BaseColor", cutawayColor);
        if (cutawayMaterial.HasProperty("_Color")) cutawayMaterial.SetColor("_Color", cutawayColor);

        // Configuration URP & Standard Transparent (80% transparent)
        if (cutawayMaterial.HasProperty("_Surface")) cutawayMaterial.SetFloat("_Surface", 1); // 1 = Transparent
        if (cutawayMaterial.HasProperty("_Blend")) cutawayMaterial.SetFloat("_Blend", 0); // 0 = Alpha
        if (cutawayMaterial.HasProperty("_ZWrite")) cutawayMaterial.SetFloat("_ZWrite", 0);
        if (cutawayMaterial.HasProperty("_Cull")) cutawayMaterial.SetFloat("_Cull", 0); // Double face

        cutawayMaterial.SetOverrideTag("RenderType", "Transparent");
        cutawayMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        cutawayMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        cutawayMaterial.SetInt("_ZWrite", 0);
        cutawayMaterial.DisableKeyword("_ALPHATEST_ON");
        cutawayMaterial.EnableKeyword("_ALPHABLEND_ON");
        cutawayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        cutawayMaterial.renderQueue = 3000;
        cutawayMaterial.enableInstancing = true;
    }

    public void SetPlanificationPreview(bool active)
    {
        isPlanificationPreview = active;
        UpdateVisibility();
    }

    public void OnUnitsChanged()
    {
        UpdateVisibility();
    }

    void Update()
    {
        checkTimer -= Time.deltaTime;
        if (checkTimer <= 0f)
        {
            checkTimer = 0.25f; // Scan 4 fois par seconde
            UpdateVisibility();
        }
    }

    public bool HasUnitsInsideBuilding()
    {
        if (isPlanificationPreview) return true;
        if (structure != null && structure.IsAnyUnitInside()) return true;

        if (structure != null && structure.windows != null)
        {
            foreach (var win in structure.windows)
            {
                if (win.isOccupied && win.occupant != null && !win.occupant.isDead)
                {
                    return true;
                }
            }
        }

        // Test géométrique rapide : y < 1.8m dans le rectangle du bâtiment
        Collider col = GetComponent<Collider>();
        if (col != null)
        {
            Bounds b = col.bounds;
            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && !u.isDead && !u.isTank && !u.isRooftopSniper)
                {
                    Vector3 p = u.transform.position;
                    if (p.y < b.max.y - 1.0f && p.x >= b.min.x - 0.2f && p.x <= b.max.x + 0.2f && p.z >= b.min.z - 0.2f && p.z <= b.max.z + 0.2f)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void UpdateVisibility()
    {
        EnsureComponents();
        if (roofRenderer == null) return;

        bool hasInteriorUnits = HasUnitsInsideBuilding();

        if (hasInteriorUnits)
        {
            if (currentState != RoofRenderState.CutawaySemiTransparent)
            {
                roofRenderer.enabled = true;
                if (cutawayMaterial == null) CreateCutawayMaterial();
                if (cutawayMaterial != null) roofRenderer.sharedMaterial = cutawayMaterial;
                if (roofCollider != null) roofCollider.enabled = true; // Plancher physique solide pour gérer le fantassin sur le toit
                currentState = RoofRenderState.CutawaySemiTransparent;
            }
        }
        else
        {
            if (currentState != RoofRenderState.Opaque)
            {
                roofRenderer.enabled = true;
                if (originalOpaqueMaterial != null) roofRenderer.sharedMaterial = originalOpaqueMaterial;
                if (roofCollider != null) roofCollider.enabled = true;
                currentState = RoofRenderState.Opaque;
            }
        }
    }
}
