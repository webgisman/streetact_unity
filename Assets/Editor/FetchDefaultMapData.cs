using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System.IO;
using System.Collections;

public class FetchDefaultMapData : EditorWindow
{
    private static IEnumerator currentRoutine;

    [MenuItem("Tools/StreetAct/Télécharger la carte hors-ligne (Default)")]
    public static void FetchData()
    {
        currentRoutine = DownloadRoutine();
        EditorApplication.update += EditorUpdate;
    }

    private static void EditorUpdate()
    {
        if (currentRoutine != null)
        {
            if (!currentRoutine.MoveNext())
            {
                currentRoutine = null;
                EditorApplication.update -= EditorUpdate;
            }
        }
        else
        {
            EditorApplication.update -= EditorUpdate;
        }
    }

    private static IEnumerator DownloadRoutine()
    {
        float latitude = 50.6927f;
        float longitude = 3.1778f;
        float radius = 250f;
        int zoom = 18;

        string resourcesPath = "Assets/Resources";
        if (!Directory.Exists(resourcesPath)) Directory.CreateDirectory(resourcesPath);

        // 1. JSON
        Debug.Log("Téléchargement du JSON Overpass...");
        string query = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[out:json][timeout:90];\n(\n  way[\"building\"](around:{0},{1},{2});\n  relation[\"building\"](around:{0},{1},{2});\n);\nout geom;",
            radius, latitude, longitude);

        WWWForm form = new WWWForm();
        form.AddField("data", query);

        using (UnityWebRequest webRequest = UnityWebRequest.Post("https://overpass-api.de/api/interpreter", form))
        {
            webRequest.timeout = 90;
            webRequest.SendWebRequest();
            
            while (!webRequest.isDone) yield return null;

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Erreur JSON: " + webRequest.error);
                yield break;
            }
            
            File.WriteAllText(resourcesPath + "/DefaultCityData.json", webRequest.downloadHandler.text);
            Debug.Log("JSON sauvegardé !");
        }

        // 2. OSM Tiles -> Single Texture
        Debug.Log("Génération de la texture globale OSM...");
        
        double latRad = latitude * Mathf.Deg2Rad;
        double metersPerDegLat = 111320.0;
        double metersPerDegLon = (40075000.0 * Mathf.Cos((float)latRad)) / 360.0;

        double deltaLat = radius / metersPerDegLat;
        double deltaLon = radius / metersPerDegLon;

        double minLat = latitude - deltaLat;
        double maxLat = latitude + deltaLat;
        double minLon = longitude - deltaLon;
        double maxLon = longitude + deltaLon;

        int minTileX = (int)(System.Math.Floor((minLon + 180.0) / 360.0 * (1 << zoom)));
        int maxTileX = (int)(System.Math.Floor((maxLon + 180.0) / 360.0 * (1 << zoom)));
        int minTileY = (int)(System.Math.Floor((1 - System.Math.Log(System.Math.Tan(maxLat * System.Math.PI / 180.0) + 1.0 / System.Math.Cos(maxLat * System.Math.PI / 180.0)) / System.Math.PI) / 2.0 * (1 << zoom)));
        int maxTileY = (int)(System.Math.Floor((1 - System.Math.Log(System.Math.Tan(minLat * System.Math.PI / 180.0) + 1.0 / System.Math.Cos(minLat * System.Math.PI / 180.0)) / System.Math.PI) / 2.0 * (1 << zoom)));

        int numTilesX = maxTileX - minTileX + 1;
        int numTilesY = maxTileY - minTileY + 1;

        Texture2D[,] tiles = new Texture2D[numTilesX, numTilesY];

        for (int x = 0; x < numTilesX; x++)
        {
            for (int y = 0; y < numTilesY; y++)
            {
                int tileX = minTileX + x;
                int tileY = minTileY + y;
                string url = $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";

                using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(url))
                {
                    www.SetRequestHeader("User-Agent", "UnityTacticalGame/1.0");
                    www.SendWebRequest();
                    
                    while (!www.isDone) yield return null;

                    if (www.result == UnityWebRequest.Result.Success)
                    {
                        tiles[x, y] = DownloadHandlerTexture.GetContent(www);
                    }
                }
            }
        }

        int tileSize = 256;
        Texture2D globalTexture = new Texture2D(numTilesX * tileSize, numTilesY * tileSize, TextureFormat.RGB24, false);

        for (int x = 0; x < numTilesX; x++)
        {
            for (int y = 0; y < numTilesY; y++)
            {
                if (tiles[x, y] != null)
                {
                    int pixelX = x * tileSize;
                    int pixelY = (numTilesY - 1 - y) * tileSize;
                    globalTexture.SetPixels(pixelX, pixelY, tileSize, tileSize, tiles[x, y].GetPixels());
                    DestroyImmediate(tiles[x, y]);
                }
            }
        }
        globalTexture.Apply();

        byte[] pngData = globalTexture.EncodeToPNG();
        File.WriteAllBytes(resourcesPath + "/DefaultMapTexture.png", pngData);
        DestroyImmediate(globalTexture);
        
        AssetDatabase.Refresh();
        Debug.Log("Texture globale sauvegardée !");
        
        // Setup texture importer
        TextureImporter importer = AssetImporter.GetAtPath("Assets/Resources/DefaultMapTexture.png") as TextureImporter;
        if (importer != null)
        {
            importer.isReadable = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        
        Debug.Log("Mode Hors-Ligne Prêt ! JSON et Texture générés.");
    }
}
