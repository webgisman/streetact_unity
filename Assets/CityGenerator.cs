using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using StreetAct.Core;
using StreetAct.Generation;
using UnityEngine.Networking;
using UnityEngine.AI;
using Unity.AI.Navigation;

public class CityGenerator : MonoBehaviour
{
    [Header("Settings")]
    public float latitude = 50.6927f;
    public float longitude = 3.1778f;
    public float radius = 250f;
    public float buildingHeight = 6f; // Hauteur moyenne des bâtiments (variation aléatoire de ±1.5m par lot)
    public Material buildingMaterial;

    // Référence de la génération actuellement en cours (réseau OU hors-ligne). Sans ce suivi, un appel
    // à GenerateCity()/LoadDefaultOfflineCity() pendant que la génération par défaut du Start() est
    // encore en vol (ex : le joueur choisit le GPS ou une ville hors-ligne dans le sélecteur de démarrage
    // avant que la requête Overpass par défaut n'ait fini) laisse DEUX villes se construire en parallèle
    // sur des coordonnées différentes -> bâtiments dupliqués/décalés par rapport au sol OSM téléchargé
    // pour l'autre position. On annule systématiquement toute génération en cours avant d'en lancer une nouvelle.
    private Coroutine activeGeneration;

    private void Start()
    {
        // Si le jeu se lance (Play), on s'assure de baker le vrai NavMesh (avec NavMeshSurface)
        // une fois que le Sol est téléchargé par MapTileLoader.
        if (Application.isPlaying)
        {
            // On lance la génération de la ville au démarrage.
            // La méthode s'occupera d'attendre le sol, de baker le NavMesh, puis de libérer les unités !
            GenerateCity();
        }
    }

    // Annule la génération en cours (s'il y en a une) et nettoie la ville précédente. À appeler avant
    // de démarrer toute nouvelle génération, réseau ou hors-ligne.
    private void CancelActiveGenerationAndClearCity()
    {
        if (activeGeneration != null)
        {
            StopCoroutine(activeGeneration);
            activeGeneration = null;
        }

        // EXTRÊMEMENT IMPORTANT :
        // Si une ancienne version de "City" est sauvegardée dans la scène avec les anciens paramètres,
        // ses enfants ont toujours la case "Static" cochée. Cela provoque l'erreur "Combined Mesh".
        // On la supprime donc automatiquement pour regénérer une ville propre.
        GameObject oldCity = GameObject.Find("City");
        if (oldCity != null)
        {
            if (Application.isPlaying) Destroy(oldCity);
            else DestroyImmediate(oldCity);
        }
    }

    [ContextMenu("Generate City")]
    public void GenerateCity()
    {
        CancelActiveGenerationAndClearCity();
        activeGeneration = StartCoroutine(FetchCityData());
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
        
        // Initialiser la projection globale pour garantir l'alignement avec MapTileLoader
        GeoProjection.SetCenter(latitude, longitude);

        WWWForm form = new WWWForm();

        form.AddField("data", query);

        string[] endpoints = new string[] {
            "https://overpass.openstreetmap.fr/api/interpreter", // Fast for France
            "https://lz4.overpass-api.de/api/interpreter",
            "https://overpass-api.de/api/interpreter"
        };

        bool success = false;
        string jsonText = "";

        foreach (string endpoint in endpoints)
        {
            Debug.Log($"Trying Overpass API endpoint: {endpoint}");
            using (UnityWebRequest webRequest = UnityWebRequest.Post(endpoint, form))
            {
                // Doit rester supérieur au [timeout:90] de la requête Overpass, sinon Unity abandonne
                // côté client avant que le serveur ait fini de répondre sur les grosses zones.
                webRequest.timeout = 100;
                webRequest.SetRequestHeader("User-Agent", "UnityTacticalGame/1.0");
                yield return webRequest.SendWebRequest();

                if (webRequest.result == UnityWebRequest.Result.ConnectionError || webRequest.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogWarning($"[CityGenerator] Endpoint {endpoint} failed: {webRequest.error}");
                    continue; // Try next endpoint
                }
                else
                {
                    jsonText = webRequest.downloadHandler.text;
                    success = true;
                    break;
                }
            }
        }

        if (!success)
        {
            Debug.LogWarning("[CityGenerator] Tous les serveurs OSM sont indisponibles. Tentative de récupération depuis le cache disque ou ville de secours.");
            LoadDefaultOfflineCity();
        }
        else
        {
                Debug.Log("Data fetched successfully. Processing...");

                // Sauvegarde automatique du cache disque local pour réutilisation hors-ligne
                string cacheFileName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "CityCache_{0:F4}_{1:F4}_{2:F0}.json", latitude, longitude, radius);
                string cachePath = System.IO.Path.Combine(Application.persistentDataPath, cacheFileName);
                try
                {
                    System.IO.File.WriteAllText(cachePath, jsonText);
                    Debug.Log($"[CityGenerator] 💾 Données de la carte sauvegardées dans le cache local : {cachePath}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[CityGenerator] Erreur d'écriture du cache : {ex.Message}");
                }

                yield return ProcessDataCoroutine(jsonText);

                // 1. On attend que la carte de base (Sol) soit VRAIMENT téléchargée et générée par MapTileLoader
                MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
                if (mapLoader != null)
                {
                    Debug.Log("[CityGenerator] En attente du chargement de la carte par MapTileLoader...");
                    while (!mapLoader.isMapLoaded)
                    {
                        yield return null;
                    }
                }
                else
                {
                    GameObject sol = null;
                    while (sol == null)
                    {
                        sol = GameObject.Find("Sol");
                        yield return null;
                    }
                }

                // 2. TRÈS IMPORTANT : Attendre 2 frames pour que Unity enregistre tous les nouveaux Meshes et Colliders
                yield return null;
                yield return null;

                // 3. Auto-Bake NavMesh
                NavMeshSurface surface = FindAnyObjectByType<NavMeshSurface>();
                if (surface == null)
                {
                    surface = gameObject.AddComponent<NavMeshSurface>();
                }
                
                // --- CRITIQUE --- Désactiver temporairement les unités pour NE PAS les "cuire" dans le NavMesh
                UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Include);
                foreach(var unit in allUnits) { if (unit != null) unit.gameObject.SetActive(false); }
                yield return null; // Laisser 1 frame à Unity pour désactiver les colliders
                
