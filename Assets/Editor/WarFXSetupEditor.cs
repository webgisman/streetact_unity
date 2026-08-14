using UnityEngine;
using UnityEditor;

public class WarFXSetupEditor
{
    [MenuItem("Tools/Configurer WarFX (Auto-Setup)")]
    public static void SetupWarFX()
    {
        string resourcesPath = "Assets/Resources/WarFX";
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
        }
        if (!AssetDatabase.IsValidFolder(resourcesPath))
        {
            AssetDatabase.CreateFolder("Assets/Resources", "WarFX");
        }

        string mfSource = "Assets/JMO Assets/WarFX/_Effects (Mobile)/MuzzleFlashes/FPS/WFXMR_MF FPS RIFLE1.prefab";
        string mfDest = "Assets/Resources/WarFX/MuzzleFlash.prefab";
        
        string hitSource = "Assets/JMO Assets/WarFX/_Effects (Mobile)/Bullet Impacts/WFXMR_BImpact SoftBody.prefab";
        string hitDest = "Assets/Resources/WarFX/BulletImpact.prefab";

        CopyIfMissing(mfSource, mfDest);
        CopyIfMissing(hitSource, hitDest);

        AssetDatabase.Refresh();
        Debug.Log("<color=green><b>[WarFX Setup] Prefabs copiés avec succès dans Resources/WarFX ! Vous pouvez maintenant lancer le jeu.</b></color>");
    }

    private static void CopyIfMissing(string source, string dest)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(dest) == null)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(source) != null)
            {
                AssetDatabase.CopyAsset(source, dest);
                Debug.Log($"[WarFX Setup] Copié : {source} -> {dest}");
            }
            else
            {
                Debug.LogWarning($"[WarFX Setup] Attention : Impossible de trouver l'asset source : {source}. Avez-vous renommé ou déplacé les dossiers WarFX ?");
            }
        }
        else
        {
            Debug.Log($"[WarFX Setup] L'asset existe déjà à : {dest}");
        }
    }
}
