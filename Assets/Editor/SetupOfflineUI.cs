using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class SetupOfflineUI
{
    [MenuItem("Tools/Novgov/Générer l'Interface Hors-Ligne (UI)")]
    public static void GenerateUI()
    {
        // 1. Setup EventSystem
        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        // 2. Setup Canvas
        Canvas canvas = Object.FindAnyObjectByType<Canvas>();
        GameObject canvasGo;
        if (canvas == null)
        {
            canvasGo = new GameObject("Canvas_GameManager");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.AddComponent<GraphicRaycaster>();
        }
        else
        {
            canvasGo = canvas.gameObject;
        }

        // 3. Setup GameManagerUI Component
        GameManagerUI gmUI = canvasGo.GetComponent<GameManagerUI>();
        if (gmUI == null) gmUI = canvasGo.AddComponent<GameManagerUI>();

        // 4. Create Loading Panel
        GameObject loadingPanel = CreatePanel("LoadingPanel", canvasGo.transform, new Color(0, 0, 0, 0.5f));
        Text loadingText = CreateText("LoadingText", loadingPanel.transform, "Génération de la ville en cours...\nVeuillez patienter.", 32);
        
        // 5. Create Error Panel
        GameObject errorPanel = CreatePanel("ErrorPanel", canvasGo.transform, new Color(0.1f, 0.1f, 0.1f, 0.95f));
        Text errorText = CreateText("ErrorText", errorPanel.transform, "Échec de connexion au serveur OSM.", 28);
        errorText.color = Color.red;
        errorText.rectTransform.anchoredPosition = new Vector2(0, 50);

        // 6. Create Button
        GameObject buttonGo = new GameObject("Btn_LoadOffline");
        buttonGo.transform.SetParent(errorPanel.transform, false);
        RectTransform btnRect = buttonGo.AddComponent<RectTransform>();
        btnRect.sizeDelta = new Vector2(350, 60);
        btnRect.anchoredPosition = new Vector2(0, -50);
        
        Image btnImg = buttonGo.AddComponent<Image>();
        btnImg.color = new Color(0.2f, 0.6f, 0.3f); // Green
        
        Button btn = buttonGo.AddComponent<Button>();
        
        Text btnText = CreateText("BtnText", buttonGo.transform, "Jouer avec la carte par défaut (Hors-Ligne)", 20);
        btnText.color = Color.white;

        // 7. Setup Links
        gmUI.loadingPanel = loadingPanel;
        gmUI.errorPanel = errorPanel;
        gmUI.errorText = errorText;

        // Auto-assign event
        UnityEditor.Events.UnityEventTools.AddPersistentListener(btn.onClick, gmUI.OnClickLoadDefaultOfflineMap);

        Debug.Log("L'interface utilisateur a été générée et configurée avec succès !");
    }

    private static GameObject CreatePanel(string name, Transform parent, Color color)
    {
        Transform existing = parent.Find(name);
        if (existing != null) GameObject.DestroyImmediate(existing.gameObject);

        GameObject panel = new GameObject(name);
        panel.transform.SetParent(parent, false);
        RectTransform rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;

        Image img = panel.AddComponent<Image>();
        img.color = color;
        
        return panel;
    }

    private static Text CreateText(string name, Transform parent, string content, int fontSize)
    {
        GameObject textGo = new GameObject(name);
        textGo.transform.SetParent(parent, false);
        RectTransform rect = textGo.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(600, 100);

        Text text = textGo.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        
        return text;
    }
}