                // Vider les anciennes données pour forcer un rebake propre
                surface.RemoveData();
                surface.collectObjects = CollectObjects.All;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                surface.defaultArea = 0; // Walkable
                surface.BuildNavMesh();
                Debug.Log("NavMesh automatically baked and perfectly fitted around building colliders!");

                // Enregistrer immédiatement les bâtiments pour le streaming 3D
                if (TacticalStreamingManager.Instance != null)
                {
                    TacticalStreamingManager.Instance.RegisterAllBuildings();
                }

                // 4. LÂCHER LES CHIENS ! On notifie les unités qu'elles peuvent enfin bouger.
                foreach(var unit in allUnits) { if (unit != null) unit.gameObject.SetActive(true); }
                yield return null; // Laisser 1 frame pour la réactivation
                
                Debug.Log($"[CityGenerator] Notifying {allUnits.Length} UnitAIs that NavMesh is ready.");
                foreach (var unit in allUnits)
                {
                    if (unit != null) unit.OnNavMeshReady();
                }
                
                GameManagerUI.OptimizeSceneMaterials();
                if (GameManagerUI.Instance != null) GameManagerUI.Instance.HideLoading();
        }
    }

    public void LoadDefaultOfflineCity()
    {
        // Peut être appelée en secours depuis FetchCityData (dont la coroutine se termine juste après)
        // ou directement depuis l'UI (bouton "Combat Urbain Hors-Ligne") : dans les deux cas on s'assure
        // qu'aucune autre génération ne continue en parallèle et ne vienne se superposer à celle-ci.
        CancelActiveGenerationAndClearCity();

        // 1. Vérifier si un cache local persistant existe sur le disque
        string cacheFileName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "CityCache_{0:F4}_{1:F4}_{2:F0}.json", latitude, longitude, radius);
        string cachePath = System.IO.Path.Combine(Application.persistentDataPath, cacheFileName);

        if (System.IO.File.Exists(cachePath))
        {
            try
            {
                string cachedJson = System.IO.File.ReadAllText(cachePath);
                if (!string.IsNullOrEmpty(cachedJson) && cachedJson.Length > 20)
                {
                    Debug.Log($"<color=green>[CityGenerator] 📂 Ville restaurée avec succès depuis le cache disque local : {cacheFileName}</color>");
                    activeGeneration = StartCoroutine(ProcessOfflineData(cachedJson));
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CityGenerator] Impossible de lire le cache disque : {ex.Message}");
            }
        }

        // 2. Fallback sur la ville par défaut intégrée dans Resources
        TextAsset jsonAsset = Resources.Load<TextAsset>("DefaultCityData");
        if (jsonAsset != null)
        {
            Debug.Log("[CityGenerator] Chargement de la ville de secours depuis le package Resources...");
            activeGeneration = StartCoroutine(ProcessOfflineData(jsonAsset.text));
        }
        else
        {
            Debug.LogError("Le fichier DefaultCityData n'a pas été trouvé dans le dossier Resources !");
        }
    }

    private IEnumerator ProcessOfflineData(string json)
    {
        
        GeoProjection.SetCenter(latitude, longitude);
        yield return ProcessDataCoroutine(json);

        
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
        if (mapLoader != null)
        {
            while (!mapLoader.isMapLoaded) yield return null;
        }

        yield return null;
        yield return null;

        NavMeshSurface surface = FindAnyObjectByType<NavMeshSurface>();
        if (surface == null) surface = gameObject.AddComponent<NavMeshSurface>();
        
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Include);
        foreach(var unit in allUnits) { if (unit != null) unit.gameObject.SetActive(false); }
        yield return null;
        
        surface.RemoveData();
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.defaultArea = 0;
        surface.BuildNavMesh();
        Debug.Log("NavMesh automatiquement généré pour le mode Hors-Ligne !");

        if (TacticalStreamingManager.Instance != null)
        {
            TacticalStreamingManager.Instance.RegisterAllBuildings();
        }

        foreach(var unit in allUnits) { if (unit != null) unit.gameObject.SetActive(true); }
        yield return null;
        
        foreach (var unit in allUnits) { if (unit != null) unit.OnNavMeshReady(); }
        
        if (GameManagerUI.Instance != null) GameManagerUI.Instance.HideLoading();
    }

    // Nombre d'éléments OSM traités entre deux "yield return null". Étale le travail de génération
    // (triangulation, meshes, colliders) sur plusieurs frames pour ne jamais geler l'écran de
    // chargement, même sur une zone dense de plusieurs centaines de bâtiments.
    private const int BUILDINGS_PER_FRAME = 8;

    private IEnumerator ProcessDataCoroutine(string json)
    {
        OverpassResponse response = JsonUtility.FromJson<OverpassResponse>(json);
        if (response == null || response.elements == null)
        {
            Debug.LogError("Failed to parse Overpass JSON.");
            yield break;
        }

        GameObject cityRoot = new GameObject("City");

        int elementsTotal = response.elements.Length;
        int buildingsCreated = 0;
        int elementsFailed = 0;
        int processedSinceYield = 0;

        foreach (var element in response.elements)
        {
            try
            {
                if (element.type == "way" && element.geometry != null && element.geometry.Length > 0)
                {
                    List<Vector2> footprint = new List<Vector2>();
                    foreach (var geo in element.geometry)
                    {
                        Vector3 pos = GeoProjection.CoordinateToWorldPoint(geo.lat, geo.lon);
                        footprint.Add(new Vector2(pos.x, pos.z));
                    }
                    buildingsCreated += CreateBuildingObject(footprint, new List<List<Vector2>>(), "Building_" + element.id, cityRoot.transform);
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
                                Vector3 pos = GeoProjection.CoordinateToWorldPoint(geo.lat, geo.lon);
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
                        buildingsCreated += CreateBuildingObject(outer, inners, "RelationBuilding_" + element.id, cityRoot.transform);
                    }
                }
            }
            catch (Exception e)
            {
                elementsFailed++;
                Debug.LogWarning($"Failed to generate building {element.id}: {e.Message}");
            }

            if (++processedSinceYield >= BUILDINGS_PER_FRAME)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }

        string summary = $"[CityGenerator] Génération terminée : {elementsTotal} éléments OSM reçus -> {buildingsCreated} bâtiments créés";
        if (elementsFailed > 0) summary += $", {elementsFailed} en échec (voir warnings ci-dessus)";
        Debug.Log($"<color=cyan>{summary}.</color>");

        // Diagnostic d'alignement : à comparer avec la ligne "[MapTileLoader] 📍 Coins du sol" pour
        // détecter un décalage entre le fond de carte (raster) et les bâtiments (vecteur OSM).
        Bounds? cityBounds = null;
        foreach (Transform lot in cityRoot.transform)
        {
            Transform footprint = lot.Find("Footprint_2D");
            Renderer footprintRenderer = footprint != null ? footprint.GetComponent<Renderer>() : null;
            if (footprintRenderer == null) continue;

            if (cityBounds == null) cityBounds = footprintRenderer.bounds;
            else { Bounds b = cityBounds.Value; b.Encapsulate(footprintRenderer.bounds); cityBounds = b; }
        }
        if (cityBounds.HasValue)
        {
            Bounds b = cityBounds.Value;
            Debug.Log($"<color=yellow>[CityGenerator] 📍 Zone couverte par les bâtiments : X[{b.min.x:F1} , {b.max.x:F1}] Z[{b.min.z:F1} , {b.max.z:F1}]</color>");
        }
    }


    /// <returns>Le nombre de lots effectivement instanciés en GameObjects (pour le diagnostic de couverture de la carte).</returns>
    private int CreateBuildingObject(List<Vector2> outer, List<List<Vector2>> inners, string name, Transform parent)
    {
        // Clean footprint (remove duplicate last point)
        if (outer.Count > 0 && Vector2.Distance(outer[0], outer[outer.Count - 1]) < 0.1f)
        {
            outer.RemoveAt(outer.Count - 1);
        }

        if (outer.Count < 3)
        {
            Debug.LogWarning($"[CityGenerator] Bâtiment ignoré (empreinte OSM dégénérée, < 3 sommets) : {name}");
            return 0;
        }

        List<List<Vector2>> cleanInners = new List<List<Vector2>>();
        foreach (var inner in inners)
        {
            if (inner.Count > 0 && Vector2.Distance(inner[0], inner[inner.Count - 1]) < 0.1f)
            {
                inner.RemoveAt(inner.Count - 1);
            }
            if (inner.Count >= 3) cleanInners.Add(inner);
        }

        // --- NEW: Inset ---
        List<Vector2> insetOuter = InsetPolygon(outer, 0.15f);
        if (insetOuter.Count < 3) insetOuter = outer; // fallback if inset fails

        // Merge holes into a single polygon
        List<Vector2> mergedFootprint = MergeHoles(insetOuter, cleanInners);
        
        // Ensure orientation is Clockwise (CW) for Unity (left-handed) so roof normals point UP
        EnsureOrientation(mergedFootprint, false); 

        // --- NEW: Subdivide large blocks into individual row houses ---
        // ONLY convex polygons will be split to prevent garbage geometry.
        // We also rely on 'building:part' from Overpass for complex buildings.
        List<List<Vector2>> subLots = BuildingSubdivider.Subdivide(mergedFootprint, out List<(Vector2, Vector2)> partyWallEdges);

        int lotIndex = 0;
        int createdCount = 0;
        foreach (var lotFootprint in subLots)
        {
            EnsureOrientation(lotFootprint, false);
            
            // Randomize height slightly per subdivided lot (autour de buildingHeight) to break the block effect
            float lotHeight = UnityEngine.Random.Range(buildingHeight - 1.5f, buildingHeight + 1.5f);

            // Triangulate
            List<int> roofIndices = Triangulate(lotFootprint);
            if (roofIndices.Count == 0)
            {
                Debug.LogWarning($"[CityGenerator] Lot ignoré (triangulation impossible, géométrie dégénérée) : {name}_Lot{lotIndex}");
                lotIndex++;
                continue;
            }

            // Instantiate Root
            string lotName = subLots.Count > 1 ? $"{name}_Lot{lotIndex}" : name;
            if (string.IsNullOrEmpty(name)) lotName = $"Batiment_{lotFootprint[0]}";
            
            GameObject buildingGo = new GameObject(lotName);
            buildingGo.transform.parent = parent;

            BuildingStructure structure = buildingGo.AddComponent<BuildingStructure>();
            structure.InitPolygon(lotFootprint, lotHeight);
            buildingGo.AddComponent<DestructibleEnvironment>();
            
            // Pre-calculate doors so we can make gaps in the walls
            GenerateDoorsAndWindows(buildingGo, structure, lotFootprint, lotHeight, partyWallEdges);

            // Mobilier urbain (lampadaires, bancs, poubelles, arbres) le long des façades extérieures.
            StreetPropsGenerator.PlaceStreetProps(buildingGo.transform, lotFootprint, structure.doors, partyWallEdges);

            // --- Create Sub-Meshes ---
            
            // 1. ROOF
            Mesh roofMesh = CreateRoofMesh(lotFootprint, roofIndices, lotHeight);
            GameObject roofGo = new GameObject("Roof");
            roofGo.transform.parent = buildingGo.transform;
            roofGo.AddComponent<MeshFilter>().sharedMesh = roofMesh;
            var roofRenderer = roofGo.AddComponent<MeshRenderer>();
            roofRenderer.sharedMaterial = GetRandomBuildingMaterial();
            roofRenderer.enabled = false; // Désactivé par défaut pour le Streaming
            roofGo.AddComponent<MeshCollider>().sharedMesh = roofMesh;

            // 2. POLYGONE 2D AU SOL (TOUJOURS VISIBLE POUR LA VUE CARTE 2D)
            Mesh floorMesh = CreateFloorMesh(lotFootprint, roofIndices, 0.08f);
            GameObject floorGo = new GameObject("Footprint_2D");
            floorGo.transform.parent = buildingGo.transform;
            floorGo.AddComponent<MeshFilter>().sharedMesh = floorMesh;
            var floorRenderer = floorGo.AddComponent<MeshRenderer>();
            floorRenderer.sharedMaterial = Get2DBuildingMaterial();
            floorRenderer.enabled = true; // Actif pour la construction 2D des polygones
            floorGo.AddComponent<MeshCollider>().sharedMesh = floorMesh;

            // 3. WALLS
            Mesh wallsMesh = CreateWallsMesh(lotFootprint, lotHeight, structure.doors);
            GameObject wallsGo = new GameObject("Walls");
            wallsGo.transform.parent = buildingGo.transform;
            wallsGo.AddComponent<MeshFilter>().sharedMesh = wallsMesh;
            var wallsRenderer = wallsGo.AddComponent<MeshRenderer>();
            wallsRenderer.sharedMaterial = GetRandomBuildingMaterial();
            wallsRenderer.enabled = false; // Désactivé par défaut pour le Streaming
            wallsGo.AddComponent<MeshCollider>().sharedMesh = wallsMesh;

            // Visual Openings
            BuildVisualOpenings(buildingGo, structure);
            
            // --- Tactical Visibility ---
            TacticalVisibility vis = buildingGo.AddComponent<TacticalVisibility>();
            vis.roofObject = roofGo;
            vis.structure = structure;

            // --- Roof Access (Ladder) ---
            CreateRoofAccess(buildingGo, lotFootprint, lotHeight);

            lotIndex++;
            createdCount++;
        }

        return createdCount;
    }

    private List<Vector2> InsetPolygon(List<Vector2> polygon, float insetAmount)
    {
        // First, ensure orientation is consistently Clockwise (so normals are predictable)
        EnsureOrientation(polygon, false);

        List<Vector2> insetPoly = new List<Vector2>();
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = polygon[(i - 1 + n) % n];
            Vector2 curr = polygon[i];
            Vector2 next = polygon[(i + 1) % n];

            Vector2 dir1 = (curr - prev).normalized;
            Vector2 dir2 = (next - curr).normalized;
            
            // For CW polygon, (dir.y, -dir.x) points INWARD
            // Let's verify: going UP (0,1), inward is RIGHT (1,0). (dir.y, -dir.x) = (1, 0). Correct!
            Vector2 norm1 = new Vector2(dir1.y, -dir1.x);
            Vector2 norm2 = new Vector2(dir2.y, -dir2.x);

            Vector2 sumNorm = (norm1 + norm2).normalized;
            float dot = Vector2.Dot(norm1, sumNorm);
            if (dot < 0.1f) dot = 0.1f; // Prevent division by zero or extreme spikes
            Vector2 offset = sumNorm * (insetAmount / dot);

            insetPoly.Add(curr + offset);
        }
        return insetPoly;
    }

    private static Material shared2DBuildingMaterial;

    private Material Get2DBuildingMaterial()
    {
        if (shared2DBuildingMaterial == null)
        {
            // Matériau 2D haute lisibilité pour le plan tactique (Ardoise tactique contrastée)
            shared2DBuildingMaterial = SafeMaterialFactory.CreateUnlit(new Color(0.24f, 0.30f, 0.38f, 0.95f));
        }
        return shared2DBuildingMaterial;
    }

    // Pool partagé de matériaux de façade. Un Material tout neuf par bâtiment (ancien comportement)
    // rend le GPU Instancing/batching inopérant : même avec enableInstancing=true, Unity ne regroupe
    // en un seul draw call que les renderers qui pointent vers EXACTEMENT le même Material. Avec des
    // centaines de bâtiments, on se retrouvait avec autant de draw calls. Ici, on réutilise un petit
    // nombre de teintes pré-générées afin que la grande majorité des murs/toits partagent la même
    // référence de matériau et soient réellement instanciés ensemble.
    private const int BUILDING_MATERIAL_PALETTE_SIZE = 16;
    private static Material[] sharedBuildingMaterialPalette;

    private Material GetRandomBuildingMaterial()
    {
        if (buildingMaterial != null) return buildingMaterial;

        if (sharedBuildingMaterialPalette == null)
        {
            sharedBuildingMaterialPalette = new Material[BUILDING_MATERIAL_PALETTE_SIZE];
            for (int i = 0; i < BUILDING_MATERIAL_PALETTE_SIZE; i++)
            {
                Color col = UnityEngine.Random.ColorHSV(0f, 1f, 0.1f, 0.3f, 0.4f, 0.8f);
                sharedBuildingMaterialPalette[i] = SafeMaterialFactory.CreateLit(col);
            }
        }

        return sharedBuildingMaterialPalette[UnityEngine.Random.Range(0, BUILDING_MATERIAL_PALETTE_SIZE)];
    }

    private void CreateRoofAccess(GameObject building, List<Vector2> footprint, float height)
    {
        // Simple ladder creation at the back of the building
        if (footprint.Count < 3) return;
        
        // Find longest edge for back face
        float maxLen = 0;
        Vector2 bestP1 = Vector2.zero, bestP2 = Vector2.zero;
        for (int i=0; i<footprint.Count; i++)
        {
            Vector2 p1 = footprint[i];
            Vector2 p2 = footprint[(i+1)%footprint.Count];
            float len = Vector2.Distance(p1, p2);
            if (len > maxLen)
            {
                maxLen = len;
                bestP1 = p1;
                bestP2 = p2;
            }
        }
        
        Vector2 mid = (bestP1 + bestP2) * 0.5f;
        Vector3 pos = new Vector3(mid.x, 0, mid.y);
        
        GameObject linkGo = new GameObject("NavMeshLink_Ladder");
        linkGo.transform.parent = building.transform;
        linkGo.transform.position = pos;
        
        var link = linkGo.AddComponent<Unity.AI.Navigation.NavMeshLink>();
        link.startPoint = new Vector3(0, 0.1f, 0);
        link.endPoint = new Vector3(0, height + 0.1f, 0);
        link.width = 1.0f;
        link.bidirectional = true;
    }

    private Mesh CreateRoofMesh(List<Vector2> footprint, List<int> roofIndices, float height)
    {
        int numPoints = footprint.Count;
        Vector3[] vertices = new Vector3[numPoints * 2]; // Top and Bottom face for thickness
        Vector2[] uvs = new Vector2[numPoints * 2];
        List<int> triangles = new List<int>();

        for (int i = 0; i < numPoints; i++)
        {
            vertices[i] = new Vector3(footprint[i].x, height, footprint[i].y);
            uvs[i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        triangles.AddRange(roofIndices);

        int offset = numPoints;
        for (int i = 0; i < numPoints; i++)
        {
            vertices[offset + i] = new Vector3(footprint[i].x, height - 0.2f, footprint[i].y);
            uvs[offset + i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        for (int i = roofIndices.Count - 1; i >= 0; i--)
        {
            triangles.Add(offset + roofIndices[i]);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    private Mesh CreateFloorMesh(List<Vector2> footprint, List<int> roofIndices, float yPos)
    {
        int numPoints = footprint.Count;
        Vector3[] vertices = new Vector3[numPoints];
        Vector2[] uvs = new Vector2[numPoints];
        
        for (int i = 0; i < numPoints; i++)
        {
            vertices[i] = new Vector3(footprint[i].x, yPos, footprint[i].y);
            uvs[i] = new Vector2(footprint[i].x, footprint[i].y);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = roofIndices.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    // Hauteur du linteau : doit correspondre au sommet du quad visuel de porte (BuildVisualOpenings: centre 1.1m, demi-hauteur 1.1m).
    private const float DOOR_OPENING_TOP = 2.2f;

    private Mesh CreateWallsMesh(List<Vector2> footprint, float height, List<BuildingStructure.BuildingDoor> doors)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        int numPoints = footprint.Count;
        for (int i = 0; i < numPoints; i++)
        {
            int next = (i + 1) % numPoints;
            Vector2 p1 = footprint[i];
            Vector2 p2 = footprint[next];
            float segLen = Vector2.Distance(p1, p2);

            List<BuildingStructure.BuildingDoor> edgeDoors = null;
            if (doors != null)
            {
                foreach (var d in doors)
                {
                    if (d.edgeIndex != i) continue;
                    if (edgeDoors == null) edgeDoors = new List<BuildingStructure.BuildingDoor>();
                    edgeDoors.Add(d);
                }
            }

            if (edgeDoors == null)
            {
                AddWallSegment(vertices, uvs, triangles, p1, p2, -2f, height);
                continue;
            }

            edgeDoors.Sort((a, b) => a.edgeDistance.CompareTo(b.edgeDistance));

            float cursor = 0f;
            foreach (var door in edgeDoors)
            {
                float doorHalf = door.width * 0.5f;
                float doorStart = Mathf.Clamp(door.edgeDistance - doorHalf, 0f, segLen);
                float doorEnd = Mathf.Clamp(door.edgeDistance + doorHalf, cursor, segLen);
                if (doorEnd <= cursor) continue; // Portes trop proches / chevauchantes : on garde le mur plein ici

                // Pan de mur plein avant l'ouverture
                AddWallSubSegment(vertices, uvs, triangles, p1, p2, segLen, cursor, doorStart, -2f, height);
                // Soubassement scellé sous le seuil (empêche tout cheminement sous le bâtiment)
                AddWallSubSegment(vertices, uvs, triangles, p1, p2, segLen, doorStart, doorEnd, -2f, 0f);
                // Linteau au-dessus de l'ouverture
                AddWallSubSegment(vertices, uvs, triangles, p1, p2, segLen, doorStart, doorEnd, DOOR_OPENING_TOP, height);

                cursor = doorEnd;
            }
            // Pan de mur plein après la dernière porte
            AddWallSubSegment(vertices, uvs, triangles, p1, p2, segLen, cursor, segLen, -2f, height);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    private void AddWallSubSegment(List<Vector3> vertices, List<Vector2> uvs, List<int> tris, Vector2 p1, Vector2 p2, float segLen, float xStart, float xEnd, float yBottom, float yTop)
    {
        if (xEnd - xStart < 0.02f || yTop - yBottom < 0.02f || segLen < 0.0001f) return;
        Vector2 dir = (p2 - p1) / segLen;
        Vector2 a = p1 + dir * xStart;
        Vector2 b = p1 + dir * xEnd;
        AddWallSegment(vertices, uvs, tris, a, b, yBottom, yTop);
    }

    private void AddWallSegment(List<Vector3> vertices, List<Vector2> uvs, List<int> tris, Vector2 p1, Vector2 p2, float yBottom, float yTop)
    {
        int vIndex = vertices.Count;
        float width = Vector2.Distance(p1, p2);
        
        Vector3 v0 = new Vector3(p1.x, yBottom, p1.y);
        Vector3 v1 = new Vector3(p2.x, yBottom, p2.y);
        Vector3 v2 = new Vector3(p1.x, yTop, p1.y);
        Vector3 v3 = new Vector3(p2.x, yTop, p2.y);
        
        vertices.Add(v0); vertices.Add(v1); vertices.Add(v2); vertices.Add(v3);
        uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(width, 0));
        uvs.Add(new Vector2(0, yTop-yBottom)); uvs.Add(new Vector2(width, yTop-yBottom));
        
        // Outer face
        tris.Add(vIndex); tris.Add(vIndex+3); tris.Add(vIndex+2);
        tris.Add(vIndex); tris.Add(vIndex+1); tris.Add(vIndex+3);
        
        // Inner face
        tris.Add(vIndex); tris.Add(vIndex+2); tris.Add(vIndex+3);
        tris.Add(vIndex); tris.Add(vIndex+3); tris.Add(vIndex+1);
    }

    /// <summary>
    /// Calcule et génère automatiquement les points de portes et fenêtres le long des façades du bâtiment.
    /// Les murs mitoyens créés par la subdivision parcellaire (<paramref name="partyWallEdges"/>) ne
    /// reçoivent jamais d'ouverture, comme dans une vraie maison de ville attenante à ses voisines.
    /// </summary>
    private void GenerateDoorsAndWindows(GameObject buildingGo, BuildingStructure structure, List<Vector2> footprint, float height, List<(Vector2, Vector2)> partyWallEdges)
    {
        if (footprint == null || footprint.Count < 3) return;

        int n = footprint.Count;

        // Calcul du centre 2D du bâtiment pour orienter les normales vers l'extérieur
        Vector2 centroid = Vector2.zero;
        foreach (var p in footprint) centroid += p;
        centroid /= n;

        bool[] isPartyWall = new bool[n];
        for (int i = 0; i < n; i++)
        {
            isPartyWall[i] = IsPartyWallEdge(footprint[i], footprint[(i + 1) % n], partyWallEdges);
        }

        // Une seule façade principale par bâtiment reçoit la porte d'entrée (comportement réel d'une
        // maison individuelle) : on choisit la plus longue arête réellement extérieure.
        int frontEdge = -1;
        float bestLen = 0f;
        for (int i = 0; i < n; i++)
        {
            if (isPartyWall[i]) continue;
            float len = Vector2.Distance(footprint[i], footprint[(i + 1) % n]);
            if (len >= 2.5f && len > bestLen) { bestLen = len; frontEdge = i; }
        }
        // Filet de sécurité : un lot très petit peut n'avoir aucune arête >= 2.5m ou être entouré de
        // murs mitoyens des deux côtés (cas rare d'une subdivision agressive). On force alors une
        // entrée sur l'arête la plus longue disponible pour garantir que le bâtiment reste franchissable.
        if (frontEdge == -1)
        {
            for (int i = 0; i < n; i++)
            {
                float len = Vector2.Distance(footprint[i], footprint[(i + 1) % n]);
                if (len > bestLen) { bestLen = len; frontEdge = i; }
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (isPartyWall[i]) continue; // Mur mitoyen : aucune porte ni fenêtre, comme en réalité

            int next = (i + 1) % n;
            Vector2 p1 = footprint[i];
            Vector2 p2 = footprint[next];

            Vector3 startPos = new Vector3(p1.x, 0, p1.y);
            Vector3 endPos = new Vector3(p2.x, 0, p2.y);
            Vector3 seg = endPos - startPos;
            float segLen = seg.magnitude;

            if (segLen < 2.5f) continue; // Mur trop court

            Vector3 tangent = seg / segLen;
            Vector3 outwardNormal = new Vector3(-tangent.z, 0, tangent.x);

            // S'assurer que la normale pointe vers l'extérieur du bâtiment
            Vector2 mid2D = (p1 + p2) * 0.5f;
            Vector2 fromCentroid = (mid2D - centroid).normalized;
            if (Vector2.Dot(new Vector2(outwardNormal.x, outwardNormal.z), fromCentroid) < 0)
            {
                outwardNormal = -outwardNormal;
            }

            // 1. PORTE(S) — une entrée principale (deux si la façade dépasse 16m, cas d'un immeuble collectif)
            if (i == frontEdge)
            {
                int numDoors = segLen > 16f ? 2 : 1;
                float doorSpacing = segLen / (numDoors + 1);
                for (int d = 1; d <= numDoors; d++)
                {
                    float doorEdgeDistance = d * doorSpacing;
                    Vector3 doorPos = startPos + tangent * doorEdgeDistance + outwardNormal * 0.05f;
                    structure.doors.Add(new BuildingStructure.BuildingDoor
                    {
                        position = doorPos,
                        entryDirection = outwardNormal,
                        edgeIndex = i,
                        edgeDistance = doorEdgeDistance
                    });

                    // --- NEW: Add NavMeshLink for the door to allow passing through solid walls ---
                    // We link from outside to inside.
                    GameObject doorLinkGo = new GameObject("Door_NavMeshLink");
                    doorLinkGo.transform.parent = buildingGo.transform;
                    doorLinkGo.transform.position = doorPos;
                    var navLink = doorLinkGo.AddComponent<Unity.AI.Navigation.NavMeshLink>();
                    navLink.startPoint = outwardNormal * 1.5f; // outside
                    navLink.endPoint = -outwardNormal * 1.5f; // inside
                    navLink.width = 1.5f;
                    navLink.bidirectional = true;
                }
            }

            // 2. FENÊTRES — densité réaliste (une par ~4m de façade), uniquement sur les murs extérieurs
            int numWindowsH = Mathf.Clamp(Mathf.FloorToInt(segLen / 4.0f), 1, 4);
            float winSpacing = segLen / (numWindowsH + 1);
            int floorCount = Mathf.Max(1, Mathf.FloorToInt(height / 3.2f));
            for (int f = 0; f < floorCount; f++)
            {
                float floorY = 1.4f + f * 3.0f;
                if (floorY > height - 0.8f) break;

                for (int w = 1; w <= numWindowsH; w++)
                {
                    Vector3 winPos = startPos + tangent * (w * winSpacing) + Vector3.up * floorY + outwardNormal * 0.05f;
                    structure.windows.Add(new BuildingStructure.BuildingWindow
                    {
                        id = structure.windows.Count + 1,
                        position = winPos,
                        outwardNormal = outwardNormal,
                        floorLevel = f,
                        isOccupied = false,
                        occupant = null
                    });
                }
            }
        }
    }

    internal static bool IsPartyWallEdge(Vector2 p1, Vector2 p2, List<(Vector2, Vector2)> partyWallEdges)
    {
        if (partyWallEdges == null) return false;
        const float eps = 0.01f;
        foreach (var (a, b) in partyWallEdges)
        {
            if ((Vector2.Distance(p1, a) < eps && Vector2.Distance(p2, b) < eps) ||
                (Vector2.Distance(p1, b) < eps && Vector2.Distance(p2, a) < eps))
            {
                return true;
            }
        }
        return false;
    }

    private static Material sharedDoorMaterial;
    private static Material sharedWindowMaterial;

    private void EnsureOpeningsMaterials()
    {
        if (sharedDoorMaterial == null)
        {
            Color doorCol = new Color(0.18f, 0.12f, 0.08f, 1f); 
            sharedDoorMaterial = SafeMaterialFactory.CreateLit(doorCol);
        }

        if (sharedWindowMaterial == null)
        {
            Color winCol = new Color(0.08f, 0.16f, 0.25f, 1f); 
            sharedWindowMaterial = SafeMaterialFactory.CreateLit(winCol);
        }
    }

    private void BuildVisualOpenings(GameObject buildingGo, BuildingStructure structure)
    {
        EnsureOpeningsMaterials();

        if (structure.doors != null && structure.doors.Count > 0)
        {
            int doorIndex = 0;
            foreach (var door in structure.doors)
            {
                Vector3 n = door.entryDirection;
                Vector3 t = new Vector3(-n.z, 0, n.x);
                Vector3 center = door.position + Vector3.up * 1.1f + n * 0.05f;

                List<Vector3> verts = new List<Vector3>();
                List<Vector2> uvs = new List<Vector2>();
                List<int> tris = new List<int>();
                AddOpeningQuad(verts, uvs, tris, center, t, Vector3.up, 1.2f, 2.2f);

                GameObject doorObj = new GameObject($"Door_{doorIndex}");
                doorObj.transform.SetParent(buildingGo.transform, false);

                MeshFilter mf = doorObj.AddComponent<MeshFilter>();
                MeshRenderer mr = doorObj.AddComponent<MeshRenderer>();
                Mesh m = new Mesh();
                m.vertices = verts.ToArray();
                m.uv = uvs.ToArray();
                m.triangles = tris.ToArray();
                m.RecalculateNormals();
                mf.sharedMesh = m;
                mr.sharedMaterial = sharedDoorMaterial;

                // BoxCollider pour la sélection et le survol à la souris
                BoxCollider bc = doorObj.AddComponent<BoxCollider>();
                bc.center = center;
                bc.size = new Vector3(1.4f, 2.2f, 0.6f);

                // Composant d'interaction avec surbrillance cyan
                var doorInteract = doorObj.AddComponent<StreetAct.Interaction.DoorInteraction>();
                doorInteract.Initialize(structure, door, sharedDoorMaterial);
                structure.doorInteractions.Add(doorInteract);

                doorIndex++;
            }
        }

        if (structure.windows != null && structure.windows.Count > 0)
        {
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();

            foreach (var win in structure.windows)
            {
                Vector3 n = win.outwardNormal;
                Vector3 t = new Vector3(-n.z, 0, n.x);
                Vector3 center = win.position + n * 0.05f;
                AddOpeningQuad(verts, uvs, tris, center, t, Vector3.up, 1.1f, 1.4f);
            }

            GameObject winsObj = new GameObject("Windows_Visual");
            winsObj.transform.SetParent(buildingGo.transform, false);
            MeshFilter mf = winsObj.AddComponent<MeshFilter>();
            MeshRenderer mr = winsObj.AddComponent<MeshRenderer>();
            Mesh m = new Mesh();
            m.vertices = verts.ToArray();
            m.uv = uvs.ToArray();
            m.triangles = tris.ToArray();
            m.RecalculateNormals();
            mf.sharedMesh = m;
            mr.sharedMaterial = sharedWindowMaterial;
        }
    }

    private void AddOpeningQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris, Vector3 center, Vector3 right, Vector3 up, float width, float height)
    {
        int startIdx = verts.Count;
        Vector3 halfR = right * (width * 0.5f);
        Vector3 halfU = up * (height * 0.5f);

        verts.Add(center - halfR - halfU);
        verts.Add(center + halfR - halfU);
        verts.Add(center + halfR + halfU);
        verts.Add(center - halfR + halfU);

        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(1, 0));
        uvs.Add(new Vector2(1, 1));
        uvs.Add(new Vector2(0, 1));

        // Face avant (Extérieure)
        tris.Add(startIdx + 0);
        tris.Add(startIdx + 2);
        tris.Add(startIdx + 1);

        tris.Add(startIdx + 0);
        tris.Add(startIdx + 3);
        tris.Add(startIdx + 2);

        // Face arrière (Intérieure - Double-face pour vue depuis l'intérieur du bâtiment)
        tris.Add(startIdx + 0);
        tris.Add(startIdx + 1);
        tris.Add(startIdx + 2);

        tris.Add(startIdx + 0);
        tris.Add(startIdx + 2);
        tris.Add(startIdx + 3);
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
