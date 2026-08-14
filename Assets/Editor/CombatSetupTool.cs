#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public class CombatSetupTool
{
    [MenuItem("Tools/Configurer Combat (Armes & Animations)")]
    public static void SetupCombatAnimator()
    {
        string controllerPath = "Assets/UnitAnimator.controller";
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);

        if (controller == null)
        {
            Debug.LogError("Impossible de trouver UnitAnimator.controller à la racine de Assets.");
            return;
        }

        // Ajouter le paramètre booléen IsShooting s'il n'existe pas
        bool paramExists = false;
        foreach (var param in controller.parameters)
        {
            if (param.name == "IsShooting")
            {
                paramExists = true;
                break;
            }
        }

        if (!paramExists)
        {
            controller.AddParameter("IsShooting", AnimatorControllerParameterType.Bool);
        }

        // Trouver l'animation "Firing Rifle"
        string animPath = "Assets/Firing Rifle.fbx";
        AnimationClip firingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
        
        if (firingClip == null)
        {
            // Parfois, l'AnimationClip principal a un sous-nom, on charge tout pour trouver
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(animPath);
            foreach (var asset in assets)
            {
                if (asset is AnimationClip && !asset.name.StartsWith("__preview__"))
                {
                    firingClip = asset as AnimationClip;
                    break;
                }
            }
        }

        if (firingClip == null)
        {
            Debug.LogError("Impossible de trouver l'AnimationClip dans 'Assets/Firing Rifle.fbx'. Vérifie le nom ou l'emplacement.");
            return;
        }

        // Obtenir la couche de base (Base Layer)
        AnimatorStateMachine rootStateMachine = controller.layers[0].stateMachine;

        // Chercher si l'état "Shoot" existe déjà
        AnimatorState shootState = null;
        AnimatorState idleState = null;
        AnimatorState runState = null;

        foreach (var state in rootStateMachine.states)
        {
            if (state.state.name == "Shoot") shootState = state.state;
            if (state.state.name == "Idle") idleState = state.state;
            if (state.state.name == "Run") runState = state.state; // Selon notre setup précédent
        }

        // Créer l'état "Shoot" s'il n'existe pas
        if (shootState == null)
        {
            shootState = rootStateMachine.AddState("Shoot");
            shootState.motion = firingClip;
            Debug.Log("L'état 'Shoot' a été ajouté au contrôleur d'animation.");
        }
        else
        {
            shootState.motion = firingClip; // Mettre à jour au cas où
        }

        // Créer les transitions si elles n'existent pas
        if (idleState != null)
        {
            CreateTransitionIfNotExists(idleState, shootState, "IsShooting", true);
            CreateTransitionIfNotExists(shootState, idleState, "IsShooting", false);
        }

        if (runState != null)
        {
            CreateTransitionIfNotExists(runState, shootState, "IsShooting", true);
            // La transition de Shoot vers Run n'est pas strictement nécessaire si on passe par Idle, 
            // mais on l'ajoute pour la fluidité.
            CreateTransitionIfNotExists(shootState, runState, "IsShooting", false);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("<color=green><b>[Succès]</b></color> Le contrôleur d'animation a été mis à jour avec la logique de combat !");
    }

    private static void CreateTransitionIfNotExists(AnimatorState fromState, AnimatorState toState, string paramName, bool expectedValue)
    {
        // Vérifier si la transition existe déjà
        foreach (var transition in fromState.transitions)
        {
            if (transition.destinationState == toState)
            {
                foreach (var condition in transition.conditions)
                {
                    if (condition.parameter == paramName)
                    {
                        // La transition existe déjà, on ne la recrée pas
                        return;
                    }
                }
            }
        }

        // Créer la transition
        AnimatorStateTransition newTransition = fromState.AddTransition(toState);
        newTransition.AddCondition(expectedValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, paramName);
        newTransition.duration = 0.1f; // Transition rapide
        newTransition.hasExitTime = false; // Ne pas attendre la fin de l'animation
    }
}
#endif
