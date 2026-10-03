#if !UNITY_SERVER
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using NodeAction = TacticalPathManager.NodeAction;

/// <summary>
/// Joue une partie SOLO complète tout seul, avec le vrai moteur : chargement, l'armée de l'IA en
/// place, PLACEMENT AUTOMATIQUE, COMMENCER LA BATAILLE, puis à chaque tour toutes les unités du
/// joueur marchent sur l'ennemi le plus proche et FIN DE TOUR (en ACCÉLÉRÉ), jusqu'à la victoire ou
/// la défaite. Vérifie qu'une partie Solo a bien un adversaire, des combats et une fin.
///
///   Unity.exe -batchmode -projectPath "..." -executeMethod SoloSkirmishPlaytest.Run -logFile solo.log
/// Code de sortie 0 = partie terminée ; détail dans les lignes "[SoloPlaytest]" du journal.
/// </summary>
[InitializeOnLoad]
public static class SoloSkirmishPlaytest
{
    private const string ActivePrefKey = "Novgov.SoloPlaytest.Active";
    private const int MaxTurns = 25;

    static SoloSkirmishPlaytest()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void Run()
    {
        EditorPrefs.SetBool(ActivePrefKey, true);
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !EditorPrefs.GetBool(ActivePrefKey, false)) return;
        EditorPrefs.SetBool(ActivePrefKey, false);
        new GameObject("SoloSkirmishPlaytestRunner").AddComponent<Runner>();
    }

    private class Runner : MonoBehaviour
    {
        private static int Count(int team)
        {
            int n = 0;
            foreach (var u in UnitAI.AllLivingUnits) if (u != null && !u.isDead && u.teamID == team) n++;
            return n;
        }

        private static UnitAI NearestEnemy(UnitAI from)
        {
            UnitAI best = null;
            float bestD = float.MaxValue;
            foreach (var u in UnitAI.AllLivingUnits)
            {
                if (u == null || u.isDead || u.teamID == from.teamID) continue;
                float d = Vector3.Distance(u.transform.position, from.transform.position);
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3f);
            GameManagerUI.Instance.OnClickLoadDefaultOfflineMap();
            float t = 0f;
            while (!UnitSpawnerUI.IsSoloDeploymentPhase && t < 60f) { t += Time.deltaTime; yield return null; }
            Debug.Log($"[SoloPlaytest] Placement ouvert : {(UnitSpawnerUI.IsSoloDeploymentPhase ? "oui" : "NON")} — IA déjà en place : {Count(2)} unités.");

            var spawner = UnitSpawnerUI.Instance;
            typeof(UnitSpawnerUI).GetMethod("AutoDeploySoloSquad", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(spawner, null);
            yield return new WaitForSeconds(1f);
            typeof(UnitSpawnerUI).GetMethod("StartSoloBattle", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(spawner, null);
            yield return new WaitForSeconds(1f);
            Debug.Log($"[SoloPlaytest] Bataille commencée : joueur {Count(1)} unités, IA {Count(2)} unités, placement fermé : {!UnitSpawnerUI.IsSoloDeploymentPhase}.");

            var tpm = TacticalPathManager.Instance;
            int turn = 0;
            while (!TacticalPathManager.IsSoloGameOver && turn < MaxTurns)
            {
                turn++;
                foreach (var u in UnitAI.AllLivingUnits)
                {
                    if (u == null || u.isDead || !u.isPlayerControlled) continue;
                    UnitAI target = NearestEnemy(u);
                    if (target == null) continue;
                    u.ClearTacticalPath();
                    NodeAction action = u.isMortar ? NodeAction.TirMortier : NodeAction.Continuer;
                    u.AddTacticalNode(new TacticalPathManager.TacticalNode { position = target.transform.position, action = action });
                }
                tpm.LancerExecutionTour();
                Time.timeScale = 3f; // ACCÉLÉRER
                float w = 0f;
                while (tpm.phaseActuelle == TacticalPathManager.GamePhase.Execution && w < 200f) { w += Time.unscaledDeltaTime; yield return null; }
                Debug.Log($"[SoloPlaytest] Tour {turn} : joueur {Count(1)} unités, IA {Count(2)} unités.");
                yield return new WaitForSeconds(0.5f);
            }

            bool over = TacticalPathManager.IsSoloGameOver;
            string result = Count(2) == 0 && Count(1) > 0 ? "VICTOIRE" : Count(1) == 0 && Count(2) > 0 ? "DÉFAITE" : Count(1) == 0 ? "MATCH NUL" : "pas de fin";
            bool screenShown = UIScreenManager.Instance.IsVisible("GameOver");
            Debug.Log($"[SoloPlaytest] RÉSULTAT : {(over ? "partie terminée" : "PARTIE NON TERMINÉE")} après {turn} tours — {result}, écran de fin affiché : {screenShown}.");
            EditorApplication.ExitPlaymode();
            int code = over && screenShown ? 0 : 1;
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
#endif
