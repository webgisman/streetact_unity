using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Novgov.Core;
using Novgov.Generation;
using UnityEngine.Networking;
using UnityEngine.AI;
using Unity.AI.Navigation;

public class CityGenerator : MonoBehaviour
{
    // Zoom de la grille "Zones de Conquête" : chaque Zone = exactement 1 tuile Slippy Map à ce zoom
    // (~195m de côté à la latitude de Lille, cf. GeoProjection.TileBoundingBox). Le GPS de l'appareil
    // ne sert plus qu'une seule fois, au tout premier lancement, pour calculer la Zone de départ via
    // GeoProjection.TileIndexFromCoordinate — ensuite le jeu ne raisonne plus qu'en (zoneTileX, zoneTileY).
    public const int ZONE_ZOOM = 17;

    [Header("Zone de Conquête actuelle (index de tuile Slippy Map, Zoom 17)")]
    public int zoneTileX = 0;
    public int zoneTileY = 0;
    public int HQBuildingIndex = -1;

    // Conservés en lecture seule pour tout code externe qui affiche encore une position GPS (debug UI,
    // logs) : recalculés à partir de (zoneTileX, zoneTileY) au centre de la Zone, jamais lus en entrée.
    public float latitude { get; private set; } = 50.6927f;
    public float longitude { get; private set; } = 3.1778f;

    /// <summary>Clé stable identifiant la carte ACTUELLEMENT chargée — "Z{ZONE_ZOOM}_{tileX}_{tileY}"
    /// pour une vraie Zone, "Default" pour la carte hors-ligne par défaut. Source de vérité unique
    /// pour le cache disque de géométrie tactique (voir TacticalGridBuilder) : zoneTileX/zoneTileY
    /// restent à leur dernière valeur même après un retour à la carte par défaut (LoadDefaultOfflineCity
    /// ne les touche pas), donc les lire directement pour construire une clé de cache donnerait la
    /// mauvaise réponse après un aller-retour Zone -> défaut.</summary>
    public string CurrentGridCacheKey { get; private set; } = "Default";

    public float buildingHeight = 6f; // Hauteur moyenne des bâtiments (variation aléatoire de ±1.5m par lot)
    public Material buildingMaterial;

    /// <summary>
    /// Vrai une fois que la Zone courante (bâtiments + sol + NavMesh baké) est ENTIÈREMENT prête —
    /// contrairement à MapTileLoader.isMapLoaded qui ne couvre que le sol. Nécessaire côté serveur
    /// (MatchSessionManager) pour savoir précisément quand demander le déploiement des unités sans
    /// deviner un délai fixe ; utile aussi côté client pour ne pas ouvrir un écran trop tôt.
    /// </summary>
    public bool IsCityReady { get; private set; }

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

        // Le cache de géométrie statique de TacticalGridBuilder (murs/grille de marche, voir ce
        // fichier — introduit le 2026-08-30 pour éviter de reconstruire ~1500 murs à chaque tour)
        // est un champ STATIQUE propre à CE processus : client et serveur tournent dans des
        // processus séparés, chacun doit invalider SON PROPRE cache au moment où SA ville change,
        // qu'il s'agisse du serveur (voir MatchSessionManager) ou du client (aperçu de trajectoire,
        // voir TacticalPathManager_PathDrawing.AppendGridPathSegment) — sans ça, un ancien cache
        // mettrait les murs de l'ANCIENNE carte au mauvais endroit sur la nouvelle.
        Novgov.TacticalCore.TacticalGridBuilder.InvalidateCache();

