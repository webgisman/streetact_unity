using UnityEngine;

/// <summary>Trace de ce que le jeu a décidé pour chaque tap/clic tactique (unité sélectionnée, menu
/// ouvert, point posé, tap refusé et pourquoi) — appelée depuis HandlePointerInput/RouteTapOnWorld
/// (Assets/Scripts/AI/Input/).
///
/// 2026-09-19 : panneau dessiné en permanence à droite de l'écran, pour pouvoir comprendre sur le
/// moment pourquoi un tap ne faisait pas ce qu'on attendait. 2026-10-03, retour joueur : « les logs
/// empêchent d'appuyer sur les boutons » — le panneau recouvrait l'interface. Il n'est plus dessiné :
/// chaque décision est écrite dans la console (Editor.log dans l'Éditeur, logcat sur Android), où
/// elle reste disponible pour diagnostiquer une partie après coup.
///
/// Déclarée hors de tout <c>#if !UNITY_SERVER</c> : ses appelants n'ont pas de garde de compilation.
/// Sans effet côté serveur headless.</summary>
public static class TapDiagnosticOverlay
{
    public static void Report(string summary)
    {
#if !UNITY_SERVER
        Debug.Log($"[Tap] {summary}");
#endif
    }
}
