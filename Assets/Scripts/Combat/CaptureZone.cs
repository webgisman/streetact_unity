using UnityEngine;

/// <summary>
/// Zone à capturer et tenir pour le mode de jeu "zone_control" (voir
/// Assets/_ServerDocs/multiplayer/03-network-protocol.md). Tourne à la fois côté serveur (le
/// calcul qui fait foi, dans MatchSessionManager.RunExecutionPhase) et côté client (juste pour
/// l'affichage visuel de la zone), même principe que UnitAI/TacticalPathManager — un seul objet
/// partagé, pas de duplication de logique.
/// </summary>
public class CaptureZone : MonoBehaviour
{
    public const float CaptureRadius = 12f;
    private const float ProgressPerTick = 5f; // % gagné par tick de contrôle exclusif
    private const float DecayPerTick = 1f;    // % perdu par tick si contestée ou vide

    public float ProgressTeam1 { get; private set; }
    public float ProgressTeam2 { get; private set; }

    public static CaptureZone Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Place la zone au centre de la carte, en réutilisant la même logique anti-toit que les
    /// spawns de UnitSpawnerUI (une zone au centre d'un toit serait injouable pour l'infanterie).
    /// </summary>
    public static CaptureZone CreateAtMapCenter()
    {
        GameObject go = new GameObject("CaptureZone");
        go.transform.position = UnitSpawnerUI.FindGroundLevelNavPoint(Vector3.zero, 40f);
        return go.AddComponent<CaptureZone>();
    }

    public void ResetProgress()
    {
        ProgressTeam1 = 0f;
        ProgressTeam2 = 0f;
    }

    /// <summary>Appelé à chaque tick de capture pendant la phase d'exécution.</summary>
    public void Tick()
    {
        bool team1Present = false;
        bool team2Present = false;

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead) continue;
            if (Vector3.Distance(u.transform.position, transform.position) > CaptureRadius) continue;
            if (u.teamID == 1) team1Present = true;
            else if (u.teamID == 2) team2Present = true;
        }

        if (team1Present && team2Present)
        {
            // Zone contestée : aucune équipe n'avance.
        }
        else if (team1Present)
        {
            ProgressTeam1 = Mathf.Clamp(ProgressTeam1 + ProgressPerTick, 0f, 100f);
            ProgressTeam2 = Mathf.Clamp(ProgressTeam2 - DecayPerTick, 0f, 100f);
        }
        else if (team2Present)
        {
            ProgressTeam2 = Mathf.Clamp(ProgressTeam2 + ProgressPerTick, 0f, 100f);
            ProgressTeam1 = Mathf.Clamp(ProgressTeam1 - DecayPerTick, 0f, 100f);
        }
        else
        {
            ProgressTeam1 = Mathf.Clamp(ProgressTeam1 - DecayPerTick, 0f, 100f);
            ProgressTeam2 = Mathf.Clamp(ProgressTeam2 - DecayPerTick, 0f, 100f);
        }
    }

    /// <summary>0 = personne n'a atteint 100% ; sinon l'équipe gagnante.</summary>
    public int GetWinningTeamIfComplete()
    {
        if (ProgressTeam1 >= 100f) return 1;
        if (ProgressTeam2 >= 100f) return 2;
        return 0;
    }
}
