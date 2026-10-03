#if !UNITY_SERVER
using System.Collections;
using UnityEditor;
using UnityEngine;
using NodeAction = TacticalPathManager.NodeAction;

/// <summary>
/// Joue dans l'Éditeur un tour de bataille de siège avec le CODE DU SERVEUR
/// (MatchSessionManager.RunExecutionPhaseRealEngine, appelé tel quel) sur le quartier de test
/// (66648,44110) : escouades standard des deux camps (placement automatique du serveur), le camp 1
/// ABSENT (aucun ordre, comme un joueur déconnecté ou dont l'ordre a été refusé), le camp 2 marchant
/// sur lui. Le camp 1 doit riposter : jusqu'au 2026-10-03 le serveur ne laissait tirer une unité que
/// pendant son propre trajet, et un camp sans ordre se faisait abattre sans tirer un seul coup.
///
///   Unity.exe -batchmode -projectPath "..." -executeMethod SiegeDuelPlaytest.Run -logFile duel.log
/// Lignes "[DuelPlaytest]". Code de sortie 0 si le camp sans ordre a riposté.
/// Le MatchSessionManager est ajouté DÉSACTIVÉ : son Start (inscription de l'instance, revenus,
/// résolution des sièges) écrirait dans la base de production.
/// </summary>
[InitializeOnLoad]
public static class SiegeDuelPlaytest
{
    private const string ActivePrefKey = "Novgov.DuelPlaytest.Active";

    static SiegeDuelPlaytest()
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
        new GameObject("SiegeDuelPlaytestRunner").AddComponent<Runner>();
    }

    private class Runner : MonoBehaviour
    {
        private int shots1, shots2;

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3f);
            Novgov.Generation.ZoneManager.EnsureInstance().LoadZone(66648, 44110);
            var city = Object.FindFirstObjectByType<CityGenerator>();
            float wait = 0f;
            while ((city == null || !city.IsCityReady || BuildingStructure.AllBuildings.Count < 50) && wait < 120f)
            {
                wait += Time.deltaTime;
                if (city == null) city = Object.FindFirstObjectByType<CityGenerator>();
                yield return null;
            }
            yield return new WaitForSeconds(2f);

            var spawner = UnitSpawnerUI.Instance;
            spawner.ClearAllUnits();
            spawner.AutoDeployTeamFallback(1);
            spawner.AutoDeployTeamFallback(2);
            yield return new WaitForSeconds(1.5f);

            UnitAI tank1 = null, tank2 = null;
            foreach (var u in UnitAI.AllLivingUnits)
            {
                if (u == null) continue;
                u.isPlayerControlled = true; // comme le serveur : deux joueurs, pas d'IA
                u.ClearTacticalPath();
                if (u.teamID == 2) // le camp 1 est absent : aucun ordre
                    u.AddTacticalNode(new TacticalPathManager.TacticalNode { position = new Vector3(-25f, 0f, -25f), action = NodeAction.Continuer });
                if (u.name.StartsWith("Leopard2")) { if (u.teamID == 1) tank1 = u; else tank2 = u; }
            }

            var server = new GameObject("MatchSessionManager (test)").AddComponent<Novgov.Server.MatchSessionManager>();
            server.enabled = false; // pas de Start : il écrirait dans la base de production
            var run = typeof(Novgov.Server.MatchSessionManager).GetMethod("RunExecutionPhaseRealEngine",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Application.logMessageReceived += CountShots;
            server.StartCoroutine((IEnumerator)run.Invoke(server, new object[] { 1, null, null }));
            float t = 0f;
            while (t < 45f)
            {
                yield return new WaitForSeconds(2f);
                t += 2f;
                foreach (var tank in new[] { tank1, tank2 })
                {
                    if (tank == null) continue;
                    string target = tank.CurrentTarget != null ? tank.CurrentTarget.name : "aucune";
                    UnitAI enemyTank = tank == tank1 ? tank2 : tank1;
                    float d = enemyTank != null ? Vector3.Distance(tank.transform.position, enemyTank.transform.position) : -1f;
                    float aim = tank.CurrentTarget != null && tank.turretBone != null
                        ? Vector3.Angle(tank.turretBone.forward, tank.CurrentTarget.transform.position - tank.transform.position) : -1f;
                    Debug.Log($"[DuelPlaytest] t={t:F0}s {tank.name} pv={tank.health} pos={tank.transform.position:F1} cible={target} angle-tourelle={aim:F0} tourelle={(tank.turretBone != null ? tank.turretBone.name : "AUCUNE")} char-ennemi à {d:F0} m");
                }
            }
            Application.logMessageReceived -= CountShots;
            Debug.Log($"[DuelPlaytest] Tirs : équipe 1 = {shots1}, équipe 2 = {shots2}.");
            EditorApplication.ExitPlaymode();
            Debug.Log($"[DuelPlaytest] Le camp sans ordre a riposté : {(shots1 > 0 ? "OUI" : "NON")}.");
            int code = shots1 > 0 ? 0 : 1;
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }

        private void CountShots(string message, string stack, LogType type)
        {
            int at = message.IndexOf("Tire sur");
            if (at < 0) return;
            string shooter = message.Substring(0, at); // "[Leopard2_1_1] " : équipe = 2e nombre du nom
            if (shooter.Contains("_1_")) shots1++;
            else if (shooter.Contains("_2_")) shots2++;
        }
    }
}
#endif
