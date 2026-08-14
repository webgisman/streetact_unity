using UnityEngine;
using UnityEditor;

public class MaterialSetupTool : Editor
{
    [MenuItem("StreetAct/3. Assigner Textures Briques Rouges")]
    public static void SetupMaterial()
    {
        string folderPath = "Assets/Textures/RedBrick";
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            EditorUtility.DisplayDialog("Erreur", "Le dossier Assets/Textures/RedBrick est introuvable ! Vérifiez le nom et l'emplacement.", "OK");
            return;
        }

        // 1. Créer le Material s'il n'existe pas déjà
        string matPath = "Assets/Textures/RedBrick/RedBrickMaterial.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            // Compatible avec URP ou Pipeline Standard
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }

        // 2. Trouver et assigner les textures automatiquement
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
        foreach(string guid in guids)
        {
            string texPath = AssetDatabase.GUIDToAssetPath(guid);
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            string lowerPath = texPath.ToLower();
            
            // Texture Diffuse/Couleur
            if (lowerPath.Contains("_diff") || lowerPath.Contains("diffuse") || lowerPath.Contains("albedo") || lowerPath.Contains("color"))
            {
                mat.SetTexture("_BaseMap", tex); // URP
                mat.SetTexture("_MainTex", tex); // Standard
            }
            // Texture Displacement (Height)
            else if (lowerPath.Contains("_disp") || lowerPath.Contains("height"))
            {
                mat.SetTexture("_ParallaxMap", tex);
            }
            // Texture Normale (Relief)
            else if (lowerPath.Contains("_n") || lowerPath.Contains("normal"))
            {
                mat.SetTexture("_BumpMap", tex);
                
                // Forcer l'importateur Unity à comprendre que c'est une Normal Map
                TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
                if (importer != null && importer.textureType != TextureImporterType.NormalMap)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.SaveAndReimport();
                }
            }
            // Texture Rugosité/Métallique
            else if (lowerPath.Contains("_r") || lowerPath.Contains("rough") || lowerPath.Contains("metallic"))
            {
                mat.SetTexture("_MetallicGlossMap", tex); // URP / Standard
            }
        }

        // Optionnel : Régler le tiling (répétition). 
        // 1x1 est parfait car nos UVs de bâtiments sont déjà calculés en mètres réels !
        mat.mainTextureScale = new Vector2(1f, 1f);

        AssetDatabase.SaveAssets();

        // 3. Assigner automatiquement le matériel au CityGenerator de la scène
        CityGenerator cityGen = Object.FindAnyObjectByType<CityGenerator>();
        if (cityGen != null)
        {
            SerializedObject so = new SerializedObject(cityGen);
            so.Update();
            so.FindProperty("buildingMaterial").objectReferenceValue = mat;
            so.ApplyModifiedProperties();
            
            EditorUtility.DisplayDialog("Succès !", "Le Matériel de Briques Rouges a été généré avec toutes vos textures et assigné automatiquement aux bâtiments !", "Génial !");
        }
        else
        {
            EditorUtility.DisplayDialog("Attention", "Le Matériel a été créé avec succès, mais aucun CityGenerator n'a été trouvé dans la scène pour l'assigner automatiquement.", "OK");
        }
    }
}
