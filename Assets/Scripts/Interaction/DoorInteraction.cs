using UnityEngine;
using UnityEngine.EventSystems;

namespace StreetAct.Interaction
{
    /// <summary>
    /// Composant attaché à chaque porte de bâtiment pour gérer la surbrillance (Hover)
    /// et l'interaction de clic pour l'infiltration de l'infanterie.
    /// </summary>
    public class DoorInteraction : MonoBehaviour
    {
        public BuildingStructure building;
        public BuildingStructure.BuildingDoor doorData;

        private MeshRenderer meshRenderer;
        private Material originalMaterial;
        private Material highlightMaterial;
        private bool isHighlighted = false;
        private static DoorInteraction currentHoveredDoor = null;

        public static DoorInteraction CurrentHoveredDoor => currentHoveredDoor;

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                originalMaterial = meshRenderer.sharedMaterial;
            }
        }

        public void Initialize(BuildingStructure parentBuilding, BuildingStructure.BuildingDoor data, Material defaultMat)
        {
            building = parentBuilding;
            doorData = data;
            originalMaterial = defaultMat;

            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null) meshRenderer.sharedMaterial = originalMaterial;

            // Création du matériau de surbrillance (Cyan Holographique / Émissif)
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Standard");
            
            highlightMaterial = new Material(shader);
            highlightMaterial.name = "Door_Highlight_Mat";
            
            Color cyanHighlight = new Color(0f, 0.9f, 1f, 1f);
            if (highlightMaterial.HasProperty("_BaseColor")) highlightMaterial.SetColor("_BaseColor", cyanHighlight);
            if (highlightMaterial.HasProperty("_Color")) highlightMaterial.SetColor("_Color", cyanHighlight);
            
            if (highlightMaterial.HasProperty("_EmissionColor"))
            {
                highlightMaterial.EnableKeyword("_EMISSION");
                highlightMaterial.SetColor("_EmissionColor", cyanHighlight * 1.5f);
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

            if (active) currentHoveredDoor = this;
            else if (currentHoveredDoor == this) currentHoveredDoor = null;
        }

        void OnDisable()
        {
            SetHighlight(false);
        }

        /// <summary>
        /// Position au seuil extérieur de la porte (côté rue).
        /// </summary>
        public Vector3 GetOutsidePosition()
        {
            if (doorData == null) return transform.position;
            return doorData.position + doorData.entryDirection * 1.2f;
        }

        /// <summary>
        /// Position à l'intérieur du bâtiment après avoir franchi la porte.
        /// </summary>
        public Vector3 GetInsidePosition()
        {
            if (doorData == null) return transform.position;
            return doorData.position - doorData.entryDirection * 1.5f + Vector3.up * 0.05f;
        }
    }
}
