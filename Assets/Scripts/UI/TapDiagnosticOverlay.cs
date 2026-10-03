using UnityEngine;

/// <summary>Trace dans la console (Editor.log, logcat) de ce que le jeu a décidé pour chaque tap
/// tactique : unité sélectionnée, menu ouvert, point posé ou refusé et pourquoi. Plus dessiné à
/// l'écran depuis le 2026-10-03 (il couvrait des boutons). Sans effet sur le serveur.</summary>
public static class TapDiagnosticOverlay
{
    public static void Report(string summary)
    {
#if !UNITY_SERVER
        Debug.Log($"[Tap] {summary}");
#endif
    }
}
