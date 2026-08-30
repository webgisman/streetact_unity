using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Novgov.Core;
using UnityEngine.Networking;

public class MapTileLoader : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Zoom des tuiles raster (texture du sol). Fixé à ZONE_ZOOM+2 (19) : chaque Zone de Conquête " +
             "(1 tuile Zoom 17) est donc composée d'exactement 16 sous-tuiles raster (grille 4x4, ~0.19m/px).")]
    [Range(10, 20)]
    public int zoom = CityGenerator.ZONE_ZOOM + 2;
    
    [Header("References")]
    [Tooltip("The ground plane. If left empty, will search for a GameObject named 'Sol' or create one.")]
    public GameObject solPlane;

    [Header("State")]
    public bool isMapLoaded = false;

    // Suivi du téléchargement de tuiles en cours. Sans ça, un second appel à LoadMap() (ex : le joueur
    // choisit le GPS ou une ville hors-ligne pendant que le chargement par défaut du Start() télécharge
    // encore ses tuiles) laisse deux coroutines de téléchargement tourner en parallèle sur le même "Sol" :
    // celle qui finit en dernier gagne, sans garantie que ce soit la bonne -> sol OSM décalé par rapport
    // aux bâtiments générés pour la position réellement choisie.
    private Coroutine activeMapLoad;

    [ContextMenu("Load Map")]
    public void LoadMap()
    {
        EnsureSolObject();

        if (Application.isPlaying)
        {
            CancelActiveMapLoad();
            isMapLoaded = false;
            activeMapLoad = StartCoroutine(DownloadAndApplyMap());
        }
        else
        {
            Debug.LogWarning("[MapTileLoader] Veuillez lancer le mode Play pour charger la carte.");
        }
    }

    private void CancelActiveMapLoad()
    {
        if (activeMapLoad != null)
        {
            StopCoroutine(activeMapLoad);
            activeMapLoad = null;
        }
    }

    private void EnsureSolObject()
    {
        if (solPlane == null)
        {
            solPlane = GameObject.Find("Sol");
            if (solPlane == null)
            {
                solPlane = new GameObject("Sol");
                solPlane.AddComponent<MeshFilter>();
                solPlane.AddComponent<MeshRenderer>();
            }
        }
    }

    private void Start()
    {
        LoadMap();
    }

    private IEnumerator DownloadAndApplyMap()
    {
        EnsureSolObject();

        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        if (cityGen == null)
        {
            Debug.LogError("[MapTileLoader] CityGenerator introuvable ! MapTileLoader a besoin de CityGenerator.");
            yield break;
        }

        float latitude = cityGen.latitude;
        float longitude = cityGen.longitude;

        GeoProjection.SetCenter(latitude, longitude);

        // Les 16 sous-tuiles (grille 4x4) qui composent exactement cette Zone de Conquête — plus de
        // calcul de rectangle englobant à partir d'un rayon, uniquement de l'arithmétique de tuiles.
        GeoProjection.TileToSubTileRange(cityGen.zoneTileX, cityGen.zoneTileY, CityGenerator.ZONE_ZOOM, zoom, out int minTileX, out int minTileY, out int subTileCount);
        int maxTileX = minTileX + subTileCount - 1;
        int maxTileY = minTileY + subTileCount - 1;

        int numTilesX = subTileCount;
        int numTilesY = subTileCount;

        Debug.Log($"[MapTileLoader] Chargement de {numTilesX * numTilesY} tuiles OSM (Zoom {zoom}) pour la Zone Z{CityGenerator.ZONE_ZOOM} ({cityGen.zoneTileX},{cityGen.zoneTileY})...");

        int tileSize = 256;
        Texture2D[,] tiles = new Texture2D[numTilesX, numTilesY];
        int downloadedCount = 0;
        
        string cacheFolder = Path.Combine(Application.persistentDataPath, "MapCache");
        if (!Directory.Exists(cacheFolder))
        {
            Directory.CreateDirectory(cacheFolder);
        }

        for (int x = 0; x < numTilesX; x++)
        {
            for (int y = 0; y < numTilesY; y++)
            {
                int tileX = minTileX + x;
                int tileY = minTileY + y;
                
                string cacheFileName = $"tile_{zoom}_{tileX}_{tileY}.png";
                string cacheFilePath = Path.Combine(cacheFolder, cacheFileName);
                
                bool validCache = false;
                if (File.Exists(cacheFilePath))
                {
                    try
                    {
                        byte[] fileData = File.ReadAllBytes(cacheFilePath);
                        if (fileData.Length > 500)
                        {
                            Texture2D tex = new Texture2D(2, 2);
                            if (tex.LoadImage(fileData) && tex.width == tileSize && tex.height == tileSize)
                            {
                                tiles[x, y] = tex;
                                validCache = true;
                            }
                            else
                            {
                                Destroy(tex);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        validCache = false;
                    }
                }

                if (!validCache)
                {
                    string url = $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";

                    using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(url))
                    {
                        www.SetRequestHeader("User-Agent", "NovgovTacticalGame/1.0 (Windows; Unity)");
                        www.timeout = 15;
                        
                        yield return www.SendWebRequest();

                        if (www.result == UnityWebRequest.Result.Success)
                        {
                            Texture2D downloadedTex = DownloadHandlerTexture.GetContent(www);
                            if (downloadedTex != null && downloadedTex.width == tileSize && downloadedTex.height == tileSize)
                            {
                                tiles[x, y] = downloadedTex;
                                
                                try
                                {
                                    byte[] pngBytes = downloadedTex.EncodeToPNG();
                                    File.WriteAllBytes(cacheFilePath, pngBytes);
                                }
                                catch (Exception) { }
                                
                                downloadedCount++;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[MapTileLoader] Erreur tuile {tileX},{tileY}: {www.error}");
                        }
                    }
                }

                // La branche "déjà en cache" ci-dessus (lecture disque + décodage PNG, sans le moindre
                // yield) pouvait bloquer un tick entier de la boucle des 16 sous-tuiles d'une Zone sans
                // jamais rendre la main — un vrai risque d'ANR mobile, et depuis que
                // MatchSessionManager.LoadZoneOnServer appelle ce même chemin côté serveur headless à
                // chaque combat de conquête, ça bloquait aussi la disponibilité du serveur de jeu lui-
                // même (voir rapport d'audit §1.6). Un yield par sous-tuile suffit à répartir le coût
                // sur plusieurs frames sans changer le résultat.
                yield return null;
            }
        }

        Debug.Log($"<color=cyan>[MapTileLoader] {downloadedCount} tuiles téléchargées, {numTilesX * numTilesY - downloadedCount} chargées du cache.</color>");

        Texture2D globalTexture = new Texture2D(numTilesX * tileSize, numTilesY * tileSize, TextureFormat.RGB24, false);
        globalTexture.filterMode = FilterMode.Bilinear;
        globalTexture.wrapMode = TextureWrapMode.Clamp;

        Color[] fillWhite = new Color[globalTexture.width * globalTexture.height];
        for (int i = 0; i < fillWhite.Length; i++) fillWhite[i] = Color.white;
        globalTexture.SetPixels(fillWhite);

        for (int x = 0; x < numTilesX; x++)
        {
            for (int y = 0; y < numTilesY; y++)
            {
                if (tiles[x, y] != null)
                {
                    int pixelX = x * tileSize;
                    int pixelY = (numTilesY - 1 - y) * tileSize;
                    
                    globalTexture.SetPixels(pixelX, pixelY, tileSize, tileSize, tiles[x, y].GetPixels());
                    Destroy(tiles[x, y]);
                }
            }
        }
        globalTexture.Apply();

        GenerateQuadMesh(latitude, longitude, minTileX, maxTileX, minTileY, maxTileY);
        ApplyTextureToMaterial(globalTexture);
    }

    public void ApplyDefaultOfflineMap()
    {
        CancelActiveMapLoad();
        EnsureSolObject();
        isMapLoaded = false;

        Texture2D globalTexture = Resources.Load<Texture2D>("DefaultMapTexture");
        if (globalTexture == null)
        {
            Debug.LogWarning("[MapTileLoader] DefaultMapTexture introuvable dans Resources.");
            return;
        }

        // Position et clé fixes, indépendantes de la Zone de Conquête actuellement chargée (si le
        // serveur vient de restaurer cette carte après un combat de conquête, cityGen.zoneTileX/Y
        // pointent encore vers CETTE Zone-là, pas vers la ville par défaut — voir le commentaire
        // équivalent sur CityGenerator.DefaultOfflineLatitude/Longitude).
        float latitude = CityGenerator.DefaultOfflineLatitude;
        float longitude = CityGenerator.DefaultOfflineLongitude;
        GeoProjection.SetCenter(latitude, longitude);

        // Tuile "virtuelle" centrée sur ce même point, uniquement pour dimensionner/positionner le
        // quad avec la même fonction que pour une vraie Zone — la ville par défaut n'est rattachée
        // à aucune vraie tuile Slippy Map.
        int defaultTileX = GeoProjection.LonToTileX(longitude, CityGenerator.ZONE_ZOOM);
        int defaultTileY = GeoProjection.LatToTileY(latitude, CityGenerator.ZONE_ZOOM);
        GeoProjection.TileToSubTileRange(defaultTileX, defaultTileY, CityGenerator.ZONE_ZOOM, zoom, out int minTileX, out int minTileY, out int subTileCount);
        int maxTileX = minTileX + subTileCount - 1;
        int maxTileY = minTileY + subTileCount - 1;

        GenerateQuadMesh(latitude, longitude, minTileX, maxTileX, minTileY, maxTileY);
        ApplyTextureToMaterial(globalTexture);
    }

    private void ApplyTextureToMaterial(Texture2D globalTexture)
    {
        EnsureSolObject();

        MeshRenderer mr = solPlane.GetComponent<MeshRenderer>();
        if (mr == null) mr = solPlane.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Standard")
                     ?? Shader.Find("Unlit/Texture");

        if (shader == null)
        {
            // Aucun shader disponible (build Dedicated Server) — la texture du sol est purement
            // cosmétique, sans incidence sur la simulation, on l'ignore proprement.
            return;
        }

        Material mat = new Material(shader);
        mat.name = "OSM_Ground_Material";
        
        mat.mainTexture = globalTexture;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", globalTexture);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", globalTexture);
            
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);

        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetTextureOffset("_BaseMap", Vector2.zero);
        }
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTextureScale("_MainTex", Vector2.one);
            mat.SetTextureOffset("_MainTex", Vector2.zero);
        }

        mr.sharedMaterial = mat;
        if (Application.isPlaying)
        {
            mr.material = mat;
        }

        Debug.Log($"<color=green>[MapTileLoader] ✅ Texture OSM appliquée avec succès au sol ({globalTexture.width}x{globalTexture.height}) avec le shader {shader.name} !</color>");
    }

    private void GenerateQuadMesh(float latitude, float longitude, int minTileX, int maxTileX, int minTileY, int maxTileY)
    {
        EnsureSolObject();

        GeoProjection.SetCenter(latitude, longitude);

        double topLeftLat = GeoProjection.TileYToLat(minTileY, zoom);
        double topLeftLon = GeoProjection.TileXToLon(minTileX, zoom);
        double bottomRightLat = GeoProjection.TileYToLat(maxTileY + 1, zoom);
        double bottomRightLon = GeoProjection.TileXToLon(maxTileX + 1, zoom);

        Vector3 topLeftUnity = GeoProjection.CoordinateToWorldPoint(topLeftLat, topLeftLon);
        Vector3 bottomRightUnity = GeoProjection.CoordinateToWorldPoint(bottomRightLat, bottomRightLon);
        
        Vector3[] vertices = new Vector3[4];
        vertices[0] = new Vector3(topLeftUnity.x, -0.05f, bottomRightUnity.z);
        vertices[1] = new Vector3(bottomRightUnity.x, -0.05f, bottomRightUnity.z);
        vertices[2] = new Vector3(topLeftUnity.x, -0.05f, topLeftUnity.z);
        vertices[3] = new Vector3(bottomRightUnity.x, -0.05f, topLeftUnity.z);

        Vector2[] uvs = new Vector2[4];
        uvs[0] = new Vector2(0, 0);
        uvs[1] = new Vector2(1, 0);
        uvs[2] = new Vector2(0, 1);
        uvs[3] = new Vector2(1, 1);

        int[] triangles = new int[6] { 0, 2, 1, 2, 3, 1 };

        Mesh mesh = new Mesh();
        mesh.name = "OSM_Map_Quad";
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        MeshFilter mf = solPlane.GetComponent<MeshFilter>();
        if (mf == null) mf = solPlane.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshCollider mc = solPlane.GetComponent<MeshCollider>();
        if (mc != null) Destroy(mc);

        BoxCollider bc = solPlane.GetComponent<BoxCollider>();
        if (bc == null) bc = solPlane.AddComponent<BoxCollider>();
        
        float width = Mathf.Abs(bottomRightUnity.x - topLeftUnity.x);
        float height = Mathf.Abs(topLeftUnity.z - bottomRightUnity.z);
        Vector3 center = new Vector3((topLeftUnity.x + bottomRightUnity.x) / 2f, -0.55f, (topLeftUnity.z + bottomRightUnity.z) / 2f);
        bc.center = center;
        bc.size = new Vector3(Mathf.Max(width, 10f), 1.0f, Mathf.Max(height, 10f));

        solPlane.transform.position = Vector3.zero;
        solPlane.transform.rotation = Quaternion.identity;
        solPlane.transform.localScale = Vector3.one;

        isMapLoaded = true;
        Debug.Log($"<color=green>[MapTileLoader] ✅ Maillage Quad généré ({width:F1}m x {height:F1}m) centré sur Sol !</color>");
        // Diagnostic d'alignement : à comparer avec la ligne "[CityGenerator] Zone couverte" pour
        // détecter un décalage entre le fond de carte (raster) et les bâtiments (vecteur OSM).
        Debug.Log($"<color=yellow>[MapTileLoader] 📍 Coins du sol : X[{topLeftUnity.x:F1} , {bottomRightUnity.x:F1}] Z[{bottomRightUnity.z:F1} , {topLeftUnity.z:F1}]</color>");
    }
}
