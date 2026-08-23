using System.Collections.Generic;
using UnityEngine;

namespace Novgov.Generation
{
    /// <summary>
    /// Place du mobilier urbain procédural (lampadaires, bancs, poubelles, arbres) le long des façades
    /// extérieures des bâtiments générés par CityGenerator. Uniquement des primitives Unity natives et
    /// des matériaux mutualisés (SafeMaterialFactory) : aucun asset externe, cohérent avec le reste du
    /// pipeline de génération de ville.
    /// </summary>
    public static class StreetPropsGenerator
    {
        private const float MIN_EDGE_LENGTH_FOR_PROPS = 4.0f;
        private const float MIN_EDGE_LENGTH_FOR_MID_PROP = 8.0f;
        private const float DOOR_CLEARANCE = 1.2f;
        private const float CORNER_OFFSET = 1.5f;
        private const float OUTWARD_OFFSET = 1.4f; // distance depuis le mur, vers la rue

        private const float STREETLIGHT_CHANCE = 0.22f;
        private const float MID_PROP_CHANCE = 0.18f;

        private static Material metalMaterial;
        private static Material foliageMaterial;
        private static Material woodMaterial;

        private static Material MetalMaterial
        {
            get
            {
                if (metalMaterial == null) metalMaterial = SafeMaterialFactory.CreateLit(new Color(0.52f, 0.53f, 0.56f));
                return metalMaterial;
            }
        }

        private static Material FoliageMaterial
        {
            get
            {
                if (foliageMaterial == null) foliageMaterial = SafeMaterialFactory.CreateLit(new Color(0.16f, 0.32f, 0.14f));
                return foliageMaterial;
            }
        }

        private static Material WoodMaterial
        {
            get
            {
                if (woodMaterial == null) woodMaterial = SafeMaterialFactory.CreateLit(new Color(0.32f, 0.22f, 0.14f));
                return woodMaterial;
            }
        }

        public static void PlaceStreetProps(Transform buildingParent, List<Vector2> footprint, List<BuildingStructure.BuildingDoor> doors, List<(Vector2, Vector2)> partyWallEdges)
        {
            if (footprint == null || footprint.Count < 3) return;
            int n = footprint.Count;

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector2 p1 = footprint[i];
                Vector2 p2 = footprint[next];

                if (CityGenerator.IsPartyWallEdge(p1, p2, partyWallEdges)) continue; // mur mitoyen : pas de rue de ce côté

                float segLen = Vector2.Distance(p1, p2);
                if (segLen < MIN_EDGE_LENGTH_FOR_PROPS) continue;

                Vector2 tangent2D = (p2 - p1) / segLen;
                Vector2 outward2D = OutwardNormal(footprint, i, tangent2D);

                // Coin de façade (près de p1) : candidat lampadaire.
                float cornerDist = Mathf.Min(CORNER_OFFSET, segLen * 0.25f);
                if (!IsNearDoor(i, cornerDist, doors) && Random.value < STREETLIGHT_CHANCE)
                {
                    Vector2 pos2D = p1 + tangent2D * cornerDist + outward2D * OUTWARD_OFFSET;
                    CreateStreetLight(buildingParent, pos2D);
                }

                // Milieu de façade (façades assez longues) : candidat arbre / banc / poubelle.
                if (segLen >= MIN_EDGE_LENGTH_FOR_MID_PROP)
                {
                    float midDist = segLen * 0.5f;
                    if (!IsNearDoor(i, midDist, doors) && Random.value < MID_PROP_CHANCE)
                    {
                        Vector2 pos2D = p1 + tangent2D * midDist + outward2D * OUTWARD_OFFSET;
                        float pick = Random.value;
                        if (pick < 0.45f) CreateTree(buildingParent, pos2D);
                        else if (pick < 0.75f) CreateBench(buildingParent, pos2D, tangent2D);
                        else CreateTrashBin(buildingParent, pos2D);
                    }
                }
            }
        }

        private static Vector2 OutwardNormal(List<Vector2> footprint, int edgeIndex, Vector2 tangent2D)
        {
            Vector2 centroid = Vector2.zero;
            foreach (var p in footprint) centroid += p;
            centroid /= footprint.Count;

            Vector2 p1 = footprint[edgeIndex];
            Vector2 p2 = footprint[(edgeIndex + 1) % footprint.Count];
            Vector2 mid = (p1 + p2) * 0.5f;

            Vector2 normal = new Vector2(-tangent2D.y, tangent2D.x);
            Vector2 fromCentroid = (mid - centroid).normalized;
            if (Vector2.Dot(normal, fromCentroid) < 0) normal = -normal;
            return normal;
        }

