using UnityEngine;
#if !UNITY_SERVER
using UnityEngine.UIElements;
#endif

public partial class TacticalPathManager
{
    // ==========================================
    // MENU CONTEXTUEL (ouverture/fermeture, options porte/fenêtre/bâtiment/sol, confirmation)
    // ==========================================

    /// <summary>Referme le ContextMenu et réinitialise toute sélection tactique en attente
    /// (bâtiment/porte/fenêtre/checkpoint). Factorisé pour ne plus jamais oublier une des étapes :
    /// un seul de ces 4 flags ou l'un des deux Action laissé "vivant" par erreur suffit à ce qu'un
    /// menu supposé fermé reste confirmable en arrière-plan (voir historique des bugs "trajet
    /// confirmé après ANNULER/tap invalide"). N'invoque currentMenuConfirmAction nulle part ici —
    /// à l'appelant de le faire AVANT s'il s'agit d'un TERMINÉ (voir confirm-button).</summary>
    private void FermerMenuContextuel(bool invokeCancelAction)
    {
        isDoorSelected = false;
        isWindowSelected = false;
        isBuildingSelected = false;
        isGroundCheckpointSelected = false;
        isBarricadeSelected = false;
        isBarricadeDeployMenuOpen = false;
        selectedBuilding = null;
        selectedBarricade = null;
        // Sans ce reset, positionClicTemporaire (partagé, pas par-unité) garde la dernière position
        // tapée pour l'unité PRÉCÉDENTE : sélectionner une unité neuve la faisait alors prévisualiser
        // (voir DessinerTousLesChemins) un trajet fantôme vers ce point resté en mémoire, jamais
        // demandé pour cette unité-ci.
        positionClicTemporaire = Vector3.positiveInfinity;
#if !UNITY_SERVER
        if (invokeCancelAction) currentMenuCancelAction?.Invoke();
        currentMenuCancelAction = null;
        currentMenuConfirmAction = null;
        if (UIScreenManager.Instance != null) UIScreenManager.Instance.SetVisible("ContextMenu", false);
#endif
    }

    /// <summary>Pour un blindé qui vient d'être bloqué (bâtiment/porte/fenêtre), tente un repli sur
    /// le sol RÉEL à ces mêmes coordonnées X/Z avant de rejeter le tap. En ville dense, la caméra
    /// tactique (vue oblique) fait souvent "raser" la façade d'un bâtiment PROCHE alors que le
    /// joueur visait la rue juste devant/à côté — le raycast touche alors le mur (parfois assez
    /// haut, voir le diagnostic loggé plus haut) au lieu du sol derrière. Sans ce repli, un blindé ne
    /// pouvait plus du tout être commandé près de la moindre façade, même en visant la rue. Retourne
    /// true si un point de sol marchable existe à proximité et que le menu d'ordre a été ouvert à sa
    /// place (le tap est alors traité comme résolu, l'appelant doit `return` immédiatement).</summary>
    private bool TryFallbackAuSolPourBlinde(Vector3 tapPoint)
    {
        Vector3 flatPoint = new Vector3(tapPoint.x, 0f, tapPoint.z);
        if (!UnityEngine.AI.NavMesh.SamplePosition(flatPoint, out UnityEngine.AI.NavMeshHit navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas))
            return false;

        positionClicTemporaire = navHit.position;
        isBuildingSelected = false;
        selectedBuilding = null;
        isDoorSelected = false;
        isWindowSelected = false;
        isGroundCheckpointSelected = true;

        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
        ShowGroundCheckpointMenu();
#endif
        return true;
    }

