using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Une sous-catégorie pliable du menu (ex: "Infanterie") : un bouton d'en-tête et son
/// contenu associé. Le pli/dépli anime la hauteur du contenu via un LayoutElement plutôt que
/// SetActive() direct, pour permettre une transition fluide sans casser le recalcul du
/// VerticalLayoutGroup parent (voir RenfortsAccordionMenu, qui pilote le rebuild de layout).
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class AccordionCategory : MonoBehaviour
{
    [Header("Références")]
    public Button headerButton;
    public Text headerLabel;
    public RectTransform content;
    [Tooltip("Optionnel : flèche/chevron qui pivote de 180° à l'ouverture.")]
    public RectTransform expandArrow;

    [Header("Comportement")]
    public bool startExpanded = false;
    public float animationDuration = 0.18f;

    public bool IsExpanded { get; private set; }

    private LayoutElement contentLayoutElement;
    private Coroutine animCoroutine;
    private float measuredContentHeight = -1f;

    public System.Action OnToggled;

    void Awake()
    {
        contentLayoutElement = content.GetComponent<LayoutElement>();
        if (contentLayoutElement == null) contentLayoutElement = content.gameObject.AddComponent<LayoutElement>();

        if (headerButton != null) headerButton.onClick.AddListener(Toggle);

        IsExpanded = startExpanded;
        // État de départ immédiat (pas d'anim au chargement) : hauteur pilotée ou masquée.
        content.gameObject.SetActive(IsExpanded);
        contentLayoutElement.preferredHeight = IsExpanded ? -1f : 0f; // -1 = laisser le fitter mesurer
        SetArrowExpanded(IsExpanded, instant: true);
    }

    public void Toggle() => SetExpanded(!IsExpanded);

    public void SetExpanded(bool expanded)
    {
        if (expanded == IsExpanded) return;
        IsExpanded = expanded;

        if (animCoroutine != null) StopCoroutine(animCoroutine);
        animCoroutine = StartCoroutine(AnimateExpand(expanded));
        SetArrowExpanded(expanded, instant: false);
        OnToggled?.Invoke();
    }

    private IEnumerator AnimateExpand(bool expanding)
    {
        content.gameObject.SetActive(true);

        // Mesure la hauteur réelle du contenu une seule fois (mise en cache) : nécessaire pour
        // connaître la cible de l'animation, le contenu étant normalement dimensionné par son
        // propre ContentSizeFitter plutôt que par une valeur fixe.
        if (measuredContentHeight < 0f)
        {
            contentLayoutElement.preferredHeight = -1f;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            measuredContentHeight = content.rect.height;
        }

        float from = expanding ? 0f : measuredContentHeight;
        float to = expanding ? measuredContentHeight : 0f;
        float t = 0f;

        while (t < animationDuration)
        {
            t += Time.unscaledDeltaTime;
            float lerp = Mathf.Clamp01(t / animationDuration);
            contentLayoutElement.preferredHeight = Mathf.Lerp(from, to, lerp);
            RenfortsAccordionMenu.RebuildAncestorLayout(content);
            yield return null;
        }

        contentLayoutElement.preferredHeight = to;
        if (!expanding) content.gameObject.SetActive(false);
        RenfortsAccordionMenu.RebuildAncestorLayout(content);
        animCoroutine = null;
    }

    private void SetArrowExpanded(bool expanded, bool instant)
    {
        if (expandArrow == null) return;
        expandArrow.localRotation = Quaternion.Euler(0f, 0f, expanded ? 180f : 0f);
    }
}

/// <summary>
/// Orchestre le menu "QG RENFORTS" en accordéon : configure le VerticalLayoutGroup / le
/// ContentSizeFitter de la zone de contenu défilante, referme les autres catégories quand on
/// en ouvre une (optionnel), et ancre le bouton "Fermer Menu" en bas du panneau, en dehors de
/// la zone qui grandit/rétrécit avec les catégories.
/// </summary>
public class RenfortsAccordionMenu : MonoBehaviour
{
    [Header("Zone de contenu (scrollable)")]
    [Tooltip("RectTransform portant le VerticalLayoutGroup + ContentSizeFitter, parent direct des catégories.")]
    public RectTransform contentRoot;

