using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// Génère procéduralement une icône d'application NOVGOV — aucune image externe nécessaire, même
/// philosophie que ProceduralIconFactory.cs/TacticalRadarUI.cs (synthèse de texture par pixel,
/// palette "tactique militaire nocturne" cohérente avec Assets/UI/Theme.tss) : silhouette de
/// ville en contre-jour, anneau de visée radar coupé bleu/rouge (les deux équipes).
/// À relancer si la palette du thème change — menu Novgov > UI > Générer l'icône de l'application.
/// </summary>
public static class AppIconGenerator
{
    private const int Size = 1024;
    private const string IconPath = "Assets/Icon/NovgovIcon.png";

    [MenuItem("Novgov/UI/Générer l'icône de l'application")]
    public static void GenerateIcon()
    {
        if (!Directory.Exists("Assets/Icon"))
            Directory.CreateDirectory("Assets/Icon");

        Color bg = new Color(0.05f, 0.07f, 0.09f, 1f);
        Color glowTint = new Color(0.10f, 0.16f, 0.24f, 1f);
        Color skylineDark = new Color(0.09f, 0.12f, 0.15f, 1f);
        Color windowAmber = new Color(1f, 0.75f, 0.25f, 1f);
        Color team1Blue = new Color(0.15f, 0.6f, 1f, 1f);
        Color team2Red = new Color(1f, 0.15f, 0.15f, 1f);
        Color tickColor = new Color(0.85f, 0.92f, 1f, 1f);

        Vector2 center = new Vector2(Size * 0.5f, Size * 0.5f);
        float radius = Size * 0.5f;
        float ringRadius = Size * 0.36f;
        float ringThickness = Size * 0.022f;
        float tickLen = Size * 0.07f;
        float tickThickness = Size * 0.014f;

        int[] buildingHeights = { 170, 260, 130, 320, 190, 380, 150, 300, 210, 260, 140, 340, 180, 270, 160, 300 };
        int buildingCount = buildingHeights.Length;
        float buildingWidth = (float)Size / buildingCount;
        float skylineBaseY = Size * 0.66f;

        var pixels = new Color32[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float dx = px - center.x, dy = py - center.y;
                float distFromCenter = Mathf.Sqrt(dx * dx + dy * dy);

                Color c = Color.Lerp(bg, glowTint, Mathf.Clamp01(1f - distFromCenter / radius) * 0.5f);

                // Silhouette de ville sur le tiers inférieur, en contre-jour.
                int bi = Mathf.Clamp((int)(x / buildingWidth), 0, buildingCount - 1);
                float buildingTopY = skylineBaseY - buildingHeights[bi] * (Size / 1024f);
                if (py > buildingTopY && py < Size * 0.78f)
                {
                    c = skylineDark;
                    float localX = x % (buildingWidth / 3f);
                    float localY = (py - buildingTopY) % 46f;
                    if (localX > 5f && localX < buildingWidth / 3f - 5f && localY > 8f && localY < 30f
                        && ((x * 7 + y * 13) % 5 != 0))
                    {
                        c = windowAmber;
                    }
                }

                // Anneau de visée radar, coupé en deux (équipe bleue / équipe rouge).
                float distToRing = Mathf.Abs(distFromCenter - ringRadius);
                if (distToRing < ringThickness)
                {
                    float angle = Mathf.Atan2(dy, dx);
                    c = angle > 0f ? team2Red : team1Blue;
                }

                // 4 repères façon réticule (haut/bas/gauche/droite).
                bool onVerticalTick = Mathf.Abs(dx) < tickThickness &&
                    (Mathf.Abs(dy + ringRadius) < tickLen || Mathf.Abs(dy - ringRadius) < tickLen);
                bool onHorizontalTick = Mathf.Abs(dy) < tickThickness &&
                    (Mathf.Abs(dx + ringRadius) < tickLen || Mathf.Abs(dx - ringRadius) < tickLen);
                if (onVerticalTick || onHorizontalTick) c = tickColor;

                pixels[y * Size + x] = c;
            }
        }

        Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();

        File.WriteAllBytes(IconPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceUpdate);

        TextureImporter importer = AssetImporter.GetAtPath(IconPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 1024;
            // Une icône compressée montre des artefacts de bloc bien plus visibles qu'une texture
            // de jeu normale (Unity le signale explicitement dans la Console) — un seul fichier,
            // le coût en taille de build est négligeable.
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            // Le réglage générique ci-dessus ne suffit pas : la plateforme Android a son propre
            // override (onglet "Android" de l'inspecteur) qui prime lors d'un build Android et
            // forçait toujours une compression ASTC/ETC2 par défaut — d'où l'avertissement
            // persistant malgré le réglage par défaut déjà à Uncompressed.
            TextureImporterPlatformSettings androidSettings = importer.GetPlatformTextureSettings("Android");
            androidSettings.overridden = true;
            androidSettings.format = TextureImporterFormat.RGBA32;
            androidSettings.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SetPlatformTextureSettings(androidSettings);

            importer.SaveAndReimport();
        }

        Texture2D iconAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (iconAsset != null)
        {
            PlayerSettings.SetIcons(NamedBuildTarget.Android, new[] { iconAsset }, IconKind.Application);
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { iconAsset }, IconKind.Application);
            EditorUtility.SetDirty(iconAsset);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[AppIconGenerator] Icône NOVGOV générée et assignée dans Player Settings : {IconPath}");
    }
}
