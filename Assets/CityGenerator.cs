using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Unity.AI.Navigation;

public class CityGenerator : MonoBehaviour
{
    [Header("Settings")]
    public float latitude = 50.6927f;
    public float longitude = 3.1778f;
    public float radius = 250f;
    public float buildingHeight = 5f;
    public Material buildingMaterial;

    private void Start()
    {
        // Si le jeu se lance (Play), on s'assure de baker le vrai NavMesh (avec NavMeshSurface)
        // une fois que le Sol est téléchargé par MapTileLoader.
        if (Application.isPlaying)
        {
            // EXTRÊMEMENT IMPORTANT :
            // Si une ancienne version de "City" est sauvegardée dans la scène avec les anciens paramètres,
            // ses enfants ont toujours la case "Static" cochée. Cela provoque l'erreur "Combined Mesh".
            // On la supprime donc automatiquement pour regénérer une ville propre.
            GameObject oldCity = GameObject.Find("City");
            if (oldCity != null)
            {
                Destroy(oldCity);
            }

            // On lance la génération de la ville au démarrage. 
            // La méthode s'occupera d'attendre le sol, de baker le NavMesh, puis de libérer les unités !
            GenerateCity();
        }
    }

    [ContextMenu("Generate City")]
    public void GenerateCity()
    {
        // Nettoyer l'ancienne ville si on regénère via le menu
        GameObject oldCity = GameObject.Find("City");
        if (oldCity != null)
        {
            if (Application.isPlaying) Destroy(oldCity);
            else DestroyImmediate(oldCity);
        }
        
        StartCoroutine(FetchCityData());
    }

    private IEnumerator FetchCityData()
    {
        Debug.Log("Fetching city data from Overpass API...");
        
        // Utilisation de InvariantCulture pour forcer le point '.' comme séparateur décimal
        // Ajout de [timeout:90] pour laisser plus de temps au serveur sur les grosses requêtes (le défaut est court)
        string query = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[out:json][timeout:90];\n(\n  way[\"building\"](around:{0},{1},{2});\n  relation[\"building\"](around:{0},{1},{2});\n);\nout geom;",
            radius, latitude, longitude);

        // Utilisation d'un POST et d'un WWWForm pour gérer automatiquement l'encodage URL et les requêtes longues
        WWWForm form = new WWWForm();
        form.AddField("data", query);

        using (UnityWebRequest webRequest = UnityWebRequest.Post("https://overpass-api.de/api/interpreter", form))
        {
            webRequest.timeout = 90; // Empêcher Unity de couper la connexion trop tôt
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.ConnectionError || webRequest.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError("Error fetching City Data: " + webRequest.error);
                if (webRequest.downloadHandler != null)
                {
                    Debug.LogError("Overpass Response: " + webRequest.downloadHandler.text);
                }
            }
            else
            {
                Debug.Log("Data fetched successfully. Processing...");
                ProcessData(webRequest.downloadHandler.text);

                // 1. On attend que la carte de base (Sol) soit téléchargée par MapTileLoader
                GameObject sol = null;
                while (sol == null)
                {
                    sol = GameObject.Find("Sol");
                    yield return null;
                }
                MeshFilter mf = sol.GetComponent<MeshFilter>();
                while (mf == null || mf.sharedMesh == null)
                {
                    mf = sol.GetComponent<MeshFilter>();
                    yield return null;
                }

                // 2. TRÈS IMPORTANT : Attendre une frame pour que Unity enregistre les nouveaux Mesh/Colliders !
                yield return null;

                // 3. Auto-Bake NavMesh
                NavMeshSurface surface = FindFirstObjectByType<NavMeshSurface>();
                if (surface == null)
                {
                    surface = gameObject.AddComponent<NavMeshSurface>();
                }
                surface.BuildNavMesh();
                Debug.Log("NavMesh automatically baked and perfectly fitted around buildings!");

                // 4. LÂCHER LES CHIENS ! On notifie les unités qu'elles peuvent enfin bouger.
                UnitAI[] units = FindObjectsByType<UnitAI>(FindObjectsSortMode.None);
                Debug.Log($"[CityGenerator] Notifying {units.Length} UnitAIs that NavMesh is ready.");
                foreach (var unit in units)
                {
                    unit.OnNavMeshReady();
                }
            }
        }
    }

