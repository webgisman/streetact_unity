using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System.Collections;

public class GameManagerUI : MonoBehaviour
{
    [Header("UI Panels")]
    public GameObject loadingPanel;
    public GameObject errorPanel;
    
    [Header("UI Text")]
    public Text errorText;
    
    private static GameManagerUI instance;
    public static GameManagerUI Instance { get { return instance; } }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        instance = this;
        
        // Cacher l'erreur au démarrage
        if (errorPanel != null) errorPanel.SetActive(false);
        if (loadingPanel != null) loadingPanel.SetActive(true);
    }

    public void ShowError(string message)
    {
        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (errorPanel != null)
        {
            errorPanel.SetActive(true);
            if (errorText != null) errorText.text = message;
        }
        else
        {
            Debug.LogError("GAME ERROR: " + message + " (No UI attached to show this)");
        }
    }

    public void OnClickLoadDefaultOfflineMap()
    {
        // 1. Cacher l'UI
        if (errorPanel != null) errorPanel.SetActive(false);
        if (loadingPanel != null) loadingPanel.SetActive(true);
        
        // 2. Relancer les processus avec le mode hors ligne activé
        StartCoroutine(LoadOfflineRoutine());
    }
    
    private IEnumerator LoadOfflineRoutine()
    {
        // On prévient le MapTileLoader et le CityGenerator d'utiliser les assets de secours
        MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
        CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
        
        if (mapLoader != null)
        {
            mapLoader.ApplyDefaultOfflineMap();
        }
        
        if (cityGen != null)
        {
            cityGen.LoadDefaultOfflineCity();
        }
        
        yield return null;
        
        if (loadingPanel != null) loadingPanel.SetActive(false);
    }
    
    public void HideLoading()
    {
        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (errorPanel != null) errorPanel.SetActive(false);
    }
}
