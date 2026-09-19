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

        // Ancré à DROITE (2026-09-19, retour explicite "l'affichage du diagnostic couvre les
        // unités") — la plupart des unités/de l'UI de jeu vivent plutôt à gauche/en bas, jamais
        // testé à droite jusqu'ici.
        //
        // Y ancré SOUS le radar (2026-09-19, second retour le même jour : "les éléments de
        // diagnostic sont sous le radar" — TacticalRadarUI vit AUSSI en haut-droite ET force
        // GUI.depth=-100 pour toujours dessiner PAR-DESSUS tout le reste en OnGUI, donc un simple
        // padding fixe de 8px partait de zéro connaissance de la vraie hauteur du radar (jusqu'à
        // 230px + bandeau, voir TacticalRadarUI.radarSize) et se faisait recouvrir dès que le radar
        // était affiché. BottomEdgeScreenY est le même point d'ancrage public déjà utilisé par
        // TacticalBottomBarScreen pour la même raison ("empiler les boutons sous le radar sans
        // chevauchement") — vaut 0 si le radar est masqué (vue 3D Action), donc ce panneau remonte
        // alors naturellement vers le haut de l'écran.
        Vector2 size = labelStyle.CalcSize(new GUIContent(text));
        float x = Screen.width - size.x - 32;
        float y = TacticalRadarUI.BottomEdgeScreenY + 8f;
        GUI.Box(new Rect(x, y, size.x + 24, size.y + 16), GUIContent.none, boxStyle);
        GUI.Label(new Rect(x + 10, y + 8, size.x + 12, size.y), text, labelStyle);
    }
}
#endif
