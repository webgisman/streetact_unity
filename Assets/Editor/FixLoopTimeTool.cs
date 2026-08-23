using UnityEngine;
using UnityEditor;

public class FixLoopTimeTool : Editor
{
    [MenuItem("Novgov/4. Activer le Loop et Fixer les Saccades d'Animation (Bake Into Pose)")]
    public static void FixLoopTime()
    {
        // On cible tous les FBX du projet pour être sûr de corriger le soldat ET ses animations
        string[] allFbx = AssetDatabase.FindAssets("t:Model");
        int count = 0;
        
        foreach (string guid in allFbx)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.ToLower().EndsWith(".fbx")) continue;

            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                bool changed = false;

                // 1. FORCER LE RIG EN HUMANOID (CRITIQUE !)
                // Sans ça, Unity ne reconnaît pas le bassin (Hips) comme racine,
                // donc le "Bake Into Pose" ne fonctionne pas et le mesh avance puis se téléporte.
                if (importer.animationType != ModelImporterAnimationType.Human)
                {
                    importer.animationType = ModelImporterAnimationType.Human;
                    changed = true;
                }

                // 2. FORCER LE BAKE INTO POSE ET LE LOOP TIME
                ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (!clips[i].loopTime)
                        {
                            clips[i].loopTime = true;
                            changed = true;
                        }

                        if (!clips[i].lockRootPositionXZ || !clips[i].lockRootRotation || !clips[i].lockRootHeightY)
                        {
                            clips[i].lockRootPositionXZ = true;
                            clips[i].lockRootRotation = true;
                            clips[i].lockRootHeightY = true;
                            
                            clips[i].keepOriginalPositionXZ = true;
                            clips[i].keepOriginalPositionY = true;
                            clips[i].keepOriginalOrientation = true;
                            
                            changed = true;
                        }
                    }
                    if (changed) importer.clipAnimations = clips;
                }
                
                if (changed)
                {
                    importer.SaveAndReimport();
                    count++;
                }
            }
        }

        // 3. CORRECTION CRITIQUE : Assigner l'Avatar aux Animators de la scène !
        // Si le composant Animator n'a pas d'Avatar assigné, Unity ignore qu'il est "Human"
        // et continue d'avancer/reculer le bassin.
        int avatarCount = 0;
        Animator[] animators = Object.FindObjectsByType<Animator>(FindObjectsInactive.Exclude);
        foreach (Animator anim in animators)
        {
            if (anim.avatar == null)
            {
                // Trouver le premier Avatar disponible dans le projet
                string[] avatarGuids = AssetDatabase.FindAssets("t:Avatar");
                if (avatarGuids.Length > 0)
                {
                    string avatarPath = AssetDatabase.GUIDToAssetPath(avatarGuids[0]);
                    Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
                    if (avatar != null)
                    {
                        anim.avatar = avatar;
                        EditorUtility.SetDirty(anim.gameObject);
                        avatarCount++;
                    }
                }
            }
        }
        
        EditorUtility.DisplayDialog("Animations Corrigées", 
            $"{count} fichiers FBX ont été convertis en Humanoid avec 'Bake Into Pose'.\n" +
            $"{avatarCount} composants Animator ont été mis à jour avec un Avatar.\n\n" +
            "CRITIQUE : Unity sait maintenant quel os est la racine. Le soldat ne fera PLUS d'allers-retours saccadés de moitié !", "Génial !");
    }
}
