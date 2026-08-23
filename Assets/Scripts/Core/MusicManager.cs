using UnityEngine;

/// <summary>
/// Musique d'ambiance unique et continue (The_Midnight_Perimeter, voir
/// Assets/Resources/Sounds/) — démarre dès le menu et ne s'arrête jamais tant que le jeu tourne,
/// seulement atténuée une fois en partie pour laisser la place aux bruitages de combat plutôt que
/// coupée puis relancée (pas de à-coup, pas de silence).
/// </summary>
public class MusicManager : MonoBehaviour
{
    public static MusicManager Instance { get; private set; }

    private const float MenuVolume = 0.8f;
    private const float GameplayVolume = 0.25f;
    private const float FadeSpeedPerSecond = 1.5f;

    private AudioSource source;
    private float targetVolume;

#if !UNITY_SERVER
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("MusicManager");
        DontDestroyOnLoad(go);
        go.AddComponent<MusicManager>();
    }
#endif

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        source = gameObject.AddComponent<AudioSource>();
        source.loop = true;
        source.playOnAwake = false;
        source.spatialBlend = 0f; // musique 2D, indépendante de la position de la caméra
        source.volume = MenuVolume;
        targetVolume = MenuVolume;

        AudioClip clip = Resources.Load<AudioClip>("Sounds/The_Midnight_Perimeter");
        if (clip == null)
        {
            Debug.LogWarning("[MusicManager] Resources/Sounds/The_Midnight_Perimeter introuvable — musique désactivée.");
            return;
        }
        source.clip = clip;
        source.Play();
    }

    private void Update()
    {
        if (source == null) return;
        source.volume = Mathf.MoveTowards(source.volume, targetVolume, FadeSpeedPerSecond * Time.deltaTime);
    }

    /// <summary>Volume plein — menus et écrans hors combat.</summary>
    public static void SetMenuVolume()
    {
        if (Instance != null) Instance.targetVolume = MenuVolume;
    }

    /// <summary>Volume réduit une fois en partie, pour ne pas couvrir les bruitages de combat.</summary>
    public static void SetGameplayVolume()
    {
        if (Instance != null) Instance.targetVolume = GameplayVolume;
    }
}
