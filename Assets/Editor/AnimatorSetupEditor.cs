using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

public class AnimatorSetupEditor
{
    [InitializeOnLoadMethod]
    [MenuItem("Tools/Configurer Animator Unités (Hit, Death et Escalade)")]
    public static void SetupAnimator()
    {
        string controllerPath = "Assets/UnitAnimator.controller";
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);

        if (controller == null)
        {
            Debug.LogError($"[AnimatorSetup] Impossible de trouver l'Animator à {controllerPath}");
            return;
        }

        // 1. Ajouter les paramètres Hit, Die et IsClimbing
        AddParameter(controller, "Hit", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "Die", AnimatorControllerParameterType.Trigger);
        AddParameter(controller, "IsClimbing", AnimatorControllerParameterType.Bool);

        AnimatorStateMachine rootStateMachine = controller.layers[0].stateMachine;

        // 2. Charger les animations FBX
        AnimationClip hitClip = LoadClip("Assets/Hit Reaction.fbx");
        AnimationClip dieClip = LoadClip("Assets/Death From The Front.fbx");
        AnimationClip climbClip = LoadClip("Assets/Freehang Climb.fbx");

        if (hitClip == null || dieClip == null)
        {
            Debug.LogError("[AnimatorSetup] Impossible de trouver les animations 'Hit Reaction.fbx' ou 'Death From The Front.fbx'.");
            return;
        }

        // 3. Ajouter les états
        AnimatorState hitState = GetOrAddState(rootStateMachine, "Hit Reaction", hitClip);
        AnimatorState dieState = GetOrAddState(rootStateMachine, "Death From The Front", dieClip);
        AnimatorState climbState = null;
        if (climbClip != null)
        {
            climbState = GetOrAddState(rootStateMachine, "Climbing", climbClip);
        }

        // Trouver les états Idle et Run
        AnimatorState idleState = null;
        AnimatorState runState = null;
        foreach (var childState in rootStateMachine.states)
        {
            string sName = childState.state.name.ToLower();
            if (sName.Contains("idle")) idleState = childState.state;
            if (sName.Contains("run")) runState = childState.state;
        }

        // 4. Ajouter les transitions
        
        // Any State -> Hit Reaction
        AddAnyStateTransition(rootStateMachine, hitState, "Hit");

        // Hit Reaction -> Idle
        if (idleState != null)
        {
            AddTransition(hitState, idleState, true);
        }

        // Any State -> Death From The Front
        AddAnyStateTransition(rootStateMachine, dieState, "Die");

        // Transitions d'escalade
        if (climbState != null && idleState != null)
        {
            // Idle -> Climbing (IsClimbing == true)
            AddBoolTransition(idleState, climbState, "IsClimbing", true);
            if (runState != null)
            {
                AddBoolTransition(runState, climbState, "IsClimbing", true);
            }

            // Climbing -> Idle (IsClimbing == false)
            AddBoolTransition(climbState, idleState, "IsClimbing", false);
        }

        // Sauvegarder les modifications de l'asset
        AssetDatabase.SaveAssets();
        Debug.Log("<color=green><b>[AnimatorSetup] Configuration de l'Animator réussie ! Hit, Death et Escalade sont prêts.</b></color>");
    }

    private static void AddBoolTransition(AnimatorState sourceState, AnimatorState targetState, string boolParam, bool conditionValue)
    {
        foreach (var transition in sourceState.transitions)
        {
            if (transition.destinationState == targetState) return; // Existe déjà
        }

        var newTransition = sourceState.AddTransition(targetState);
        newTransition.hasExitTime = false;
        newTransition.hasFixedDuration = true;
        newTransition.duration = 0.2f;
        newTransition.AddCondition(conditionValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, boolParam);
    }

    private static void AddParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
    {
        foreach (var param in controller.parameters)
        {
            if (param.name == name) return; // Existe déjà
        }
        controller.AddParameter(name, type);
    }

    private static AnimationClip LoadClip(string fbxPath)
    {
        Object[] objects = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        foreach (var obj in objects)
        {
            if (obj is AnimationClip clip && !clip.name.StartsWith("__preview__") && clip.name.Contains("mixamo.com"))
            {
                return clip;
            }
            else if (obj is AnimationClip clip2 && !clip2.name.StartsWith("__preview__"))
            {
                return clip2; // Fallback générique si Mixamo n'est pas dans le nom
            }
        }
        return null;
    }

    private static AnimatorState GetOrAddState(AnimatorStateMachine sm, string stateName, AnimationClip clip)
    {
        foreach (var childState in sm.states)
        {
            if (childState.state.name == stateName)
            {
                childState.state.motion = clip;
                return childState.state;
            }
        }
        AnimatorState newState = sm.AddState(stateName);
        newState.motion = clip;
        return newState;
    }

    private static void AddAnyStateTransition(AnimatorStateMachine sm, AnimatorState targetState, string triggerName)
    {
        foreach (var transition in sm.anyStateTransitions)
        {
            if (transition.destinationState == targetState) return; // Existe déjà
        }

        var newTransition = sm.AddAnyStateTransition(targetState);
        newTransition.AddCondition(AnimatorConditionMode.If, 0, triggerName);
        newTransition.hasExitTime = false;
        newTransition.duration = 0.1f;
    }

    private static void AddTransition(AnimatorState sourceState, AnimatorState targetState, bool hasExitTime)
    {
        foreach (var transition in sourceState.transitions)
        {
            if (transition.destinationState == targetState) return; // Existe déjà
        }

        var newTransition = sourceState.AddTransition(targetState);
        newTransition.hasExitTime = hasExitTime;
        newTransition.hasFixedDuration = true;
        newTransition.duration = 0.25f;
    }
}
