using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public class AnimationSetupTool : Editor
{
    [MenuItem("StreetAct/1. Réparer les Squelettes 3D (Humanoid)")]
    public static void ConfigurerSquelettes()
    {
        string[] fbxFiles = AssetDatabase.FindAssets("t:Model");
        
        int count = 0;
        foreach (string guid in fbxFiles)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.ToLower().EndsWith(".fbx"))
            {
                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer != null && importer.animationType != ModelImporterAnimationType.Generic)
                {
                    importer.animationType = ModelImporterAnimationType.Generic;
                    importer.SaveAndReimport();
                    count++;
                }
            }
        }
        
        EditorUtility.DisplayDialog("Terminé", $"{count} modèles 3D ont été convertis avec succès au format Generic !\n\nCela permet aux animations de Mixamo de fonctionner sans corrompre le mesh.", "OK");
    }

    [MenuItem("StreetAct/2. Configuration Magique des Animations")]
    public static void ConfigurerAnimations()
    {
        // 1. Trouver les clips d'animation dans les FBX
        AnimationClip idleClip = GetClipFromFBX("Assets/Rifle Idle.fbx");
        AnimationClip runClip = GetClipFromFBX("Assets/Rifle Run.fbx");

        if (idleClip == null || runClip == null)
        {
            Debug.LogError("Impossible de trouver les fichiers Rifle Idle.fbx ou Rifle Run.fbx dans le dossier Assets ! Vérifiez qu'ils sont bien à la racine du dossier Assets.");
            return;
        }

        // 2. Créer l'AnimatorController
        string controllerPath = "Assets/UnitAnimator.controller";
        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

        // 3. Ajouter le paramètre de vitesse
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        // 4. Configurer les états (Idle et Run)
        AnimatorStateMachine rootStateMachine = controller.layers[0].stateMachine;
        
        AnimatorState idleState = rootStateMachine.AddState("Idle");
        idleState.motion = idleClip;

        AnimatorState runState = rootStateMachine.AddState("Run");
        runState.motion = runClip;

        // 5. Configurer les transitions fluides entre les deux états
        
        // Transition de Idle -> Run (si vitesse > 0.1)
        AnimatorStateTransition idleToRun = idleState.AddTransition(runState);
        idleToRun.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        idleToRun.hasExitTime = false; // Ne pas attendre la fin de l'animation pour changer
        idleToRun.duration = 0.2f;     // Transition fluide de 0.2 secondes

        // Transition de Run -> Idle (si vitesse < 0.1)
        AnimatorStateTransition runToIdle = runState.AddTransition(idleState);
        runToIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        runToIdle.hasExitTime = false;
        runToIdle.duration = 0.2f;

        Debug.Log("L'Animator Controller 'UnitAnimator' a été généré avec succès !");

        // 6. Assigner automatiquement l'AnimatorController aux unités existantes dans la scène
        UnitAI[] units = Object.FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        foreach(var unit in units)
        {
            Animator anim = unit.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.runtimeAnimatorController = controller;
                Debug.Log($"AnimatorController assigné automatiquement à {unit.gameObject.name} !");
            }
        }
        
        EditorUtility.DisplayDialog("Succès !", "L'Animator Controller a été créé et les transitions ont été configurées.\n\nAssurez-vous d'avoir glissé votre modèle 3D dans vos unités (pour qu'elles aient un composant Animator).", "Super !");
    }

    private static AnimationClip GetClipFromFBX(string path)
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
        foreach (Object asset in assets)
        {
            // Trouver le clip d'animation principal dans le FBX
            if (asset is AnimationClip && !asset.name.StartsWith("__preview__"))
            {
                return asset as AnimationClip;
            }
        }
        return null;
    }
}
