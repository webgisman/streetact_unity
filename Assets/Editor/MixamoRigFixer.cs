using UnityEngine;
using UnityEditor;

public class MixamoRigFixer
{
    [MenuItem("Tools/Corriger les Animations (Mettre en Humanoid + Bake Y)")]
    public static void FixRigs()
    {
        string[] guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets" });
        bool changedAny = false;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.ToLower().EndsWith(".fbx"))
            {
                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer != null)
                {
                    bool changed = false;

                    // 1. Forcer le type d'animation en Humanoid
                    if (importer.animationType != ModelImporterAnimationType.Human)
                    {
                        importer.animationType = ModelImporterAnimationType.Human;
                        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                        changed = true;
                    }

                    // 2. Forcer le "Bake Into Pose" pour la position Y et la rotation
                    ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
                    if (clips != null && clips.Length > 0)
                    {
                        for (int i = 0; i < clips.Length; i++)
                        {
                            if (!clips[i].lockRootHeightY)
                            {
                                clips[i].lockRootHeightY = true; // Bake Into Pose Y
                                clips[i].keepOriginalPositionY = true;
                                changed = true;
                            }
                            if (!clips[i].lockRootPositionXZ)
                            {
                                clips[i].lockRootPositionXZ = true; // Bake Into Pose XZ
                                clips[i].keepOriginalPositionXZ = true;
                                changed = true;
                            }
                            if (!clips[i].lockRootRotation)
                            {
                                clips[i].lockRootRotation = true; // Bake Into Pose Rotation
                                clips[i].keepOriginalOrientation = true;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            importer.clipAnimations = clips;
                        }
                    }

                    if (changed)
                    {
                        importer.SaveAndReimport();
                        Debug.Log($"[MixamoRigFixer] Converti et Baké : {path}");
                        changedAny = true;
                    }
                }
            }
        }

        if (changedAny)
        {
            Debug.Log("<color=green><b>[MixamoRigFixer] Succès ! Toutes les animations FBX ont été converties en Humanoid et fixées au sol (Bake Into Pose).</b></color>");
        }
        else
        {
            Debug.Log("[MixamoRigFixer] Toutes les animations étaient déjà configurées correctement.");
        }
    }
}
