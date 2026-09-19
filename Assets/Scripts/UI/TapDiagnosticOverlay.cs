using System.Collections.Generic;
using UnityEngine;

/// <summary>Panneau de diagnostic TOUJOURS VISIBLE (Éditeur ET vrai build appareil — contrairement à
/// EditorDebugOverlay, qui est réservé à l'Éditeur) montrant ce que le jeu a décidé pour le DERNIER
/// tap/clic tactique, quel qu'en soit le résultat (succès ou refus).
///
/// Créé le 2026-09-19 sur demande explicite ("quand je clique il faut toujours une information comme
/// ça je peux debugger et comprendre pourquoi ça fonctionne pas") : jusqu'ici, un tap réussi ne
/// laissait aucune trace visible de CE QUI avait été détecté (unité ? bâtiment ? sol ? rien ?), donc
/// un comportement inattendu ("je tape dans le vide, rien ne se passe" / "ça marche alors que c'est
/// une unité lourde") ne pouvait se diagnostiquer qu'en relisant le code après coup — jamais sur le
/// moment, jamais sur un vrai appareil. Voir RouteTapOnWorld/HandlePointerInput (Assets/Scripts/AI/
/// Input/) pour les points d'appel de <see cref="Report"/>.
///
/// Ancré à DROITE de l'écran (pas en haut-gauche comme EditorDebugOverlay) : retour explicite du
/// joueur, "l'affichage du diagnostic couvre les unités" — la plupart des unités/boutons vivent plutôt
/// à gauche/en bas dans ce jeu, la colonne droite reste généralement libre.
///
/// <see cref="Report"/> est déclarée EN DEHORS de tout bloc <c>#if !UNITY_SERVER</c> (même principe
/// que <c>UnitSpawnerUI.IsPointerOverOnGUI</c>/<c>IsLocalPlayerTeam</c>) : elle est appelée depuis
/// HandlePointerInput/RouteTapOnWorld SANS garde de compilation — un build où UNITY_SERVER est défini
/// ferait alors échouer TOUTE la compilation avec "TapDiagnosticOverlay n'existe pas", pas seulement
/// désactiver l'affichage. Seul le RENDU (OnGUI, sans objet à afficher côté serveur headless) reste
/// conditionnel.</summary>
public static class TapDiagnosticOverlay
{
    private const int MaxEntries = 4;
    private static readonly List<string> recentEntries = new List<string>();

    /// <summary>Enregistre le résultat d'UN tap — appelé à chaque point de décision de
    /// HandlePointerInput/RouteTapOnWorld (unité sélectionnée, menu ouvert, ordre au sol posé, tap
    /// refusé...). Le plus récent est toujours affiché en premier. No-op côté serveur headless (voir
    /// doc de la classe).</summary>
    public static void Report(string summary)
    {
#if !UNITY_SERVER
        recentEntries.Insert(0, $"[{Time.time:F1}s] {summary}");
        while (recentEntries.Count > MaxEntries) recentEntries.RemoveAt(recentEntries.Count - 1);
        EnsureBootstrapped();
#endif
    }

#if !UNITY_SERVER
    private static bool bootstrapped = false;
    private static void EnsureBootstrapped()
    {
        if (bootstrapped) return;
        bootstrapped = true;
        var go = new GameObject("TapDiagnosticOverlay");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<TapDiagnosticOverlayRenderer>();
    }

    private class TapDiagnosticOverlayRenderer : MonoBehaviour
    {
        private GUIStyle boxStyle;
        private GUIStyle labelStyle;

        private void OnGUI()
        {
            if (recentEntries.Count == 0) return;

            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = true };
            }

            string text = "<b>[TAP DIAGNOSTIC]</b>\n" + string.Join("\n\n", recentEntries);

            const float panelWidth = 260f;
            float textHeight = labelStyle.CalcHeight(new GUIContent(text), panelWidth);

            float x = Screen.width - panelWidth - 16f;
            // Ancré SOUS le radar (2026-09-19, retour joueur : "les éléments de diagnostic sont
            // sous le radar" — TacticalRadarUI vit AUSSI en haut-droite et force GUI.depth=-100,
            // donc un décalage fixe ne suffisait pas dès que le radar dépassait sa propre taille de
            // référence, voir TacticalRadarUI.BottomEdgeScreenY, le même point d'ancrage public déjà
            // utilisé par TacticalBottomBarScreen). +90 (pas +8 comme EditorDebugOverlay) : laisse
            // aussi la place au panneau EditorDebugOverlay au-dessus dans l'Éditeur (compte/Zone/
            // ville simulée, ancré juste sous le radar lui aussi) — les deux ne se recouvrent ainsi
            // jamais. Sur un vrai appareil (EditorDebugOverlay absent, #if UNITY_EDITOR), cette
            // marge ne laisse qu'un espace vide au-dessus, sans consequence.
            float y = TacticalRadarUI.BottomEdgeScreenY + 90f;
            GUI.Box(new Rect(x, y, panelWidth + 16f, textHeight + 16f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(x + 8f, y + 8f, panelWidth, textHeight), text, labelStyle);
        }
    }
#endif
}