    [Header("Catégories")]
    public List<AccordionCategory> categories = new List<AccordionCategory>();
    [Tooltip("Si vrai, ouvrir une catégorie referme automatiquement les autres.")]
    public bool onlyOneOpenAtATime = true;

    [Header("Bouton Fermer Menu (footer fixe)")]
    [Tooltip("Bouton toujours collé en bas du PANNEAU, jamais à l'intérieur du contentRoot.")]
    public RectTransform closeMenuButton;
    public float closeButtonHeight = 90f;
    public float closeButtonBottomMargin = 12f;

    void Awake()
    {
        ConfigureContentLayout();
        AnchorCloseButtonToPanelBottom();

        foreach (var category in categories)
        {
            if (category == null) continue;
            category.OnToggled += () => OnCategoryToggled(category);
        }
    }

    /// <summary>
    /// Applique/complète la configuration de VerticalLayoutGroup + ContentSizeFitter sur
    /// contentRoot pour que le menu grandisse/rétrécisse tout seul avec le nombre de
    /// catégories ouvertes — GetOrAdd plutôt qu'un simple Add, pour rester idempotent si les
    /// composants sont déjà posés à la main dans l'Inspector (leurs valeurs ne sont alors PAS
    /// écrasées, sauf les 3 réglages ci-dessous qui sont ceux qui cassent tout si mal réglés).
    /// </summary>
    private void ConfigureContentLayout()
    {
        var layoutGroup = contentRoot.GetComponent<VerticalLayoutGroup>();
        if (layoutGroup == null) layoutGroup = contentRoot.gameObject.AddComponent<VerticalLayoutGroup>();

        layoutGroup.childControlWidth = true;
        layoutGroup.childControlHeight = true;
        layoutGroup.childForceExpandWidth = true;
        layoutGroup.childForceExpandHeight = false; // chaque catégorie garde SA hauteur mesurée, pas étirée

        var fitter = contentRoot.GetComponent<ContentSizeFitter>();
        if (fitter == null) fitter = contentRoot.gameObject.AddComponent<ContentSizeFitter>();

        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize; // clé de l'ajustement dynamique
    }

    /// <summary>
    /// Détache "Fermer Menu" du flux du VerticalLayoutGroup : ancré en bas-étiré du PANNEAU
    /// parent (anchorMin/Max Y = 0, pivot Y = 0), donc sa position ne dépend JAMAIS de la
    /// hauteur cumulée des catégories au-dessus — contrairement à un bouton placé comme
    /// dernier enfant du contentRoot, qui suivrait le contenu et remonterait/descendrait à
    /// chaque pli/dépli.
    /// </summary>
    private void AnchorCloseButtonToPanelBottom()
    {
        if (closeMenuButton == null) return;

        // Le panneau ("QG RENFORTS") est le parent du bouton, PAS contentRoot — le bouton doit
        // rester un sibling de la zone scrollable, ancré au panneau qui les contient tous les deux.
        closeMenuButton.anchorMin = new Vector2(0f, 0f);
        closeMenuButton.anchorMax = new Vector2(1f, 0f);
        closeMenuButton.pivot = new Vector2(0.5f, 0f);
        closeMenuButton.anchoredPosition = new Vector2(0f, closeButtonBottomMargin);
        closeMenuButton.sizeDelta = new Vector2(0f, closeButtonHeight); // largeur pilotée par le stretch horizontal
    }

    private void OnCategoryToggled(AccordionCategory toggled)
    {
        if (!onlyOneOpenAtATime || !toggled.IsExpanded) return;

        foreach (var other in categories)
        {
            if (other != null && other != toggled && other.IsExpanded)
                other.SetExpanded(false);
        }
    }

    /// <summary>Force le recalcul du layout en remontant jusqu'au contentRoot (ou au Canvas si
    /// non trouvé) — nécessaire à chaque frame d'animation car ForceRebuildLayoutImmediate ne
    /// se propage pas tout seul aux ancêtres.</summary>
    public static void RebuildAncestorLayout(RectTransform from)
    {
        RectTransform current = from;
        while (current != null)
        {
            LayoutRebuilder.MarkLayoutForRebuild(current);
            current = current.parent as RectTransform;
        }
    }
}