        private static bool IsNearDoor(int edgeIndex, float distAlongEdge, List<BuildingStructure.BuildingDoor> doors)
        {
            if (doors == null) return false;
            foreach (var door in doors)
            {
                if (door.edgeIndex != edgeIndex) continue;
                float clearance = door.width * 0.5f + DOOR_CLEARANCE;
                if (Mathf.Abs(distAlongEdge - door.edgeDistance) < clearance) return true;
            }
            return false;
        }

        private static Transform NewPropRoot(Transform parent, string name, Vector2 pos2D)
        {
            GameObject go = new GameObject(name);
            go.transform.parent = parent;
            go.transform.position = new Vector3(pos2D.x, 0f, pos2D.y);
            return go.transform;
        }

        private static void StripDefaultCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col == null) return;
            if (Application.isPlaying) Object.Destroy(col);
            else Object.DestroyImmediate(col);
        }

        private static void CreateStreetLight(Transform parent, Vector2 pos2D)
        {
            Transform root = NewPropRoot(parent, "StreetLight", pos2D);

            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.parent = root;
            const float poleHeight = 4.0f;
            pole.transform.localPosition = new Vector3(0f, poleHeight * 0.5f, 0f);
            pole.transform.localScale = new Vector3(0.09f, poleHeight * 0.5f, 0.09f);
            pole.GetComponent<MeshRenderer>().sharedMaterial = MetalMaterial;
            StripDefaultCollider(pole);
            CapsuleCollider capsule = pole.AddComponent<CapsuleCollider>();
            capsule.height = 2f; // en unités locales non-scalées (le cylindre primitif fait 2 unités de haut)
            capsule.radius = 1f;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Lamp";
            head.transform.parent = root;
            head.transform.localPosition = new Vector3(0f, poleHeight, 0f);
            head.transform.localScale = Vector3.one * 0.28f;
            head.GetComponent<MeshRenderer>().sharedMaterial = MetalMaterial;
            StripDefaultCollider(head);
        }

        private static void CreateTree(Transform parent, Vector2 pos2D)
        {
            Transform root = NewPropRoot(parent, "Tree", pos2D);

            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.parent = root;
            const float trunkHeight = 2.4f;
            trunk.transform.localPosition = new Vector3(0f, trunkHeight * 0.5f, 0f);
            trunk.transform.localScale = new Vector3(0.14f, trunkHeight * 0.5f, 0.14f);
            trunk.GetComponent<MeshRenderer>().sharedMaterial = WoodMaterial;
            StripDefaultCollider(trunk);
            CapsuleCollider capsule = trunk.AddComponent<CapsuleCollider>();
            capsule.height = 2f;
            capsule.radius = 1f;

            int canopyCount = Random.value < 0.5f ? 1 : 2;
            for (int c = 0; c < canopyCount; c++)
            {
                GameObject canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                canopy.name = "Canopy";
                canopy.transform.parent = root;
                float radius = Random.Range(0.9f, 1.3f);
                Vector3 jitter = new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f));
                canopy.transform.localPosition = new Vector3(0f, trunkHeight + radius * 0.6f, 0f) + jitter;
                canopy.transform.localScale = new Vector3(radius * 1.6f, radius, radius * 1.6f);
                canopy.GetComponent<MeshRenderer>().sharedMaterial = FoliageMaterial;
                StripDefaultCollider(canopy); // purement décoratif, ne bloque pas le pathing
            }
        }

        private static void CreateBench(Transform parent, Vector2 pos2D, Vector2 tangent2D)
        {
            Transform root = NewPropRoot(parent, "Bench", pos2D);
            root.rotation = Quaternion.LookRotation(new Vector3(tangent2D.x, 0f, tangent2D.y), Vector3.up);

            GameObject seat = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seat.name = "Seat";
            seat.transform.parent = root;
            seat.transform.localPosition = new Vector3(0f, 0.22f, 0f);
            seat.transform.localScale = new Vector3(1.4f, 0.06f, 0.45f);
            seat.GetComponent<MeshRenderer>().sharedMaterial = WoodMaterial;

            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Backrest";
            back.transform.parent = root;
            back.transform.localPosition = new Vector3(0f, 0.42f, -0.2f);
            back.transform.localScale = new Vector3(1.4f, 0.35f, 0.06f);
            back.GetComponent<MeshRenderer>().sharedMaterial = WoodMaterial;
        }

        private static void CreateTrashBin(Transform parent, Vector2 pos2D)
        {
            Transform root = NewPropRoot(parent, "TrashBin", pos2D);

            GameObject bin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bin.name = "Bin";
            bin.transform.parent = root;
            bin.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            bin.transform.localScale = new Vector3(0.22f, 0.35f, 0.22f);
            bin.GetComponent<MeshRenderer>().sharedMaterial = MetalMaterial;
        }
    }
}
