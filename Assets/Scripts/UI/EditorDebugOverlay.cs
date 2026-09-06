using UnityEngine;

#if UNITY_EDITOR
/// <summary>Panneau de diagnostic Éditeur (jamais compilé dans un build appareil — voir #if
/// UNITY_EDITOR ci-dessus) : affiche en permanence, dans le coin haut-gauche de CHAQUE instance
/// (Éditeur principal ou Joueur Virtuel Multiplayer Play Mode), son état réel — compte connecté,
/// Zone actuelle, ville GPS simulée — pour ne plus avoir à déduire ces informations depuis des
/// captures d'écran lors des tests multijoueur en Éditeur (2026-09-06, demande explicite : "crée un
/// menu plus explicatif").</summary>
public class EditorDebugOverlay : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("EditorDebugOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<EditorDebugOverlay>();
    }

    private GUIStyle boxStyle;
    private GUIStyle labelStyle;

    private void OnGUI()
    {
        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, richText = true };
        }

        string playerLabel = Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor ? "Éditeur principal" : "Joueur Virtuel";
        string account = Novgov.Auth.SupabaseAuthClient.CurrentSession?.user?.email ?? "non connecté";

        Novgov.Generation.ZoneManager zm = Novgov.Generation.ZoneManager.Instance;
        string zone = zm != null ? $"({zm.CurrentTileX},{zm.CurrentTileY})" : "aucune";

        var mockCity = GameManagerUI.PickEditorMockCity();

        string text =
            $"<b>[DIAGNOSTIC] {playerLabel}</b>\n" +
            $"Compte : {account}\n" +
            $"Zone actuelle : {zone}\n" +
            $"Ville simulée : {mockCity.Name}";

        Vector2 size = labelStyle.CalcSize(new GUIContent(text));
        GUI.Box(new Rect(8, 8, size.x + 24, size.y + 16), GUIContent.none, boxStyle);
        GUI.Label(new Rect(18, 16, size.x + 12, size.y), text, labelStyle);
    }
}
#endif