        // Même raisonnement, même piège : la liste des zones de ruines franchissables est un état
        // STATIQUE du processus. Les bâtiments de l'ancienne ville viennent d'être détruits par
        // Destroy(oldCity), mais leurs rectangles de "franchissement libre" restaient enregistrés et
        // s'appliquaient à la carte suivante — des unités traversaient donc les murs dès le premier
        // tour, sur des bâtiments parfaitement intacts.
        DestructibleEnvironment.ResetRubble();
    }

    [ContextMenu("Generate City")]
    public void GenerateCity()
    {
        CancelActiveGenerationAndClearCity();
        IsCityReady = false;
        activeGeneration = StartCoroutine(FetchCityData());
    }

    private IEnumerator FetchCityData()
    {
        Debug.Log($"Fetching city data for Zone Z{ZONE_ZOOM} ({zoneTileX},{zoneTileY}) from Overpass API...");

        // Bounding box EXACTE de la tuile Slippy Map (S,W,N,E) — remplace l'ancien filtre circulaire
        // "around:250,lat,lon". Overpass QL attend l'ordre (south,west,north,east) pour un bbox.
        GeoProjection.TileBoundingBox(zoneTileX, zoneTileY, ZONE_ZOOM, out double south, out double west, out double north, out double east);

        // Centre de la Zone = milieu de sa bbox. Sert d'origine (0,0,0) Unity pour cette Zone : stable
        // et déterministe (dérivé uniquement de l'index de tuile), contrairement à l'ancien centre GPS
        // brut de l'appareil qui pouvait légèrement varier d'une lecture à l'autre.
        double centerLat = (south + north) / 2.0;
        double centerLon = (west + east) / 2.0;
        latitude = (float)centerLat;
        longitude = (float)centerLon;
        GeoProjection.SetCenter(latitude, longitude);
        CurrentGridCacheKey = $"Z{ZONE_ZOOM}_{zoneTileX}_{zoneTileY}";

        // CACHE DISQUE LU AVANT TOUTE REQUÊTE (correctif 2026-09-05). Ce fichier était jusqu'ici
        // écrit après chaque fetch mais JAMAIS relu : chaque appel à cette méthode déclenchait donc
        // TOUJOURS un vrai appel réseau Overpass, même pour une tuile déjà générée par ce processus
        // auparavant. Une fois qu'une tuile a été vue une première fois, son JSON est désormais figé
        // ici pour la durée de vie du cache — ce qui réduit aussi l'exposition au risque documenté
        // "deux requêtes vers deux miroirs Overpass indépendants peuvent légèrement diverger" : seul
        // le tout premier appel jamais fait pour cette tuile touche encore le réseau.
        //
        // Cette méthode n'est plus utilisée pour la géométrie d'un VRAI match multijoueur (voir
        // LoadZoneFromServerData, appelé à la place) : elle ne sert plus qu'à l'initialisation de la
        // Zone domicile du joueur, à l'exploration de la carte des Zones, et — en tout dernier
        // recours si le serveur n'a pas fourni ses données — à un repli d'urgence. Ce cache-hit ne
        // réintroduit donc aucun risque d'équité qui n'existait pas déjà dans ce chemin de repli.
        if (TryReadZoneCacheFromDisk(zoneTileX, zoneTileY, out string cachedJson))
        {
            Debug.Log($"[CityGenerator] 📂 Zone ({zoneTileX},{zoneTileY}) restaurée depuis le cache disque local — aucune requête Overpass.");
            yield return FinishZoneLoadFromJson(cachedJson);
            yield break;
        }

        // Utilisation de InvariantCulture pour forcer le point '.' comme séparateur décimal
        // Ajout de [timeout:90] pour laisser plus de temps au serveur sur les grosses requêtes (le défaut est court)
        string query = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[out:json][timeout:90];\n(\n  way[\"building\"]({0},{1},{2},{3});\n  relation[\"building\"]({0},{1},{2},{3});\n);\nout geom;",
            south, west, north, east);

        // Utilisation d'un POST et d'un WWWForm pour gérer automatiquement l'encodage URL et les requêtes longues

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
            WriteZoneCacheToDisk(zoneTileX, zoneTileY, jsonText);
            yield return FinishZoneLoadFromJson(jsonText);
        }
    }

    /// <summary>Écrit le JSON Overpass brut d'une Zone sur le disque local, pour réutilisation
    /// hors-ligne future (voir ZoneCacheFilePath). Un échec d'écriture (permissions, disque plein)
    /// n'empêche jamais la partie de continuer — capturé et journalisé, rien de plus.</summary>
    private static void WriteZoneCacheToDisk(int tileX, int tileY, string jsonText)
    {
        string cachePath = ZoneCacheFilePath(tileX, tileY);
        try
        {
            string dir = System.IO.Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(cachePath, jsonText);
            Debug.Log($"[CityGenerator] 💾 Données de la carte sauvegardées dans le cache local : {cachePath}");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[CityGenerator] Erreur d'écriture du cache : {ex.Message}");
        }
    }

    /// <summary>Tout ce qui suit l'obtention du JSON Overpass d'une Zone — que ce JSON vienne d'un
    /// fetch réseau direct (FetchCityData), du cache disque local, OU du serveur autoritaire (voir
    /// LoadZoneFromServerData, correctif 2026-09-05) : parse+construit les bâtiments
    /// (ProcessDataCoroutine), attend le sol (MapTileLoader), bake le NavMesh, notifie les UnitAI.
    /// Extrait de FetchCityData pour que ces TROIS origines de données partagent EXACTEMENT le même
    /// chemin de traitement — la moindre divergence de code ici serait une source d'iniquité en soi,
    /// indépendamment de la question "le JSON en entrée est-il identique".</summary>
    private IEnumerator FinishZoneLoadFromJson(string jsonText)
    {
        yield return ProcessDataCoroutine(jsonText);
        yield return FinalizeCityGeneration();
    }

    /// <summary>Attend le sol, bake le NavMesh autour des bâtiments fraîchement créés, puis relâche
    /// les unités et marque la ville prête — extrait de FinishZoneLoadFromJson (2026-09-12) pour être
    /// partagé avec ApplyAuthoritativeBuildingsCoroutine (résynchronisation "équité géométrique, 2ème
    /// étage") : les deux chemins créent des bâtiments par des voies différentes (JSON Overpass vs
    /// structure déjà résolue reçue du serveur), mais la finalisation (NavMesh/streaming/IsCityReady)
    /// est identique dans les deux cas. Comportement STRICTEMENT inchangé par rapport à l'ancien code
    /// inline de FinishZoneLoadFromJson — seule la localisation a changé.</summary>
    private IEnumerator FinalizeCityGeneration()
    {
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
        // On retient qui était RÉELLEMENT actif avant ce masquage temporaire : les unités
        // placées à la main dans la scène (Unite_1/2, Leopard_1/2, canon-vehicle_1/2) sont
        // désactivées par défaut pour servir uniquement de modèle au déploiement manuel — un
        // ré-activation en masse ici les faisait réapparaître sur le champ de bataille à
        // chaque génération de carte, quel que soit leur état d'origine dans la scène.
        bool[] wasActive = new bool[allUnits.Length];
        for (int i = 0; i < allUnits.Length; i++) wasActive[i] = allUnits[i] != null && allUnits[i].gameObject.activeSelf;
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

        // 4. LÂCHER LES CHIENS ! On ne réactive que celles qui étaient déjà actives avant.
        for (int i = 0; i < allUnits.Length; i++) { if (allUnits[i] != null && wasActive[i]) allUnits[i].gameObject.SetActive(true); }
        yield return null; // Laisser 1 frame pour la réactivation

        Debug.Log($"[CityGenerator] Notifying {allUnits.Length} UnitAIs that NavMesh is ready.");
        foreach (var unit in allUnits)
        {
            if (unit != null && unit.gameObject.activeSelf) unit.OnNavMeshReady();
        }

        GameManagerUI.OptimizeSceneMaterials();
        if (GameManagerUI.Instance != null) GameManagerUI.Instance.HideLoading();
        IsCityReady = true;
    }

    /// <summary>ÉQUITÉ GÉOMÉTRIQUE, 2ème étage (2026-09-12) — voir MatchState.AuthoritativeCityHash
    /// (serveur) et NetMessage.city_verify_result pour le contexte complet. Appelée UNIQUEMENT quand
    /// le serveur a détecté que la ville générée localement par ce client diffère de la sienne :
    /// reconstruit la ville ENTIÈRE (jamais un patch partiel, voir NetMessage.BuildingGeometryDto)
    /// directement depuis la structure déjà résolue reçue du serveur — sans repasser par le JSON
    /// Overpass brut ni par l'algorithme de subdivision/placement de portes-fenêtres normal (qui a
    /// justement produit un résultat différent une première fois sur ce client) : chaque bâtiment est
    /// instancié tel quel (empreinte/hauteur/portes/fenêtres déjà figées), donc garanti identique au
    /// serveur quoi qu'il arrive, plutôt que de retenter le même calcul en espérant un résultat
    /// différent.</summary>
    public void ApplyAuthoritativeBuildings(List<Novgov.TacticalCore.TacticalBuilding> buildings, System.Action onComplete)
    {
        StartCoroutine(ApplyAuthoritativeBuildingsCoroutine(buildings, onComplete));
    }

    private IEnumerator ApplyAuthoritativeBuildingsCoroutine(List<Novgov.TacticalCore.TacticalBuilding> buildings, System.Action onComplete)
    {
        CancelActiveGenerationAndClearCity();
        IsCityReady = false;

        GameObject cityRoot = new GameObject("City");
        int processedSinceYield = 0;
        int created = 0;
        foreach (var template in buildings)
        {
            if (CreateBuildingObjectFromAuthoritative(template, cityRoot.transform)) created++;

            if (++processedSinceYield >= BUILDINGS_PER_FRAME)
            {
                processedSinceYield = 0;
                yield return null;
            }
        }
        Debug.Log($"<color=cyan>[CityGenerator] Resynchronisation depuis la structure autoritaire du serveur : {created}/{buildings.Count} bâtiments recréés à l'identique.</color>");

        yield return FinalizeCityGeneration();
        onComplete?.Invoke();
    }

    /// <summary>Un bâtiment DÉJÀ résolu (empreinte/hauteur/portes/fenêtres exactes reçues du serveur)
    /// — contrairement à CreateBuildingObject (chemin normal), aucune fusion de trous/subdivision en
    /// lots ni génération procédurale de portes/fenêtres : tout est déjà connu, on instancie
    /// directement. edgeIndex/edgeDistance de chaque porte (nécessaires à TacticalGridBuilder.
    /// AddWallSegmentsForBuilding pour marquer le bon segment de mur "franchissable", voir ce
    /// fichier) ne sont volontairement PAS transmis sur le réseau (NetMessage.DoorGeometryDto) : ils
    /// se déduisent sans aucune ambiguïté de la position de la porte + de l'empreinte (voir
    /// ResolveEdgeIndexAndDistance), pas la peine de faire transiter une donnée redondante. Même
    /// remarque pour la hauteur Y d'une fenêtre (voir NetMessage.WindowGeometryDto) : recalculée ici
    /// avec EXACTEMENT la même formule que GenerateDoorsAndWindows ("floorY = 1.4f + f * 3.0f").
    /// Matériaux/mobilier urbain non reproduits à l'identique (dépendent du flux UnityEngine.Random
    /// partagé, jamais transmis) — cosmétique uniquement, sans effet sur la résolution tactique.</summary>
    private bool CreateBuildingObjectFromAuthoritative(Novgov.TacticalCore.TacticalBuilding template, Transform parent)
    {
        List<Vector2> footprint = template.footprint;
        if (footprint == null || footprint.Count < 3)
        {
            Debug.LogWarning($"[CityGenerator] Bâtiment autoritaire ignoré (empreinte < 3 sommets) : id={template.id}");
            return false;
        }

        float lotHeight = template.height > 0f ? template.height : 6f;
        string lotName = "Building_Resync_" + template.id;

        GameObject buildingGo = new GameObject(lotName);
        buildingGo.transform.parent = parent;

        BuildingStructure structure = buildingGo.AddComponent<BuildingStructure>();
        structure.InitPolygon(footprint, lotHeight);
        buildingGo.AddComponent<DestructibleEnvironment>();

        if (template.doors != null)
        {
            foreach (var d in template.doors)
            {
                ResolveEdgeIndexAndDistance(footprint, d.position, out int edgeIndex, out float edgeDistance);
                structure.doors.Add(new BuildingStructure.BuildingDoor
                {
                    position = new Vector3(d.position.x, 0f, d.position.y),
                    entryDirection = new Vector3(d.entryDirection.x, 0f, d.entryDirection.y),
                    edgeIndex = edgeIndex,
                    edgeDistance = edgeDistance,
                    width = d.width
                });

                // Même NavMeshLink que GenerateDoorsAndWindows (voir ce commentaire là-bas pour le
                // pourquoi) — un lien manquant après resynchronisation laisserait le NavMesh visuel
                // solo bloqué à cette porte, alors que le rendu normal (JSON Overpass) l'aurait posé.
                GameObject doorLinkGo = new GameObject("Door_NavMeshLink");
                doorLinkGo.transform.parent = buildingGo.transform;
                doorLinkGo.transform.position = new Vector3(d.position.x, 0f, d.position.y);
                var navLink = doorLinkGo.AddComponent<Unity.AI.Navigation.NavMeshLink>();
                Vector3 outward = new Vector3(d.entryDirection.x, 0f, d.entryDirection.y);
                navLink.startPoint = outward * 1.5f;
                navLink.endPoint = -outward * 1.5f;
                navLink.width = 1.5f;
                navLink.bidirectional = true;
            }
        }

        if (template.windows != null)
        {
            foreach (var w in template.windows)
            {
                // Même formule EXACTE que GenerateDoorsAndWindows ("floorY = 1.4f + f * 3.0f") — la
                // hauteur Y n'est pas transmise sur le réseau, voir NetMessage.WindowGeometryDto.
                float floorY = 1.4f + w.floorLevel * 3.0f;
                structure.windows.Add(new BuildingStructure.BuildingWindow
                {
                    id = w.id,
                    position = new Vector3(w.position.x, floorY, w.position.y),
                    outwardNormal = new Vector3(w.outwardNormal.x, 0f, w.outwardNormal.y),
                    floorLevel = w.floorLevel,
                    isOccupied = false,
                    occupant = null
                });
            }
        }

        // --- Meshes (mêmes fonctions PURES que le chemin normal, aucune dépendance au flux Random) ---
        List<int> roofIndices = Triangulate(footprint);
        if (roofIndices.Count == 0)
        {
            Debug.LogWarning($"[CityGenerator] Bâtiment autoritaire ignoré (triangulation impossible) : id={template.id}");
            Destroy(buildingGo);
            return false;
        }

        Mesh roofMesh = CreateHipRoofMesh(footprint, roofIndices, lotHeight);
        GameObject roofGo = new GameObject("Roof");
        roofGo.transform.parent = buildingGo.transform;
        roofGo.AddComponent<MeshFilter>().sharedMesh = roofMesh;
        var roofRenderer = roofGo.AddComponent<MeshRenderer>();
        roofRenderer.sharedMaterial = GetRandomRoofMaterial();
        roofRenderer.enabled = false;
        roofGo.AddComponent<MeshCollider>().sharedMesh = roofMesh;

        Mesh floorMesh = CreateFloorMesh(footprint, roofIndices, 0.08f);
        GameObject floorGo = new GameObject("Footprint_2D");
        floorGo.transform.parent = buildingGo.transform;
        floorGo.AddComponent<MeshFilter>().sharedMesh = floorMesh;
        var floorRenderer = floorGo.AddComponent<MeshRenderer>();
        Material floorMat = Get2DBuildingMaterial();
        floorRenderer.sharedMaterial = floorMat;
        floorRenderer.enabled = true;
        floorGo.AddComponent<MeshCollider>().sharedMesh = floorMesh;

        if (floorMat != null)
        {
            Color tinted = JitterBuildingColor(Base2DBuildingColor, footprint[0]);
            MaterialPropertyBlock tintBlock = new MaterialPropertyBlock();
            if (floorMat.HasProperty("_BaseColor")) tintBlock.SetColor("_BaseColor", tinted);
            if (floorMat.HasProperty("_Color")) tintBlock.SetColor("_Color", tinted);
            floorRenderer.SetPropertyBlock(tintBlock);
        }

        Mesh outlineMesh = CreateOutlineMesh(footprint, 0.09f, 0.4f);
        if (outlineMesh != null)
        {
            GameObject outlineGo = new GameObject("Outline_2D");
            outlineGo.transform.parent = buildingGo.transform;
            outlineGo.AddComponent<MeshFilter>().sharedMesh = outlineMesh;
            var outlineRenderer = outlineGo.AddComponent<MeshRenderer>();
            outlineRenderer.sharedMaterial = Get2DOutlineMaterial();
            outlineRenderer.enabled = true;
        }

        Mesh wallsMesh = CreateWallsMesh(footprint, lotHeight, structure.doors);
        GameObject wallsGo = new GameObject("Walls");
        wallsGo.transform.parent = buildingGo.transform;
        wallsGo.AddComponent<MeshFilter>().sharedMesh = wallsMesh;
        var wallsRenderer = wallsGo.AddComponent<MeshRenderer>();
        wallsRenderer.sharedMaterial = GetRandomWallMaterial();
        wallsRenderer.enabled = false;
        wallsGo.AddComponent<MeshCollider>().sharedMesh = wallsMesh;

        BuildVisualOpenings(buildingGo, structure);

        TacticalVisibility vis = buildingGo.AddComponent<TacticalVisibility>();
        vis.roofObject = roofGo;
        vis.structure = structure;

        CreateRoofAccess(buildingGo, footprint, lotHeight);

        return true;
    }

    /// <summary>Retrouve, purement géométriquement, sur quelle arête de <paramref name="footprint"/>
    /// se trouve <paramref name="doorPos"/> (au décalage de 0.05m près vers l'extérieur, voir
    /// GenerateDoorsAndWindows — négligeable face à la distance entre arêtes) — nécessaire car
    /// edgeIndex/edgeDistance ne sont pas transmis sur le réseau (voir NetMessage.DoorGeometryDto),
    /// une porte reçue étant par construction déjà posée sur une arête réelle de cette même
    /// empreinte.</summary>
    private static void ResolveEdgeIndexAndDistance(List<Vector2> footprint, Vector2 doorPos, out int edgeIndex, out float edgeDistance)
    {
        int n = footprint.Count;
        edgeIndex = 0;
        edgeDistance = 0f;
        float bestSqrDist = float.MaxValue;

        for (int i = 0; i < n; i++)
        {
            Vector2 a = footprint[i];
            Vector2 b = footprint[(i + 1) % n];
            Vector2 ab = b - a;
            float lenSqr = ab.sqrMagnitude;
            if (lenSqr < 0.0001f) continue;

            float t = Mathf.Clamp01(Vector2.Dot(doorPos - a, ab) / lenSqr);
            Vector2 projected = a + ab * t;
            float sqrDist = (doorPos - projected).sqrMagnitude;

            if (sqrDist < bestSqrDist)
            {
                bestSqrDist = sqrDist;
                edgeIndex = i;
                edgeDistance = t * Mathf.Sqrt(lenSqr);
            }
        }
    }

    /// <summary>ÉQUITÉ MULTIJOUEUR (2026-09-05) — point d'entrée principal pour charger une Zone à
    /// partir du JSON Overpass exact que le SERVEUR AUTORITAIRE a lui-même utilisé pour cette
    /// tuile, plutôt que de laisser ce client refaire sa propre requête Overpass indépendante.
    ///
    /// AVANT ce correctif : client ET serveur appelaient chacun GenerateCity() -> FetchCityData(),
    /// deux requêtes HTTP totalement indépendantes vers Overpass (parfois deux miroirs différents
    /// parmi les 3 de repli) pour la MÊME tuile. Rien ne garantissait que les deux réponses
    /// contiennent exactement les mêmes bâtiments : une édition OSM survenue entre les deux appels
    /// (même de quelques secondes), ou un simple retard de réplication entre miroirs Overpass,
    /// pouvait faire diverger silencieusement la géométrie vue par le joueur de celle utilisée par
    /// le serveur pour arbitrer le combat — jamais détecté, jamais signalé.
    ///
    /// Ce chemin élimine la cause : plus aucune requête Overpass n'est faite ici, le JSON est du
    /// texte déjà entièrement déterminé par le serveur (voir MultiplayerMatchController, message
    /// "zone_geometry_ready"), rejoué tel quel dans EXACTEMENT le même pipeline
    /// (FinishZoneLoadFromJson) que si ce client l'avait obtenu par sa propre requête. Le résultat
    /// est également sauvegardé en cache local (comme FetchCityData), pour une reprise hors-ligne
    /// future de cette même Zone.</summary>
    public void LoadZoneFromServerData(int tileX, int tileY, string json)
    {
        CancelActiveGenerationAndClearCity();
        IsCityReady = false;

        if (string.IsNullOrEmpty(json))
        {
            // Ne devrait jamais arriver si l'appelant a bien vérifié avant d'appeler cette méthode
            // (voir MultiplayerMatchController.LoadMatchMapThenOpenDeployment) — filet de sécurité
            // uniquement, jamais silencieux.
            Debug.LogError("[CityGenerator] LoadZoneFromServerData appelé sans JSON — repli sur une génération locale indépendante (RISQUE D'ÉQUITÉ : ce client va potentiellement voir des bâtiments différents du serveur).");
            zoneTileX = tileX;
            zoneTileY = tileY;
            GenerateCity();
            return;
        }

        // Même préambule que FetchCityData (bbox de la tuile -> centre -> origine Unity de la Zone
        // -> clé de cache) : indispensable même si aucune requête réseau n'est faite ici, ces valeurs
        // pilotent GeoProjection.CoordinateToWorldPoint et le seed déterministe de ProcessDataCoroutine.
        zoneTileX = tileX;
        zoneTileY = tileY;
        GeoProjection.TileBoundingBox(tileX, tileY, ZONE_ZOOM, out double south, out double west, out double north, out double east);
        double centerLat = (south + north) / 2.0;
        double centerLon = (west + east) / 2.0;
        latitude = (float)centerLat;
        longitude = (float)centerLon;
        GeoProjection.SetCenter(latitude, longitude);
        CurrentGridCacheKey = $"Z{ZONE_ZOOM}_{tileX}_{tileY}";

        WriteZoneCacheToDisk(tileX, tileY, json);
        activeGeneration = StartCoroutine(FinishZoneLoadFromJson(json));
    }

    // Nom de fichier cache unique et déterministe pour une Zone de Conquête (index de tuile, pas de
    // coordonnées GPS arrondies) : CityCache_Z17_{tileX}_{tileY}.json.
    /// <summary>Chemin complet du cache disque du JSON Overpass brut d'une Zone — dans le MÊME
    /// sous-dossier persistant que TacticalGridBuilder ("TacticalGridCache"), correctif 2026-09-05 :
    /// ce fichier vivait auparavant à la racine de Application.persistentDataPath, un répertoire
    /// EFFACÉ à chaque redéploiement du conteneur serveur (contrairement à "TacticalGridCache", monté
    /// sur un volume Docker nommé qui survit aux redéploiements — voir docker-compose.yml). Partager
    /// ce même dossier évite tout changement d'infrastructure (aucune modification de
    /// docker-compose.yml nécessaire) tout en donnant au cache de géométrie brute la même
    /// persistance que le cache de géométrie digérée.</summary>
    private static string ZoneCacheFilePath(int tileX, int tileY)
    {
        string dir = System.IO.Path.Combine(Application.persistentDataPath, "TacticalGridCache");
        return System.IO.Path.Combine(dir, $"CityCache_Z{ZONE_ZOOM}_{tileX}_{tileY}.json");
    }

    /// <summary>Tente de lire le JSON Overpass déjà mis en cache pour cette tuile — false si absent
    /// ou manifestement invalide (fichier tronqué). Voir WriteZoneCacheToDisk pour l'écriture.
    /// Public : appelé aussi par MatchSessionManager côté serveur pour retrouver le JSON exact
    /// utilisé afin de le transmettre au client (voir NetMessage.city_data_json).</summary>
    public static bool TryReadZoneCacheFromDisk(int tileX, int tileY, out string json)
    {
        json = null;
        string path = ZoneCacheFilePath(tileX, tileY);
        if (!System.IO.File.Exists(path)) return false;
        try
        {
            string text = System.IO.File.ReadAllText(path);
            if (string.IsNullOrEmpty(text) || text.Length < 20) return false;
            json = text;
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[CityGenerator] Impossible de lire le cache de Zone : {ex.Message}");
            return false;
        }
    }

    // La carte "par défaut" (hors-ligne, secours réseau, écran de chargement du serveur au repos)
    // est un contenu figé indépendant du système de Zones : elle garde sa PROPRE position/clé de
    // cache fixes, sans jamais lire ni écrire zoneTileX/zoneTileY. Sans cette séparation, appeler
    // LoadDefaultOfflineCity() juste après une Zone réelle (ex : le serveur restaure la carte par
    // défaut après un combat de conquête, voir MatchSessionManager.RestoreDefaultMapOnServer)
    // trouverait le cache disque de CETTE Zone (qui existe forcément, il vient d'être généré) et la
    // rechargerait par erreur au lieu de la vraie ville par défaut embarquée dans Resources.
    public const float DefaultOfflineLatitude = 50.6927f;
    public const float DefaultOfflineLongitude = 3.1778f;
    private const string DefaultOfflineCacheFileName = "CityCache_Default.json";

    public void LoadDefaultOfflineCity()
    {
        // Peut être appelée en secours depuis FetchCityData (dont la coroutine se termine juste après)
        // ou directement depuis l'UI (bouton "Combat Urbain Hors-Ligne") : dans les deux cas on s'assure
        // qu'aucune autre génération ne continue en parallèle et ne vienne se superposer à celle-ci.
        CancelActiveGenerationAndClearCity();
        IsCityReady = false;

        // 1. Vérifier si un cache local persistant existe sur le disque pour la ville par défaut
        string cachePath = System.IO.Path.Combine(Application.persistentDataPath, DefaultOfflineCacheFileName);

        if (System.IO.File.Exists(cachePath))
        {
            try
            {
                string cachedJson = System.IO.File.ReadAllText(cachePath);
                if (!string.IsNullOrEmpty(cachedJson) && cachedJson.Length > 20)
                {
                    Debug.Log($"<color=green>[CityGenerator] 📂 Ville par défaut restaurée depuis le cache disque local : {DefaultOfflineCacheFileName}</color>");
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
        latitude = DefaultOfflineLatitude;
        longitude = DefaultOfflineLongitude;
        GeoProjection.SetCenter(latitude, longitude);
        CurrentGridCacheKey = "Default";
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
        // Voir le commentaire équivalent plus haut dans ce fichier : on ne réactive que les unités
        // qui étaient déjà actives avant ce masquage temporaire, sinon les unités pré-placées dans
        // la scène (désactivées par défaut, servant de modèle au déploiement manuel) réapparaissent
        // automatiquement sur la carte à chaque chargement.
        bool[] wasActive = new bool[allUnits.Length];
        for (int i = 0; i < allUnits.Length; i++) wasActive[i] = allUnits[i] != null && allUnits[i].gameObject.activeSelf;
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

        for (int i = 0; i < allUnits.Length; i++) { if (allUnits[i] != null && wasActive[i]) allUnits[i].gameObject.SetActive(true); }
        yield return null;

        foreach (var unit in allUnits) { if (unit != null && unit.gameObject.activeSelf) unit.OnNavMeshReady(); }

        if (GameManagerUI.Instance != null) GameManagerUI.Instance.HideLoading();
        IsCityReady = true;
    }

    // Nombre d'éléments OSM traités entre deux "yield return null". Étale le travail de génération
    // (triangulation, meshes, colliders) sur plusieurs frames pour ne jamais geler l'écran de
    // chargement, même sur une zone dense de plusieurs centaines de bâtiments.
    private const int BUILDINGS_PER_FRAME = 8;

    private IEnumerator ProcessDataCoroutine(string json)
    {
        // ÉQUITÉ MULTIJOUEUR (correctif 2026-09-05) : flux UnityEngine.Random réamorcé
        // DÉTERMINISTIQUEMENT ici, une seule fois, avant tout tirage — matériaux de mur/toit
        // (GetRandomWallMaterial/GetRandomRoofMaterial) ET mobilier urbain
        // (StreetPropsGenerator.PlaceStreetProps, qui pose de VRAIS colliders physiques intégrés au
        // NavMesh). Sans amorçage, ce flux global est initialisé par Unity de façon NON
        // déterministe au démarrage du process : deux exécutions de cette même méthode (client et
        // serveur, ou même client et client) sur EXACTEMENT le même JSON produisaient quand même un
        // mobilier urbain différent (nombre, position, présence d'un lampadaire/arbre/banc) — la
        // "disposition" au sens large restait donc différente d'un appareil à l'autre même une fois
        // les bâtiments identiques garantis.
        //
        // Le seed dérive de zoneTileX/zoneTileY (jamais de CurrentGridCacheKey.GetHashCode() : le
        // hash d'une string .NET n'est PAS garanti stable entre process/plateformes, contrairement à
        // de l'arithmétique entière simple) pour une vraie Zone, ou d'une constante fixe pour la
        // carte par défaut (bundle identique des deux côtés, un simple nombre arbitraire suffit).
        // Déterministe SEULEMENT si l'ORDRE et le NOMBRE d'appels à Random qui suivent sont
        // eux-mêmes une fonction pure du JSON (c'est le cas ici : un seul passage séquentiel sur
        // response.elements, jamais de branchement dépendant de l'horloge/du réseau) — garanti
        // désormais que client et serveur reçoivent le MÊME texte JSON (voir LoadZoneFromServerData).
        int citySeed = (CurrentGridCacheKey == "Default") ? 424242 : unchecked(zoneTileX * 73_856_093 ^ zoneTileY * 19_349_663);
        UnityEngine.Random.InitState(citySeed);

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

        HQBuildingIndex = -1; // Reset for next time

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
            
            // Variation de hauteur par lot, DÉTERMINISTE à partir de la géométrie du lot lui-même
            // (même hachage trigonométrique que JitterBuildingColor).
            //
            // C'était un UnityEngine.Random.Range, tiré d'un flux global jamais initialisé depuis la
            // tuile : le client et le serveur — qui génèrent chacun leur ville de leur côté, le réseau
            // ne transportant que les coordonnées de tuile — obtenaient donc des hauteurs DIFFÉRENTES
            // pour le même bâtiment. Après une escalade, le serveur plaçait l'unité à SA hauteur et le
            // joueur la voyait flotter jusqu'à ~3m au-dessus du toit ou enfoncée dedans jusqu'à la
            // taille. Deux instances du pool serveur ne s'accordaient pas non plus entre elles.
            float lotHeight = DeterministicLotHeight(buildingHeight, lotFootprint[0]);

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
            var destEnv = buildingGo.AddComponent<DestructibleEnvironment>();
            if (HQBuildingIndex != -1 && HQBuildingIndex == lotIndex) destEnv.isHQ = true;
            
            // Pre-calculate doors so we can make gaps in the walls
            GenerateDoorsAndWindows(buildingGo, structure, lotFootprint, lotHeight, partyWallEdges);

            // Mobilier urbain (lampadaires, bancs, poubelles, arbres) le long des façades extérieures.
            StreetPropsGenerator.PlaceStreetProps(buildingGo.transform, lotFootprint, structure.doors, partyWallEdges);

            // --- Create Sub-Meshes ---
            
            // 1. ROOF
            Mesh roofMesh = CreateHipRoofMesh(lotFootprint, roofIndices, lotHeight);
            GameObject roofGo = new GameObject("Roof");
            roofGo.transform.parent = buildingGo.transform;
            roofGo.AddComponent<MeshFilter>().sharedMesh = roofMesh;
            var roofRenderer = roofGo.AddComponent<MeshRenderer>();
            roofRenderer.sharedMaterial = GetRandomRoofMaterial();
            roofRenderer.enabled = false; // Désactivé par défaut pour le Streaming
            roofGo.AddComponent<MeshCollider>().sharedMesh = roofMesh;

            // 2. POLYGONE 2D AU SOL (TOUJOURS VISIBLE POUR LA VUE CARTE 2D)
            Mesh floorMesh = CreateFloorMesh(lotFootprint, roofIndices, 0.08f);
            GameObject floorGo = new GameObject("Footprint_2D");
            floorGo.transform.parent = buildingGo.transform;
            floorGo.AddComponent<MeshFilter>().sharedMesh = floorMesh;
            var floorRenderer = floorGo.AddComponent<MeshRenderer>();
            Material floorMat = Get2DBuildingMaterial();
            floorRenderer.sharedMaterial = floorMat;
            floorRenderer.enabled = true; // Actif pour la construction 2D des polygones
            floorGo.AddComponent<MeshCollider>().sharedMesh = floorMesh;

            // Teinte par bâtiment (garde le même Material partagé pour l'instancing GPU — voir
            // le commentaire sur GetRandomBuildingMaterial — seule la valeur d'instance change).
            if (floorMat != null)
            {
                Color tinted = JitterBuildingColor(Base2DBuildingColor, lotFootprint[0]);
                if (HQBuildingIndex != -1 && HQBuildingIndex == lotIndex) tinted = Color.yellow; // HIGHLIGHT HQ
                
                MaterialPropertyBlock tintBlock = new MaterialPropertyBlock();
                if (floorMat.HasProperty("_BaseColor")) tintBlock.SetColor("_BaseColor", tinted);
                if (floorMat.HasProperty("_Color")) tintBlock.SetColor("_Color", tinted);
                floorRenderer.SetPropertyBlock(tintBlock);
            }

            // Contour sombre du footprint (voir CreateOutlineMesh) — pas de collider, la sélection
            // 2D passe déjà par le collider du floor mesh ci-dessus.
            Mesh outlineMesh = CreateOutlineMesh(lotFootprint, 0.09f, 0.4f);
            if (outlineMesh != null)
            {
                GameObject outlineGo = new GameObject("Outline_2D");
                outlineGo.transform.parent = buildingGo.transform;
                outlineGo.AddComponent<MeshFilter>().sharedMesh = outlineMesh;
                var outlineRenderer = outlineGo.AddComponent<MeshRenderer>();
                outlineRenderer.sharedMaterial = Get2DOutlineMaterial();
                outlineRenderer.enabled = true;
            }

            // 3. WALLS
            Mesh wallsMesh = CreateWallsMesh(lotFootprint, lotHeight, structure.doors);
            GameObject wallsGo = new GameObject("Walls");
            wallsGo.transform.parent = buildingGo.transform;
            wallsGo.AddComponent<MeshFilter>().sharedMesh = wallsMesh;
            var wallsRenderer = wallsGo.AddComponent<MeshRenderer>();
            wallsRenderer.sharedMaterial = GetRandomWallMaterial();
            wallsRenderer.enabled = false; // Désactivé par défaut pour le Streaming
            
            if (HQBuildingIndex != -1 && HQBuildingIndex == lotIndex) 
            {
                MaterialPropertyBlock tintBlock = new MaterialPropertyBlock();
                tintBlock.SetColor("_BaseColor", new Color(1f, 0.84f, 0f));
                tintBlock.SetColor("_Color", new Color(1f, 0.84f, 0f));
                wallsRenderer.SetPropertyBlock(tintBlock);
            }

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

    private static readonly Color Base2DBuildingColor = new Color(0.24f, 0.30f, 0.38f, 0.95f);
    private static Material shared2DBuildingMaterial;

    private Material Get2DBuildingMaterial()
    {
        if (shared2DBuildingMaterial == null)
        {
            // Matériau 2D haute lisibilité pour le plan tactique (Ardoise tactique contrastée)
            shared2DBuildingMaterial = SafeMaterialFactory.CreateUnlit(Base2DBuildingColor);
        }
        return shared2DBuildingMaterial;
    }

    private static Material shared2DOutlineMaterial;

    private Material Get2DOutlineMaterial()
    {
        if (shared2DOutlineMaterial == null)
        {
            // Contour sombre fin : sans lui, des bâtiments adjacents à la même teinte de base
            // fusionnent visuellement en une seule masse indistincte vue du dessus.
            shared2DOutlineMaterial = SafeMaterialFactory.CreateUnlit(new Color(0.06f, 0.07f, 0.09f, 0.95f));
        }
        return shared2DOutlineMaterial;
    }

    /// <summary>Hauteur d'un lot, variée de ±1.5m autour de la hauteur nominale mais entièrement
    /// DÉTERMINÉE par la position du lot. Indispensable : client et serveur génèrent chacun leur
    /// propre ville à partir des seules coordonnées de tuile, donc toute valeur tirée d'un flux
    /// aléatoire les fait diverger sur la hauteur des toits, c'est-à-dire sur la position Y d'une
    /// unité perchée. Dériver la valeur de la géométrie évite en plus toute dépendance à l'ORDRE des
    /// appels, contrairement à un simple Random.InitState.
    ///
    /// Hash ENTIER pur (Novgov.Core.DeterministicHash), plus de trigonométrie (correctif 2026-09-05).
    /// La version précédente (`Mathf.Sin(x*a+y*b) * grand_facteur` puis `Mathf.Floor`) restait
    /// techniquement déterministe SUR UNE PLATEFORME DONNÉE, mais `sin()` n'est pas garantie
    /// bit-identique par IEEE754 entre la libm Android (Bionic/ARM) du client et la glibc Linux du
    /// serveur dédié — un écart d'un seul bit sur `Sin(x)`, amplifié par le grand facteur, pouvait en
    /// théorie faire basculer `Floor()` d'une unité entière et changer la hauteur du toit de ~3m
    /// entre les deux, pour le MÊME bâtiment. Un hash entier (XOR/shift/multiplication sur des
    /// entiers 32 bits) est lui garanti bit-identique sur toute plateforme .NET/Mono/IL2CPP.</summary>
    private static float DeterministicLotHeight(float nominalHeight, Vector2 seedPoint)
    {
        float unit = Novgov.Core.DeterministicHash.Unit01(seedPoint.x, seedPoint.y); // [0, 1)
        return nominalHeight - 1.5f + unit * 3f;  // [nominal-1.5, nominal+1.5)
    }

    /// <summary>Variation de luminosité déterministe par bâtiment : deux bâtiments à la même
    /// position produisent toujours la même teinte. Même hash entier que DeterministicLotHeight
    /// (voir son commentaire) — un décalage fixe de coordonnées (+1000,+1000) suffit à obtenir une
    /// séquence de hash INDÉPENDANTE de celle de la hauteur pour la MÊME position, sans reproduire
    /// la même valeur pour les deux usages.</summary>
    private static Color JitterBuildingColor(Color baseColor, Vector2 seedPoint)
    {
        float jitter = Novgov.Core.DeterministicHash.Unit01(seedPoint.x + 1000f, seedPoint.y + 1000f) - 0.5f; // [-0.5, 0.5)
        float scale = 1f + jitter * 0.22f; // ±11% de luminosité
        return new Color(
            Mathf.Clamp01(baseColor.r * scale),
            Mathf.Clamp01(baseColor.g * scale),
            Mathf.Clamp01(baseColor.b * scale),
            baseColor.a);
    }

    /// <summary>
    /// Ruban fin le long du périmètre (entre le footprint et son inset intérieur), pour distinguer
    /// chaque bâtiment de ses voisins dans la masse 2D. Réutilise InsetPolygon, déjà éprouvé sur
    /// des empreintes OSM concaves — donc pas de logique de triangulation supplémentaire à durcir.
    /// </summary>
    private Mesh CreateOutlineMesh(List<Vector2> outer, float yPos, float strokeWidth)
    {
        List<Vector2> inner = InsetPolygon(outer, strokeWidth);
        int n = outer.Count;
        if (inner.Count != n) return null;

        Vector3[] vertices = new Vector3[n * 2];
        Vector2[] uvs = new Vector2[n * 2];
        for (int i = 0; i < n; i++)
        {
            vertices[i] = new Vector3(outer[i].x, yPos, outer[i].y);
            vertices[n + i] = new Vector3(inner[i].x, yPos, inner[i].y);
        }

        List<int> triangles = new List<int>(n * 6);
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            triangles.Add(i); triangles.Add(next); triangles.Add(n + next);
            triangles.Add(i); triangles.Add(n + next); triangles.Add(n + i);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        return mesh;
    }

    // Pool partagé de matériaux de façade. Un Material tout neuf par bâtiment (ancien comportement)
    // rend le GPU Instancing/batching inopérant : même avec enableInstancing=true, Unity ne regroupe
    // en un seul draw call que les renderers qui pointent vers EXACTEMENT le même Material. Avec des
    // centaines de bâtiments, on se retrouvait avec autant de draw calls. Ici, on réutilise un petit
    // nombre de teintes pré-générées afin que la grande majorité des murs/toits partagent la même
    // référence de matériau et soient réellement instanciés ensemble.
    private const int BUILDING_MATERIAL_PALETTE_SIZE = 16;
    private static Material[] sharedWallMaterialPalette;

    private Material GetRandomWallMaterial()
    {
        if (buildingMaterial != null) return buildingMaterial;

        if (sharedWallMaterialPalette == null)
        {
            sharedWallMaterialPalette = new Material[BUILDING_MATERIAL_PALETTE_SIZE];
            for (int i = 0; i < BUILDING_MATERIAL_PALETTE_SIZE; i++)
            {
                Color col = UnityEngine.Random.ColorHSV(0f, 1f, 0.1f, 0.3f, 0.4f, 0.8f);
                sharedWallMaterialPalette[i] = SafeMaterialFactory.CreateLit(col);
            }
        }

        return sharedWallMaterialPalette[UnityEngine.Random.Range(0, BUILDING_MATERIAL_PALETTE_SIZE)];
    }

    // Palette distincte pour les toits (tons de toiture européenne : tuile terracotta / ardoise-zinc
    // sombre) — un toit ne doit jamais pouvoir piocher exactement la même teinte qu'un mur, sinon le
    // bâtiment se lit comme un bloc plat uniforme même avec la géométrie en pente ci-dessous.
    private static Material[] sharedRoofMaterialPalette;

    private Material GetRandomRoofMaterial()
    {
        if (buildingMaterial != null) return buildingMaterial;

        if (sharedRoofMaterialPalette == null)
        {
            sharedRoofMaterialPalette = new Material[BUILDING_MATERIAL_PALETTE_SIZE];
            for (int i = 0; i < BUILDING_MATERIAL_PALETTE_SIZE; i++)
            {
                Color col = (i % 3 == 0)
                    ? UnityEngine.Random.ColorHSV(0.55f, 0.65f, 0.03f, 0.12f, 0.22f, 0.42f)  // ardoise/zinc
                    : UnityEngine.Random.ColorHSV(0.02f, 0.06f, 0.35f, 0.55f, 0.35f, 0.55f); // tuile terracotta
                sharedRoofMaterialPalette[i] = SafeMaterialFactory.CreateLit(col);
            }
        }

        return sharedRoofMaterialPalette[UnityEngine.Random.Range(0, BUILDING_MATERIAL_PALETTE_SIZE)];
    }

    // Toit à pans générique (hip roof) avec une bande plate périphérique ("coursive") avant la
    // pente : TacticalPathManager.ConfirmerBuildingAction ("MONTER SUR LE TOIT") pose toujours
    // l'unité à Y = selectedBuilding.height (le niveau du larmier, sans lien avec la géométrie du
    // toit), et UnitAI_Movement.ExecuteClimbCoroutine la fait grimper le long de la façade pour
    // atterrir à quelques centimètres à peine du mur. Une PREMIÈRE version de ce toit faisait
    // démarrer la pente dès le bord (repli inset de 1.3m) : à cette distance du mur, le toit était
    // déjà remonté d'environ 25-30cm au-dessus du larmier, donnant l'impression que l'unité
    // apparaissait à moitié enfoncée dans le toit. La coursive plate (ROOF_WALKWAY_MARGIN) garantit
    // que toute la zone où une unité peut réellement se tenir reste exactement à la hauteur des
    // murs ; seule la pente au-delà (vers le centre) est remontée. Repli sur l'ancien toit plat si
    // l'inset dégénère (lot étroit type maison mitoyenne) : mieux vaut un toit plat correct qu'une
    // pente auto-intersectante.
    private const float ROOF_WALKWAY_MARGIN = 2.5f;
    private const float ROOF_PITCH_HEIGHT = 1.0f;
    private const float ROOF_HIP_INSET = 1.3f;

    private Mesh CreateHipRoofMesh(List<Vector2> footprint, List<int> flatRoofIndices, float height)
    {
        int n = footprint.Count;
        List<Vector2> walkway = InsetPolygon(footprint, ROOF_WALKWAY_MARGIN);
        List<Vector2> ridge = InsetPolygon(footprint, ROOF_WALKWAY_MARGIN + ROOF_HIP_INSET);

        float footprintArea = Mathf.Abs(Area(footprint));
        float ridgeArea = (ridge.Count == n && walkway.Count == n) ? Mathf.Abs(Area(ridge)) : 0f;
        List<int> ridgeIndices = (ridgeArea > footprintArea * 0.15f) ? Triangulate(ridge) : null;

        if (ridgeIndices == null || ridgeIndices.Count == 0)
        {
            return CreateRoofMesh(footprint, flatRoofIndices, height);
        }

        float ridgeY = height + ROOF_PITCH_HEIGHT;
        int walkwayOffset = n;
        int ridgeTopOffset = n * 2;
        int capOffset = n * 3;
        int bottomOffset = n * 3 + ridge.Count;

        Vector3[] vertices = new Vector3[bottomOffset + n];
        Vector2[] uvs = new Vector2[vertices.Length];
        List<int> triangles = new List<int>();

        // Larmier — bord du toit, hauteur inchangée (= haut des murs)
        for (int i = 0; i < n; i++)
        {
            vertices[i] = new Vector3(footprint[i].x, height, footprint[i].y);
            uvs[i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        // Bord intérieur de la coursive — même hauteur que le larmier (bande plate)
        for (int i = 0; i < n; i++)
        {
            vertices[walkwayOffset + i] = new Vector3(walkway[i].x, height, walkway[i].y);
            uvs[walkwayOffset + i] = new Vector2(walkway[i].x, walkway[i].y);
        }
        // Faîtage — haut des pentes
        for (int i = 0; i < n; i++)
        {
            vertices[ridgeTopOffset + i] = new Vector3(ridge[i].x, ridgeY, ridge[i].y);
            uvs[ridgeTopOffset + i] = new Vector2(ridge[i].x, ridge[i].y);
        }
        // Coursive plate (un quad par segment du périmètre, larmier -> bord intérieur)
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            triangles.Add(i); triangles.Add(next); triangles.Add(walkwayOffset + next);
            triangles.Add(i); triangles.Add(walkwayOffset + next); triangles.Add(walkwayOffset + i);
        }
        // Pentes (bord intérieur de la coursive -> faîtage)
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            triangles.Add(walkwayOffset + i); triangles.Add(walkwayOffset + next); triangles.Add(ridgeTopOffset + next);
            triangles.Add(walkwayOffset + i); triangles.Add(ridgeTopOffset + next); triangles.Add(ridgeTopOffset + i);
        }
        // Capuchon plat du faîtage (vertices dupliqués pour ne pas mélanger ses normales avec
        // celles des pentes lors du RecalculateNormals)
        for (int i = 0; i < ridge.Count; i++)
        {
            vertices[capOffset + i] = new Vector3(ridge[i].x, ridgeY, ridge[i].y);
            uvs[capOffset + i] = new Vector2(ridge[i].x, ridge[i].y);
        }
        for (int i = 0; i < ridgeIndices.Count; i++)
        {
            triangles.Add(capOffset + ridgeIndices[i]);
        }
        // Face inférieure, visible depuis l'intérieur du bâtiment (parité avec l'ancien toit plat)
        for (int i = 0; i < n; i++)
        {
            vertices[bottomOffset + i] = new Vector3(footprint[i].x, height - 0.2f, footprint[i].y);
            uvs[bottomOffset + i] = new Vector2(footprint[i].x, footprint[i].y);
        }
        for (int i = flatRoofIndices.Count - 1; i >= 0; i--)
        {
            triangles.Add(bottomOffset + flatRoofIndices[i]);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
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
                var doorInteract = doorObj.AddComponent<Novgov.Interaction.DoorInteraction>();
                doorInteract.Initialize(structure, door, sharedDoorMaterial);
                structure.doorInteractions.Add(doorInteract);

                doorIndex++;
            }
        }

        // UNE FENÊTRE = UN OBJET SÉLECTIONNABLE (correctif 2026-09-03).
        //
        // Les fenêtres étaient fusionnées en un seul maillage "Windows_Visual" sans collider et sans
        // composant d'interaction, là où chaque porte recevait son propre BoxCollider et son
        // DoorInteraction (voir juste au-dessus). Résultat : le raycast de sélection ne pouvait jamais
        // toucher de fenêtre, WindowInteraction n'était instancié NULLE PART, donc clickedWindow
        // restait toujours nul, ShowWindowMenu() était du code mort et NodeAction.GarnisonFenetre ne
        // pouvait pas être produite par le client. Toute la mécanique de garnison à la fenêtre (-75%
        // de dégâts, cône de tir de 140°) était ainsi inatteignable en solo comme en multijoueur,
        // alors que le serveur ET le client en avaient l'implémentation complète.
        if (structure.windows != null && structure.windows.Count > 0)
        {
            for (int wi = 0; wi < structure.windows.Count; wi++)
            {
                var win = structure.windows[wi];

                Vector3 n = win.outwardNormal;
                Vector3 t = new Vector3(-n.z, 0, n.x);
                Vector3 center = win.position + n * 0.05f;

                List<Vector3> verts = new List<Vector3>();
                List<Vector2> uvs = new List<Vector2>();
                List<int> tris = new List<int>();
                AddOpeningQuad(verts, uvs, tris, center, t, Vector3.up, 1.1f, 1.4f);

                GameObject winObj = new GameObject($"Window_{wi}");
                winObj.transform.SetParent(buildingGo.transform, false);

                MeshFilter mf = winObj.AddComponent<MeshFilter>();
                MeshRenderer mr = winObj.AddComponent<MeshRenderer>();
                Mesh m = new Mesh();
                m.vertices = verts.ToArray();
                m.uv = uvs.ToArray();
                m.triangles = tris.ToArray();
                m.RecalculateNormals();
                mf.sharedMesh = m;
                mr.sharedMaterial = sharedWindowMaterial;

                // Collider de sélection, un peu plus généreux que le quad pour rester atteignable au
                // doigt sur un téléphone.
                BoxCollider bc = winObj.AddComponent<BoxCollider>();
                bc.center = center;
                bc.size = new Vector3(1.3f, 1.6f, 0.5f);

                var windowInteract = winObj.AddComponent<Novgov.Interaction.WindowInteraction>();
                windowInteract.Initialize(structure, win, sharedWindowMaterial);
                structure.windowInteractions.Add(windowInteract);
            }
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