    public void ConfirmerAction(int actionIndex)
    {
        // Son de clic UI et Vibration
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        if (uniteSelectionnee != null) PlayUnitVoiceLine(uniteSelectionnee.GetComponent<UnitAI>(), isSelection: false);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif

        TacticalNode nouveauNoeud = new TacticalNode
        {
            position = positionClicTemporaire,
            action = (NodeAction)actionIndex
        };

        if (uniteSelectionnee != null)
        {
            UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
            if (unitAI != null)
            {
                // Si l'action est d'ENTRER dans le bâtiment, ouvrir immédiatement le toit en mode Planification !
                if ((NodeAction)actionIndex == NodeAction.EntrerBatiment && selectedDoor != null && selectedDoor.building != null)
                {
                    if (selectedDoor.building.tacticalVisibility != null)
                    {
                        selectedDoor.building.tacticalVisibility.SetPlanificationPreview(true);
                    }
                    unitAI.currentBuilding = selectedDoor.building;
                    Debug.Log($"<color=green>[TacticalPathManager] 🚪 Entrée confirmée : Toit de {selectedDoor.building.gameObject.name} ouvert en direct pour continuer le tracé intérieur !</color>");
                }
                else if ((NodeAction)actionIndex == NodeAction.SortirBatiment && selectedDoor != null && selectedDoor.building != null)
                {
                    if (selectedDoor.building.tacticalVisibility != null && !selectedDoor.building.IsAnyUnitInside())
                    {
                        selectedDoor.building.tacticalVisibility.SetPlanificationPreview(false);
                    }
                    unitAI.currentBuilding = null;
                }

                unitAI.AddTacticalNode(nouveauNoeud);

                // --- APPARITION DU MARQUEUR HOLOGRAPHIQUE ---
                GameObject marker = new GameObject("WaypointMarker");
                marker.transform.position = positionClicTemporaire;
                WaypointMarker wm = marker.AddComponent<WaypointMarker>();
                if ((NodeAction)actionIndex == NodeAction.TirMortier)
                {
                    wm.isArtilleryTarget = true;
                }
            }
        }

        isDoorSelected = false;
        isWindowSelected = false;
        isBuildingSelected = false;
        selectedBuilding = null;
        isGroundCheckpointSelected = false;

        if (menuPanel != null) menuPanel.SetActive(false);
    }

    public void ConfirmerBuildingAction(int choice)
    {
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif
        if (selectedBuilding == null || uniteSelectionnee == null) return;
        UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
        if (unitAI == null) return;
        PlayUnitVoiceLine(unitAI, isSelection: false);

        if (choice == 1) // Infiltration / Intérieur
        {
            if (selectedBuilding.tacticalVisibility != null)
            {
                selectedBuilding.tacticalVisibility.SetPlanificationPreview(true);
            }

            // currentBuilding ne doit être posé QUE s'il existe réellement une porte pour y entrer
            // (GetClosestDoor renvoie null pour un bâtiment sans donnée de porte générée, cas réel
            // et atteignable) — sinon l'unité se retrouve marquée "à l'intérieur" sans jamais avoir
            // de nœud EntrerBatiment ni bougé, ce qui fausse tous ses clics suivants et contourne
            // l'enregistrement d'occupation du bâtiment (RegisterUnitInside).
            var door = selectedBuilding.GetClosestDoor(unitAI.transform.position);
            if (door != null)
            {
                if (unitAI.currentBuilding != selectedBuilding)
                {
                    unitAI.AddTacticalNode(new TacticalNode { position = door.position, action = NodeAction.EntrerBatiment });
                }
                unitAI.currentBuilding = selectedBuilding;

                Vector3 insidePos = new Vector3(positionClicTemporaire.x, 0.05f, positionClicTemporaire.z);
                unitAI.AddTacticalNode(new TacticalNode { position = insidePos, action = NodeAction.Continuer });

                GameObject marker = new GameObject("WaypointMarker");
                marker.transform.position = insidePos;
                marker.AddComponent<WaypointMarker>();
            }
            else
            {
                Debug.LogWarning($"[TacticalPathManager] {selectedBuilding.name} n'a aucune porte détectée — infiltration impossible pour cette unité.");
            }
        }
        else if (choice == 2) // Escalade / Toit
        {
            float roofHeight = (selectedBuilding.height > 0) ? selectedBuilding.height : 6.0f;
            Vector3 roofPos = new Vector3(positionClicTemporaire.x, roofHeight, positionClicTemporaire.z);
            unitAI.AddTacticalNode(new TacticalNode { position = roofPos, action = NodeAction.Escalade });

            GameObject marker = new GameObject("WaypointMarker");
            marker.transform.position = roofPos;
            marker.AddComponent<WaypointMarker>();
        }
        else if (choice == 3) // Porte la plus proche
        {
            var door = selectedBuilding.GetClosestDoor(unitAI.transform.position);
            Vector3 doorPos = (door != null) ? door.position : positionClicTemporaire;
            unitAI.AddTacticalNode(new TacticalNode { position = doorPos, action = NodeAction.Continuer });

            GameObject marker = new GameObject("WaypointMarker");
            marker.transform.position = doorPos;
            marker.AddComponent<WaypointMarker>();
        }

        isBuildingSelected = false;
        selectedBuilding = null;
        isDoorSelected = false;
        isWindowSelected = false;
        isGroundCheckpointSelected = false;

        if (menuPanel != null) menuPanel.SetActive(false);
        DessinerTousLesChemins();
    }

