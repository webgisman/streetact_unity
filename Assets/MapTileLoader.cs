using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class MapTileLoader : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Zoom level for OpenStreetMap tiles (higher = sharper, but more tiles to download)")]
    [Range(10, 19)]
    public int zoom = 18;
    
    [Header("References")]
    [Tooltip("The ground plane. If left empty, will search for a GameObject named 'Sol' or create one.")]
    public GameObject solPlane;

    [Header("State")]
    public bool isMapLoaded = false;

    [ContextMenu("Load Map")]
    public void LoadMap()
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
        
        // Ensure Coroutines run properly by executing in Play mode
        if (Application.isPlaying)
        {
            StartCoroutine(DownloadAndApplyMap());
        }
        else
        {
            Debug.LogWarning("Veuillez lancer le mode Play pour télécharger la carte. Les Coroutines ne s'exécutent pas complètement en mode Édition.");
        }
    }

    private void Start()
    {
        // Automatically load the map when we enter Play mode
        LoadMap();
    }

    private IEnumerator DownloadAndApplyMap()
    {
        // 0. Get coordinates from CityGenerator to guarantee 100% perfect alignment
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        if (cityGen == null)
        {
            Debug.LogError("CityGenerator introuvable ! Le MapTileLoader a besoin du CityGenerator pour se synchroniser.");
            yield break;
        }

        float latitude = cityGen.latitude;
        float longitude = cityGen.longitude;
        float radius = cityGen.radius;

        // 1. Calculate bounding box in lat/lon based on radius
        double latRad = latitude * Mathf.Deg2Rad;
        double metersPerDegLat = 111320.0;
        double metersPerDegLon = (40075000.0 * Mathf.Cos((float)latRad)) / 360.0;

        double deltaLat = radius / metersPerDegLat;
        double deltaLon = radius / metersPerDegLon;

        double minLat = latitude - deltaLat;
        double maxLat = latitude + deltaLat;
        double minLon = longitude - deltaLon;
        double maxLon = longitude + deltaLon;

        // 2. Calculate OSM tile coordinates
        int minTileX = LonToTileX(minLon, zoom);
        int maxTileX = LonToTileX(maxLon, zoom);
        int minTileY = LatToTileY(maxLat, zoom); // Max lat corresponds to smaller Y in OSM
        int maxTileY = LatToTileY(minLat, zoom); // Min lat corresponds to bigger Y in OSM

        int numTilesX = maxTileX - minTileX + 1;
        int numTilesY = maxTileY - minTileY + 1;

        Debug.Log($"Downloading {numTilesX * numTilesY} tiles from OpenStreetMap...");

        // 3. Download or Load all tiles from Cache
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
                
                if (File.Exists(cacheFilePath))
                {
                    // Charger depuis le cache local (extrêmement rapide)
                    byte[] fileData = File.ReadAllBytes(cacheFilePath);
                    Texture2D tex = new Texture2D(2, 2);
                    tex.LoadImage(fileData); // Unity décode automatiquement l'image PNG/JPG
                    tiles[x, y] = tex;
                }
                else
                {
                    // Télécharger depuis Internet
                    string url = $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";

                    using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(url))
                    {
                        // OSM usage policy requires a valid User-Agent
                        www.SetRequestHeader("User-Agent", "UnityTacticalGame/1.0");
                        
                        yield return www.SendWebRequest();

                        if (www.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogWarning($"[MapTileLoader] Échec du téléchargement de la tuile {tileX},{tileY} ({www.error}). Bascule automatique sur la carte de secours intégrée.");
                            ApplyDefaultOfflineMap();
                            isMapLoaded = true;
                            yield break;
                        }
                        else
                        {
                            Texture2D downloadedTex = DownloadHandlerTexture.GetContent(www);
                            tiles[x, y] = downloadedTex;
                            
                            // Sauvegarder dans le cache pour la prochaine fois
                            byte[] pngBytes = downloadedTex.EncodeToPNG();
                            File.WriteAllBytes(cacheFilePath, pngBytes);
                        }
                    }
                    
                    downloadedCount++;
                }
            }
        }
        
        if (downloadedCount > 0)
            Debug.Log($"[MapTileLoader] {downloadedCount} nouvelles tuiles téléchargées et mises en cache. Les autres ont été chargées depuis le disque.");
        else
            Debug.Log($"[MapTileLoader] Toutes les tuiles ont été chargées instantanément depuis le cache local !");

        // 4. Assemble the global texture
        int tileSize = 256;
        Texture2D globalTexture = new Texture2D(numTilesX * tileSize, numTilesY * tileSize, TextureFormat.RGB24, false);
        globalTexture.filterMode = FilterMode.Bilinear;
        globalTexture.wrapMode = TextureWrapMode.Clamp;

        for (int x = 0; x < numTilesX; x++)
        {
            for (int y = 0; y < numTilesY; y++)
            {
                if (tiles[x, y] != null)
                {
                    // Unity texture Y-axis is bottom-up, OSM tile Y-axis is top-down
                    int pixelX = x * tileSize;
                    int pixelY = (numTilesY - 1 - y) * tileSize;
                    
                    globalTexture.SetPixels(pixelX, pixelY, tileSize, tileSize, tiles[x, y].GetPixels());
                    Destroy(tiles[x, y]); // Free memory of individual tile textures
                }
            }
        }
        globalTexture.Apply();

        // 6. Generate a custom Quad Mesh
        GenerateQuadMesh(latitude, longitude, minTileX, maxTileX, minTileY, maxTileY);
    }

    public void ApplyDefaultOfflineMap()
    {
        Texture2D globalTexture = Resources.Load<Texture2D>("DefaultMapTexture");
        if (globalTexture == null)
        {
            Debug.LogError("La texture DefaultMapTexture n'a pas été trouvée dans Resources !");
            return;
        }

        ApplyTextureToMaterial(globalTexture);

        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        if (cityGen != null)
        {
            float latitude = cityGen.latitude;
            float longitude = cityGen.longitude;
            float radius = cityGen.radius;

            double latRad = latitude * Mathf.Deg2Rad;
            double metersPerDegLat = 111320.0;
            double metersPerDegLon = (40075000.0 * Mathf.Cos((float)latRad)) / 360.0;

            double deltaLat = radius / metersPerDegLat;
            double deltaLon = radius / metersPerDegLon;

            int minTileX = LonToTileX(longitude - deltaLon, zoom);
            int maxTileX = LonToTileX(longitude + deltaLon, zoom);
            int minTileY = LatToTileY(latitude + deltaLat, zoom);
            int maxTileY = LatToTileY(latitude - deltaLat, zoom);

            GenerateQuadMesh(latitude, longitude, minTileX, maxTileX, minTileY, maxTileY);
        }
    }

    private void ApplyTextureToMaterial(Texture2D globalTexture)
    {
        MeshRenderer mr = solPlane.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Material mat = mr.sharedMaterial;
            if (mat == null || mat.name == "Default-Material")
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                mat = new Material(shader);
                mr.sharedMaterial = mat;
            }
            
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", globalTexture);
            else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", globalTexture);
                
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);

            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTextureScale("_BaseMap", Vector2.one);
                mat.SetTextureOffset("_BaseMap", Vector2.zero);
            }
            else if (mat.HasProperty("_MainTex"))
            {
                mat.SetTextureScale("_MainTex", Vector2.one);
                mat.SetTextureOffset("_MainTex", Vector2.zero);
            }
        }
    }

    private void GenerateQuadMesh(float latitude, float longitude, int minTileX, int maxTileX, int minTileY, int maxTileY)
    {
        double topLeftLat = TileYToLat(minTileY, zoom);
        double topLeftLon = TileXToLon(minTileX, zoom);
        double bottomRightLat = TileYToLat(maxTileY + 1, zoom);
        double bottomRightLon = TileXToLon(maxTileX + 1, zoom);

        Vector3 topLeftUnity = CoordinateToWorldPoint(topLeftLat, topLeftLon, latitude, longitude);
        Vector3 bottomRightUnity = CoordinateToWorldPoint(bottomRightLat, bottomRightLon, latitude, longitude);
        
        Vector3[] vertices = new Vector3[4];
        vertices[0] = new Vector3(topLeftUnity.x, -0.1f, bottomRightUnity.z); // Bottom-Left
        vertices[1] = new Vector3(bottomRightUnity.x, -0.1f, bottomRightUnity.z); // Bottom-Right
        vertices[2] = new Vector3(topLeftUnity.x, -0.1f, topLeftUnity.z); // Top-Left
        vertices[3] = new Vector3(bottomRightUnity.x, -0.1f, topLeftUnity.z); // Top-Right

        Vector2[] uvs = new Vector2[4];
        uvs[0] = new Vector2(0, 0);
        uvs[1] = new Vector2(1, 0);
        uvs[2] = new Vector2(0, 1);
        uvs[3] = new Vector2(1, 1);

        int[] triangles = new int[6] { 0, 2, 1, 2, 3, 1 };

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();

        MeshFilter mf = solPlane.GetComponent<MeshFilter>();
        if (mf == null) mf = solPlane.AddComponent<MeshFilter>();
        mf.mesh = mesh;

        // Remplacement par un BoxCollider géant optimisé pour éviter le warning des triangles > 500 unités
        MeshCollider mc = solPlane.GetComponent<MeshCollider>();
        if (mc != null) Destroy(mc);

        BoxCollider bc = solPlane.GetComponent<BoxCollider>();
        if (bc == null) bc = solPlane.AddComponent<BoxCollider>();
        
        float width = Mathf.Abs(bottomRightUnity.x - topLeftUnity.x);
        float height = Mathf.Abs(topLeftUnity.z - bottomRightUnity.z);
        Vector3 center = new Vector3((topLeftUnity.x + bottomRightUnity.x) / 2f, -0.6f, (topLeftUnity.z + bottomRightUnity.z) / 2f);
        bc.center = center;
        bc.size = new Vector3(Mathf.Max(width, 10f), 1.0f, Mathf.Max(height, 10f));

        solPlane.transform.position = Vector3.zero;
        solPlane.transform.rotation = Quaternion.identity;
        solPlane.transform.localScale = Vector3.one;

        isMapLoaded = true;
        Debug.Log("Map loaded, custom mesh generated, and perfectly aligned on 'Sol'!");
    }

    #region Coordinate Conversions

    private int LonToTileX(double lon, int zoom)
    {
        return (int)(Math.Floor((lon + 180.0) / 360.0 * (1 << zoom)));
    }

    private int LatToTileY(double lat, int zoom)
    {
        return (int)(Math.Floor((1 - Math.Log(Math.Tan(lat * Math.PI / 180.0) + 1.0 / Math.Cos(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * (1 << zoom)));
    }

    private double TileXToLon(int x, int zoom)
    {
        return x / (double)(1 << zoom) * 360.0 - 180.0;
    }

    private double TileYToLat(int y, int zoom)
    {
        double n = Math.PI - 2.0 * Math.PI * y / (double)(1 << zoom);
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
    }

    private Vector3 CoordinateToWorldPoint(double lat, double lon, float centerLat, float centerLon)
    {
        // Use Web Mercator (EPSG:3857) to match OSM tiles perfectly
        double R = 6378137.0; // Earth radius in meters
        
        // Center coordinates in Web Mercator
        double centerLonRad = centerLon * Math.PI / 180.0;
        double centerLatRad = centerLat * Math.PI / 180.0;
        double centerX = R * centerLonRad;
        double centerY = R * Math.Log(Math.Tan(Math.PI / 4.0 + centerLatRad / 2.0));

        // Target coordinates in Web Mercator
        double lonRad = lon * Math.PI / 180.0;
        double latRad = lat * Math.PI / 180.0;
        double x = R * lonRad;
        double y = R * Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));

        // Scale to actual real-world meters at this latitude to avoid size distortion
        double scale = Math.Cos(centerLat * Math.PI / 180.0);

        return new Vector3((float)((x - centerX) * scale), 0, (float)((y - centerY) * scale));
    }

    #endregion
}
