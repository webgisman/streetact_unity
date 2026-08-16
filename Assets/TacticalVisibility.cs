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
        // En vue 2D Commandement, aucun calcul de toiture n'est nécessaire
        if (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState != CameraStateManager.CameraState.Action)
        {
            return;
        }

        // Si le toit n'est pas activé par le streaming 3D local, ne rien calculer
        if (roofRenderer == null || !roofRenderer.enabled) return;

        checkTimer -= Time.deltaTime;
        if (checkTimer <= 0f)
        {
            checkTimer = 0.5f; // Scan allégé 2 fois par seconde uniquement sur les bâtiments 3D proches
            UpdateVisibility();
        }
    }

    public bool HasUnitsInsideBuilding()
    {
        if (isPlanificationPreview) return true;
        if (structure != null && structure.IsAnyUnitInside()) return true;

        if (structure != null && structure.windows != null)
        {
            for (int i = 0; i < structure.windows.Count; i++)
            {
                var win = structure.windows[i];
                if (win.isOccupied && win.occupant != null && !win.occupant.isDead)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void UpdateVisibility()
    {
        // En vue 2D, ne jamais forcer l'affichage du toit 3D
        if (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState != CameraStateManager.CameraState.Action)
        {
            return;
        }

        EnsureComponents();
        if (roofRenderer == null || !roofRenderer.enabled) return;

        bool hasInteriorUnits = HasUnitsInsideBuilding();

        if (hasInteriorUnits)
        {
            if (currentState != RoofRenderState.CutawaySemiTransparent)
            {
                if (cutawayMaterial == null) CreateCutawayMaterial();
                if (cutawayMaterial != null) roofRenderer.sharedMaterial = cutawayMaterial;
                if (roofCollider != null) roofCollider.enabled = true;
                currentState = RoofRenderState.CutawaySemiTransparent;
            }
        }
        else
        {
            if (currentState != RoofRenderState.Opaque)
            {
                if (originalOpaqueMaterial != null) roofRenderer.sharedMaterial = originalOpaqueMaterial;
                if (roofCollider != null) roofCollider.enabled = true;
                currentState = RoofRenderState.Opaque;
            }
        }
    }
}