    private void ProcessData(string json)
    {
        OverpassResponse response = JsonUtility.FromJson<OverpassResponse>(json);
        if (response == null || response.elements == null)
        {
            Debug.LogError("Failed to parse Overpass JSON.");
            return;
        }

        GameObject cityRoot = new GameObject("City");

        foreach (var element in response.elements)
        {
            try
            {
                if (element.type == "way" && element.geometry != null && element.geometry.Length > 0)
                {
                    List<Vector2> footprint = new List<Vector2>();
                    foreach (var geo in element.geometry)
                    {
                        Vector3 pos = CoordinateToWorldPoint(geo.lat, geo.lon);
                        footprint.Add(new Vector2(pos.x, pos.z));
                    }
                    CreateBuildingObject(footprint, new List<List<Vector2>>(), "Building_" + element.id, cityRoot.transform);
                }
                else if (element.type == "relation" && element.members != null)
                {
                    List<List<Vector2>> outers = new List<List<Vector2>>();
                    List<List<Vector2>> inners = new List<List<Vector2>>();

                    foreach (var member in element.members)
                    {
                        if (member.type == "way" && member.geometry != null && member.geometry.Length > 0)
                        {
                            List<Vector2> ring = new List<Vector2>();
                            foreach (var geo in member.geometry)
                            {
                                Vector3 pos = CoordinateToWorldPoint(geo.lat, geo.lon);
                                ring.Add(new Vector2(pos.x, pos.z));
                            }

                            if (member.role == "outer")
                                outers.Add(ring);
                            else if (member.role == "inner")
                                inners.Add(ring);
                        }
                    }

                    foreach (var outer in outers)
                    {
                        CreateBuildingObject(outer, inners, "RelationBuilding_" + element.id, cityRoot.transform);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to generate building {element.id}: {e.Message}");
            }
        }
        
        Debug.Log("City generation completed!");
    }

    private void CreateBuildingObject(List<Vector2> outer, List<List<Vector2>> inners, string name, Transform parent)
    {
        // Clean footprint (remove duplicate last point)
        if (outer.Count > 0 && Vector2.Distance(outer[0], outer[outer.Count - 1]) < 0.1f)
        {
            outer.RemoveAt(outer.Count - 1);
        }
        
        if (outer.Count < 3) return;

        List<List<Vector2>> cleanInners = new List<List<Vector2>>();
        foreach (var inner in inners)
        {
            if (inner.Count > 0 && Vector2.Distance(inner[0], inner[inner.Count - 1]) < 0.1f)
            {
                inner.RemoveAt(inner.Count - 1);
            }
            if (inner.Count >= 3) cleanInners.Add(inner);
        }

        // Merge holes into a single polygon
        List<Vector2> mergedFootprint = MergeHoles(outer, cleanInners);
        
        // Ensure orientation is Clockwise (CW) for Unity (left-handed) so roof normals point UP
        EnsureOrientation(mergedFootprint, false); 

        // Triangulate
        List<int> roofIndices = Triangulate(mergedFootprint);
        if (roofIndices.Count == 0) return;

        // Build Mesh
        Mesh mesh = CreateBuildingMesh(mergedFootprint, roofIndices, buildingHeight);

        // Instantiate
        GameObject buildingGo = new GameObject(name);
        buildingGo.transform.parent = parent;

        MeshFilter mf = buildingGo.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        // SUPPRIMÉ : Ne pas utiliser isStatic = true !
        // Cela active le "Static Batching" d'Unity au lancement, ce qui verrouille la lecture des Meshes.
        // C'est ce qui causait l'erreur "Source mesh Combined Mesh does not allow read access"
        // et empêchait complètement le NavMesh de voir les bâtiments !
        // buildingGo.isStatic = true;

        MeshRenderer mr = buildingGo.AddComponent<MeshRenderer>();
        if (buildingMaterial != null)
        {
            mr.sharedMaterial = buildingMaterial;
        }
        else
        {
            // Fallback material setup for URP or Standard
            Shader defaultShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            mr.sharedMaterial = new Material(defaultShader);
        }

        MeshCollider mc = buildingGo.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        // Force NavMesh to carve a hole
        NavMeshModifier navMod = buildingGo.AddComponent<NavMeshModifier>();
        navMod.overrideArea = true;
        navMod.area = 1; // 1 = Not Walkable
    }

    private Mesh CreateBuildingMesh(List<Vector2> footprint, List<int> roofIndices, float height)
    {
        int numPoints = footprint.Count;
        int numWallQuads = numPoints;
        int totalVertices = numPoints * 2 + numWallQuads * 4;
        
        Vector3[] vertices = new Vector3[totalVertices];
        Vector2[] uvs = new Vector2[totalVertices];
        List<int> triangles = new List<int>();

        // 1. Toit (Roof) - Regarde vers le ciel
        for (int i = 0; i < numPoints; i++)
        {
            vertices[i] = new Vector3(footprint[i].x, height, footprint[i].y);
            uvs[i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        triangles.AddRange(roofIndices);

        // 2. Sol Interne (Floor) - Regarde AUSSI vers le ciel pour bloquer le scanner !
        int floorOffset = numPoints;
        for (int i = 0; i < numPoints; i++)
        {
            // IMPORTANT : Placé à Y = 1.0f (1 mètre) au-dessus du sol. 
            // Si on le met trop bas (0.05), le Voxelizer du NavMesh le fusionne avec le sol (-0.1)
            // car la résolution verticale (Cell Height) est souvent de 0.2m par défaut.
            // À 1.0m, il est bien détecté, et comme l'espace en dessous (1.1m) est inférieur à 
            // la hauteur de l'agent (2m), le NavMesh ne se générera PAS en dessous !
            vertices[floorOffset + i] = new Vector3(footprint[i].x, 1.0f, footprint[i].y);
            uvs[floorOffset + i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        // Copie exacte des triangles du toit pour être orienté vers le haut
        for (int i = 0; i < roofIndices.Count; i++)
        {
            triangles.Add(floorOffset + roofIndices[i]);
        }

        // 3. Murs (Walls)
        int vIndex = numPoints * 2;
        for (int i = 0; i < numPoints; i++)
        {
            int next = (i + 1) % numPoints;
            
            // On enfonce un peu les murs à -1m pour l'esthétique
            Vector3 p1 = new Vector3(footprint[i].x, -1f, footprint[i].y);
            Vector3 p2 = new Vector3(footprint[next].x, -1f, footprint[next].y);
            Vector3 p3 = new Vector3(footprint[i].x, height, footprint[i].y);
            Vector3 p4 = new Vector3(footprint[next].x, height, footprint[next].y);

            vertices[vIndex] = p1;
            vertices[vIndex + 1] = p2;
            vertices[vIndex + 2] = p3;
            vertices[vIndex + 3] = p4;

            float wallWidth = Vector3.Distance(p1, p2);
            uvs[vIndex] = new Vector2(0, 0);
            uvs[vIndex + 1] = new Vector2(wallWidth, 0);
            uvs[vIndex + 2] = new Vector2(0, height);
            uvs[vIndex + 3] = new Vector2(wallWidth, height);

            triangles.Add(vIndex); triangles.Add(vIndex + 3); triangles.Add(vIndex + 2);
            triangles.Add(vIndex); triangles.Add(vIndex + 1); triangles.Add(vIndex + 3);

            vIndex += 4;
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = totalVertices > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        return mesh;
    }

    private Vector3 CoordinateToWorldPoint(double lat, double lon)
    {
        double R = 6378137.0; // Earth radius in meters (Web Mercator)
        
        // Center coordinates in Web Mercator
        double centerLonRad = longitude * Math.PI / 180.0;
        double centerLatRad = latitude * Math.PI / 180.0;
        double centerX = R * centerLonRad;
        double centerY = R * Math.Log(Math.Tan(Math.PI / 4.0 + centerLatRad / 2.0));

        // Target coordinates in Web Mercator
        double lonRad = lon * Math.PI / 180.0;
        double latRad = lat * Math.PI / 180.0;
        double x = R * lonRad;
        double y = R * Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));

        // Scale to actual real-world meters at this latitude to avoid size distortion
        double scale = Math.Cos(latitude * Math.PI / 180.0);

        return new Vector3((float)((x - centerX) * scale), 0, (float)((y - centerY) * scale));
    }

    private List<Vector2> MergeHoles(List<Vector2> outer, List<List<Vector2>> holes)
    {
        if (holes == null || holes.Count == 0) return outer;

        List<Vector2> result = new List<Vector2>(outer);
        EnsureOrientation(result, false); // CW for outer

        foreach (var hole in holes)
        {
            EnsureOrientation(hole, true); // CCW for holes

            int holeVertexIdx = 0;
            float maxX = hole[0].x;
            for (int i = 1; i < hole.Count; i++)
            {
                if (hole[i].x > maxX)
                {
                    maxX = hole[i].x;
                    holeVertexIdx = i;
                }
            }
            Vector2 h = hole[holeVertexIdx];

            int outerVertexIdx = 0;
            float minDist = float.MaxValue;
            for (int i = 0; i < result.Count; i++)
            {
                float dist = Vector2.Distance(h, result[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    outerVertexIdx = i;
                }
            }

            // Bridge hole to outer polygon
            List<Vector2> newPolygon = new List<Vector2>();
            for (int i = 0; i <= outerVertexIdx; i++)
                newPolygon.Add(result[i]);

            for (int i = 0; i < hole.Count; i++)
                newPolygon.Add(hole[(holeVertexIdx + i) % hole.Count]);

            newPolygon.Add(hole[holeVertexIdx]);
            newPolygon.Add(result[outerVertexIdx]);

            for (int i = outerVertexIdx + 1; i < result.Count; i++)
                newPolygon.Add(result[i]);

            result = newPolygon;
        }

        return result;
    }

    private void EnsureOrientation(List<Vector2> p, bool ccw)
    {
        float area = Area(p);
        if (ccw && area < 0) p.Reverse();
        if (!ccw && area > 0) p.Reverse();
    }

    #region Ear Clipping Triangulation

    public static List<int> Triangulate(List<Vector2> points)
    {
        List<int> indices = new List<int>();
        int n = points.Count;
        if (n < 3) return indices;

        int[] V = new int[n];
        if (Area(points) > 0)
        {
            for (int v = 0; v < n; v++) V[v] = v;
        }
        else
        {
            for (int v = 0; v < n; v++) V[v] = (n - 1) - v;
        }

        int nv = n;
        int count = 2 * nv;
        for (int v = nv - 1; nv > 2;)
        {
            if (count-- <= 0) break; 

            int u = v;
            if (nv <= u) u = 0;
            v = u + 1;
            if (nv <= v) v = 0;
            int w = v + 1;
            if (nv <= w) w = 0;

            if (Snip(points, u, v, w, nv, V))
            {
                int a = V[u];
                int b = V[v];
                int c = V[w];
                
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
                
                for (int s = v, t = v + 1; t < nv; s++, t++) V[s] = V[t];
                nv--;
                count = 2 * nv;
            }
        }

        indices.Reverse(); // Unity front face is CW, Ear Clipping returns CCW
        return indices;
    }

    private static float Area(List<Vector2> points)
    {
        int n = points.Count;
        float A = 0.0f;
        for (int p = n - 1, q = 0; q < n; p = q++)
        {
            A += points[p].x * points[q].y - points[q].x * points[p].y;
        }
        return A * 0.5f;
    }

    private static bool Snip(List<Vector2> points, int u, int v, int w, int n, int[] V)
    {
        Vector2 A = points[V[u]];
        Vector2 B = points[V[v]];
        Vector2 C = points[V[w]];

        if (Mathf.Epsilon > (((B.x - A.x) * (C.y - A.y)) - ((B.y - A.y) * (C.x - A.x)))) return false;

        for (int p = 0; p < n; p++)
        {
            if ((p == u) || (p == v) || (p == w)) continue;
            Vector2 P = points[V[p]];
            if (InsideTriangle(A, B, C, P)) return false;
        }
        return true;
    }

    private static bool InsideTriangle(Vector2 A, Vector2 B, Vector2 C, Vector2 P)
    {
        float ax = C.x - B.x, ay = C.y - B.y;
        float bx = A.x - C.x, by = A.y - C.y;
        float cx = B.x - A.x, cy = B.y - A.y;
        float apx = P.x - A.x, apy = P.y - A.y;
        float bpx = P.x - B.x, bpy = P.y - B.y;
        float cpx = P.x - C.x, cpy = P.y - C.y;

        float aCROSSbp = ax * bpy - ay * bpx;
        float cCROSSap = cx * apy - cy * apx;
        float bCROSScp = bx * cpy - by * cpx;

        // Strict greater than allows zero-width bridges for multipolygons
        return ((aCROSSbp > 0.0f) && (bCROSScp > 0.0f) && (cCROSSap > 0.0f));
    }

    #endregion

    #region Data Classes

    [Serializable]
    private class OverpassResponse
    {
        public OverpassElement[] elements;
    }

    [Serializable]
    private class OverpassElement
    {
        public string type;
        public long id;
        public OverpassGeometry[] geometry; 
        public OverpassMember[] members; 
    }

    [Serializable]
    private class OverpassGeometry
    {
        public double lat;
        public double lon;
    }

    [Serializable]
    private class OverpassMember
    {
        public string type;
        public string role;
        public OverpassGeometry[] geometry;
    }

    #endregion
}