    // --- ANIMATION UI (Bounce) --- (legacy, menu GameObject pré-UI Toolkit ; conservé tel quel)
    private System.Collections.IEnumerator AnimateMenuBounce()
    {
        menuPanel.SetActive(true);
        Vector3 finalScale = Vector3.one;
        menuPanel.transform.localScale = Vector3.zero;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime * 4f; // Vitesse de l'animation
            // Formule mathématique d'un "Spring/Bounce" d'amortissement
            float scale = 1f - Mathf.Exp(-t * 8f) * Mathf.Cos(t * 15f);
            menuPanel.transform.localScale = finalScale * scale;
            yield return null;
        }
        menuPanel.transform.localScale = finalScale;
    }

#if !UNITY_SERVER
    /// <summary>Affiche le menu d'ordre tactique pour l'endroit tapé : chaque bouton ne fait que
    /// "proposer" son action (surlignée, voir .context-button-selected) — rien n'est appliqué à la
    /// trajectoire de l'unité tant que le joueur ne valide pas avec TERMINÉ (confirm-button, câblé
    /// une seule fois dans BindTacticalUI). ANNULER referme sans rien changer.</summary>
    private void ShowContextMenu(string title, System.Action onCancel, params (string text, Color color, System.Action onClick)[] buttons)
    {
        if (!tacticalUiBound) return;
        menuTitleEl.text = title;
        buttonContainerEl.Clear();
        foreach (var (text, color, onClick) in buttons)
        {
            Button btn = null;
            btn = new Button(() =>
            {
                // Arme le garde-fou anti-fuite de UnitSpawnerUI.IsPointerOverOnGUI (cooldown 0.3s) :
                // sans lui, le RELÂCHEMENT du même clic physique (même pixel) peut retomber sur un
                // raycast 3D brut si ce bouton modifie l'affichage du ContextMenu entre-temps —
                // c'est la cause racine de tous les bugs "TERMINÉ/ANNULER produit un comportement
                // random sur la carte" observés jusqu'ici (voir cancel/confirm dans BindTacticalUI,
                // seuls endroits qui masquent réellement le ContextMenu). Un cooldown en secondes
                // (essayé avant) bloquait aussi la sélection légitime d'une autre unité juste après —
                // une seule frame suffit à couper la fuite du MÊME clic physique.
                suppressPointerInputUntilFrame = Time.frameCount;
                foreach (var sibling in buttonContainerEl.Children())
                    sibling.RemoveFromClassList("context-button-selected");
                btn.AddToClassList("context-button-selected");
                currentMenuConfirmAction = onClick;
                confirmBtnEl.SetEnabled(true);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            }) { text = text };
            btn.AddToClassList("context-button");
            btn.style.borderLeftColor = new StyleColor(color);
            buttonContainerEl.Add(btn);
        }
        currentMenuCancelAction = onCancel;
        currentMenuConfirmAction = null;
        confirmBtnEl.SetEnabled(false);
        UIScreenManager.Instance.SetVisible("ContextMenu", true);
    }

    /// <summary>Toast temporaire (voir invalid-tap-toast dans TacticalBottomBarScreen.uxml) pour
    /// signaler un tap sur un endroit non atteignable par l'unité sélectionnée — au lieu d'ignorer
    /// le tap en silence comme avant. L'unité reste sélectionnée, le joueur retape ailleurs.</summary>
    private void ShowInvalidTapFeedback()
    {
        if (!tacticalUiBound) return;
        invalidTapToastEl.style.display = DisplayStyle.Flex;
        invalidTapToastTimer = 1.8f;
    }

    private void ShowBuildingMenu()
    {
        // Les libellés de ce menu (construits dynamiquement, pas via UXML) ne portent plus
        // d'emoji : Theme.tss assigne désormais une police custom (Oswald) à tous les Label/Button
        // UI Toolkit, et cette police n'a aucun glyphe emoji de repli — un emoji laissé ici
        // s'afficherait comme un carré vide ("tofu") sur la plupart des appareils Android.
        ShowContextMenu($"BÂTIMENT : {selectedBuilding.gameObject.name}",
            () => { isBuildingSelected = false; selectedBuilding = null; },
            ("1. INFILTRATION / INTÉRIEUR (RDC)", NovgovTheme.Success, () => ConfirmerBuildingAction(1)),
            ("2. MONTER SUR LE TOIT (Sniper / Guet)", NovgovTheme.Info, () => ConfirmerBuildingAction(2)),
            ("3. PORTE LA PLUS PROCHE", NovgovTheme.Accent, () => ConfirmerBuildingAction(3))
        );
    }

    private void ShowDoorMenu()
    {
        if (isExitDoorAction)
        {
            ShowContextMenu($"PORTE : {selectedDoor.building.gameObject.name}",
                () => { isDoorSelected = false; },
                ("1. SORTIR DANS LA RUE", NovgovTheme.Success, () => ConfirmerAction((int)NodeAction.SortirBatiment)),
                ("2. GUETTER PAR LA PORTE", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.GuetterPorte)),
                ("3. CHECKPOINT SIMPLE", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Continuer))
            );
        }
        else
        {
            ShowContextMenu($"ENTRÉE : {selectedDoor.building.gameObject.name}",
                () => { isDoorSelected = false; },
                ("ENTRER DANS LE BÂTIMENT", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.EntrerBatiment))
            );
        }
    }

    private void ShowWindowMenu()
    {
        ShowContextMenu($"FENÊTRE : {selectedWindow.building.gameObject.name}",
            () => { isWindowSelected = false; },
            ("1. GUETTER (Couvert -75%)", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.GarnisonFenetre)),
            ("2. CHECKPOINT SIMPLE", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Continuer))
        );
    }

    /// <summary>Reconstruit le menu du checkpoint au sol — mêmes règles que l'ancien OnGUI (mortier
    /// / blindé / 4 variantes d'infanterie selon toit-cible, unité déjà sur un toit, unité à
    /// l'intérieur). Appelé une fois au moment du tap (voir HandlePointerInput()), pas chaque frame.</summary>
    private void ShowGroundCheckpointMenu()
    {
        UnitAI selectedUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
        bool isMortarUnit = (selectedUnitAI != null && selectedUnitAI.isMortar);

        System.Action onCancel = () => { isGroundCheckpointSelected = false; };

        if (isMortarUnit)
        {
            ShowContextMenu("ARTILLERIE : ORDRE DE TIR", onCancel,
                ("1. TIR DE MORTIER (Zone AoE)", NovgovTheme.Danger, () => ConfirmerAction((int)NodeAction.TirMortier)),
                ("2. SE DÉPLACER (Position)", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (selectedUnitAI != null && selectedUnitAI.isTank)
        {
            ShowContextMenu("BLINDÉ : ORDRE DE MANOEUVRE", onCancel,
                ("1. AVANCER (Déplacement)", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. GUETTER (Surveillance)", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        // Menu Fantassin
        UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
        bool isTargetOnRoof = positionClicTemporaire.y > 1.8f;

        bool unitIsAlreadyOnRoof = (uAI != null && (uAI.isRooftopSniper || uAI.transform.position.y > 2.0f));
        if (uAI != null && uAI.tacticalPath.Count > 0)
        {
            var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
            if (lastN.action == NodeAction.Escalade || lastN.position.y > 2.0f) unitIsAlreadyOnRoof = true;
        }

        bool unitIsInsideBuilding = (uAI != null && uAI.currentBuilding != null && !uAI.isRooftopSniper);
        if (uAI != null && uAI.tacticalPath.Count > 0)
        {
            var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
            if (lastN.action == NodeAction.EntrerBatiment) unitIsInsideBuilding = true;
            else if (lastN.action == NodeAction.SortirBatiment) unitIsInsideBuilding = false;
        }

        if (isTargetOnRoof)
        {
            if (unitIsAlreadyOnRoof)
            {
                ShowContextMenu("INFANTERIE : DÉPLACEMENT TOIT", onCancel,
                    ("1. CONTINUER SUR LE TOIT", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                    ("2. GUETTER SUR LE TOIT", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                    ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
                );
            }
            else
            {
                ShowContextMenu("INFANTERIE : ESCALADE DE FAÇADE", onCancel,
                    ("1. ESCALADER SUR LE TOIT", NovgovTheme.Success, () => ConfirmerAction((int)NodeAction.Escalade)),
                    ("2. GUETTER (+50% Défense)", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                    ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
                );
            }
            return;
        }

        if (unitIsAlreadyOnRoof)
        {
            ShowContextMenu("INFANTERIE : DESCENTE VERS RUE", onCancel,
                ("1. DESCENDRE DU TOIT", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Escalade)),
                ("2. GUETTER (+50% Défense)", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (unitIsInsideBuilding)
        {
            ShowContextMenu("INFANTERIE : DÉPLACEMENT INTÉRIEUR", onCancel,
                ("1. SE DÉPLACER À L'INTÉRIEUR", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. GUETTER INTÉRIEUR", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (isNearBuildingWall)
        {
            ShowContextMenu("INFANTERIE : ORDRE TACTIQUE", onCancel,
                ("1. CONTINUER (Mouvement)", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. GUETTER (+50% Défense)", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s)),
                ("4. SE CACHER (Contre mur)", NovgovTheme.Success, () => ConfirmerAction((int)NodeAction.SeCacher))
            );
        }
        else
        {
            ShowContextMenu("INFANTERIE : ORDRE TACTIQUE", onCancel,
                ("1. CONTINUER (Mouvement)", NovgovTheme.Neutral, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. GUETTER (+50% Défense)", NovgovTheme.Info, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ATTENDRE 30 SECONDES", NovgovTheme.Accent, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
        }
    }

    /// <summary>Une barricade est un obstacle statique (RoadBarrier), pas une UnitAI — elle n'a donc
    /// jamais rien à faire dans le menu d'ordre tactique (avancer/guetter n'ont aucun sens pour
    /// elle). Menu dédié minimal : juste la retirer si le joueur change d'avis sur son placement.</summary>
    private void ShowBarricadeMenu()
    {
        ShowContextMenu($"BARRICADE ({selectedBarricade.health:F0}/{selectedBarricade.maxHealth:F0} PV)",
            () => { isBarricadeSelected = false; selectedBarricade = null; },
            ("1. RETIRER LA BARRICADE", NovgovTheme.Danger, ConfirmerRetirerBarricade)
        );
    }

    private void ConfirmerRetirerBarricade()
    {
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        if (selectedBarricade != null) selectedBarricade.RemoveByPlayer();
        isBarricadeSelected = false;
        selectedBarricade = null;
    }

    /// <summary>Confirmation d'une EXTENSION de barricades PAS ENCORE posées (voir UnitSpawnerUI.
    /// HandlePlacementPreview) : la toute première barricade d'une session de placement se pose
    /// directement, sans passer par ici, exactement comme n'importe quelle autre unité — ce menu
    /// n'apparaît qu'à partir du DEUXIÈME tap, qui prolonge la ligne depuis la dernière barricade
    /// posée jusqu'au nouveau point, avec un nombre de barricades intermédiaires déjà calculé
    /// (trajet faisable + stock). Même mécanique DÉPLOYER/ANNULER qu'un ordre tactique classique
    /// (option surlignée puis TERMINÉ), pour rester cohérent avec le reste du jeu. ANNULER
    /// n'abandonne QUE cette extension (CancelPendingBarricadeExtension) — l'ancre existante et le
    /// mode placement restent actifs pour que le joueur retape un autre point.</summary>
    public void ShowBarricadeExtensionMenu(int count)
    {
        isBarricadeDeployMenuOpen = true;
        // L'option est nommée comme un CHOIX à surligner ("1. ..."), pas comme le bouton d'action
        // lui-même — sinon on croit que la cliquer suffit, alors qu'il faut ENSUITE cliquer
        // TERMINÉ (footer de ShowContextMenu) pour que ça s'applique, exactement comme pour
        // n'importe quel autre ordre tactique (ex: "1. CONTINUER" puis TERMINÉ).
        ShowContextMenu($"EXTENSION : {count} BARRICADE(S) SUPPLÉMENTAIRE(S)",
            () => { if (UnitSpawnerUI.Instance != null) UnitSpawnerUI.Instance.CancelPendingBarricadeExtension(); },
            ("1. DÉPLOYER CETTE EXTENSION", NovgovTheme.Danger, () => { if (UnitSpawnerUI.Instance != null) UnitSpawnerUI.Instance.ConfirmPendingBarricadeExtension(); })
        );
    }

    /// <summary>Referme le menu DÉPLOYER/ANNULER ci-dessus si le joueur quitte le mode placement
    /// par un autre chemin (clic droit, Échap, bouton DÉPLOIEMENT) plutôt que par ANNULER dans ce
    /// menu — sans ça, la fenêtre resterait affichée, orpheline, une fois le tracé déjà abandonné
    /// côté UnitSpawnerUI.</summary>
    public void CloseBarricadeDeployMenuIfOpen()
    {
        if (isBarricadeDeployMenuOpen) FermerMenuContextuel(invokeCancelAction: false);
    }
#endif
}
