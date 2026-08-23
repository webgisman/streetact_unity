using UnityEngine;
using UnityEngine.EventSystems;

namespace Novgov.Interaction
{
    /// <summary>
    /// Composant attaché à chaque fenêtre de bâtiment pour gérer la surbrillance (Ambre/Or)
    /// et le menu d'ordres tactiques pour le tir vers l'extérieur et la couverture lourde.
    /// </summary>
    public class WindowInteraction : MonoBehaviour
    {
        public BuildingStructure building;
        public BuildingStructure.BuildingWindow windowData;

        private MeshRenderer meshRenderer;
        private Material originalMaterial;
        private Material highlightMaterial;
        private bool isHighlighted = false;
        private static WindowInteraction currentHoveredWindow = null;

        public static WindowInteraction CurrentHoveredWindow => currentHoveredWindow;

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                originalMaterial = meshRenderer.sharedMaterial;
            }
        }

        public void Initialize(BuildingStructure parentBuilding, BuildingStructure.BuildingWindow data, Material defaultMat)
        {
            building = parentBuilding;
            windowData = data;
            originalMaterial = defaultMat;

            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null) meshRenderer.sharedMaterial = originalMaterial;

            // Création du matériau de surbrillance (Ambre / Or / Protection)
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Standard");

            if (shader == null)
            {
                // Aucun shader disponible (build Dedicated Server) — la surbrillance est purement
                // cosmétique, sans incidence sur la simulation, on l'ignore proprement.
                return;
            }

            highlightMaterial = new Material(shader);
            highlightMaterial.name = "Window_Highlight_Mat";

            Color amberHighlight = new Color(1f, 0.75f, 0.1f, 1f);
            if (highlightMaterial.HasProperty("_BaseColor")) highlightMaterial.SetColor("_BaseColor", amberHighlight);
            if (highlightMaterial.HasProperty("_Color")) highlightMaterial.SetColor("_Color", amberHighlight);

            if (highlightMaterial.HasProperty("_EmissionColor"))
            {
                highlightMaterial.EnableKeyword("_EMISSION");
                highlightMaterial.SetColor("_EmissionColor", amberHighlight * 1.5f);
            }
        }

        public void SetHighlight(bool active)
        {
            if (isHighlighted == active) return;
            isHighlighted = active;

            if (meshRenderer != null)
            {
                meshRenderer.sharedMaterial = active ? highlightMaterial : originalMaterial;
            }

            if (active) currentHoveredWindow = this;
            else if (currentHoveredWindow == this) currentHoveredWindow = null;
        }

        void OnDisable()
        {
            SetHighlight(false);
        }

        /// <summary>
        /// Position de tir à l'intérieur du bâtiment, juste derrière la fenêtre.
        /// </summary>
        public Vector3 GetInteriorStancePosition()
        {
            if (windowData == null) return transform.position;
            return windowData.position - windowData.outwardNormal * 0.4f;
        }
    }
}
