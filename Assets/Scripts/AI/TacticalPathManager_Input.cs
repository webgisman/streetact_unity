using UnityEngine;
using UnityEngine.InputSystem;

public partial class TacticalPathManager
{
    // ==========================================
    // DÉTECTION DU TAP/CLIC (sélection d'unité, ordres de mouvement, menus contextuels)
    // ==========================================

    // Tracking for Tap vs Drag
    private Vector2 pointerDownPos;
    private bool isPointerDown = false;

    // Un bouton du ContextMenu (option/ANNULER/TERMINÉ/vue 3D) qui masque le menu peut faire fuiter
    // le RELÂCHEMENT de ce même clic physique vers un raycast 3D brut, dans la MÊME frame (voir les
    // commentaires dans ShowContextMenu/BindTacticalUI) — armé à Time.frameCount par ces boutons,
    // vérifié ci-dessous pour sauter la cascade d'actions une seule frame, sans retarder le tap
    // suivant (contrairement à un cooldown en secondes, qui bloquait aussi la sélection légitime
    // d'une autre unité juste après un TERMINÉ rapide).
    private int suppressPointerInputUntilFrame = -1;

    /// <summary>Traite un tap/clic unique par frame (voir Update()) : sélection d'unité tolérante
    /// à l'écran, clic droit pour annuler, puis toute la cascade porte/fenêtre/bâtiment/sol pour
    /// l'unité actuellement sélectionnée en phase de Planification.</summary>
    private void HandlePointerInput()
    {
        // Lecture universelle du pointeur (Tactile Mobile & Souris PC)
        Vector2 pointerPosition = Vector2.zero;
        bool isPointerActive = false;
        bool wasPressed = false;
        bool wasReleased = false;

        if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
        {
            var touch = Touchscreen.current.touches[0];
            pointerPosition = touch.position.ReadValue();
            isPointerActive = true;
            wasPressed = touch.press.wasPressedThisFrame;
            wasReleased = touch.press.wasReleasedThisFrame;
        }
        else if (Mouse.current != null)
        {
            pointerPosition = Mouse.current.position.ReadValue();
            isPointerActive = true;
            wasPressed = Mouse.current.leftButton.wasPressedThisFrame;
            wasReleased = Mouse.current.leftButton.wasReleasedThisFrame;
        }
        else if (Pointer.current != null)
        {
            pointerPosition = Pointer.current.position.ReadValue();
            isPointerActive = true;
            wasPressed = Pointer.current.press.wasPressedThisFrame;
            wasReleased = Pointer.current.press.wasReleasedThisFrame;
        }

        // Clic Droit : Annulation rapide du menu ou du dernier checkpoint
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            if (AnyOrderMenuOpen)
            {
                FermerMenuContextuel(invokeCancelAction: true);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                return;
            }
            else if (uniteSelectionnee != null)
            {
                UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (unitAI != null && unitAI.tacticalPath.Count > 0)
                {
                    unitAI.RemoveLastTacticalNode();
                    DessinerTousLesChemins();
                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    Debug.Log($"[{unitAI.gameObject.name}] ↩️ Dernier checkpoint supprimé.");
                }
                return;
            }
        }

        bool isActionClick = false;

        // --- Clic Gauche ou Touch : Sélection ou Action (TAP ou CLIC DIRECT) ---
        if (isPointerActive)
        {
            if (wasPressed)
            {
                // N'armer que le suivi de l'appui ici — ne PAS déclencher isActionClick sur
                // wasPressed : le bloc wasReleased ci-dessous s'en charge déjà (tap = appui +
                // relâchement à moins de 45px). Déclencher aussi sur l'appui faisait traiter
                // CHAQUE tap physique DEUX FOIS (appui, puis relâchement) — inoffensif sur du
                // sol vide (même résultat rejoué deux fois), mais un clic sur ANNULER/TERMINÉ
                // masque le ContextMenu dès l'appui (relâchement du bouton UI Toolkit) ; le
                // second passage (déclenché ici par le relâchement du CLIC PHYSIQUE, sur le
                // même pixel) ne trouvait alors plus l'UI sous le doigt/curseur et retombait sur
                // un raycast 3D brut au même endroit — d'où le warning "chars ne peuvent pas
                // entrer" après un simple TERMINÉ/ANNULER, et l'impossibilité d'enchaîner
                // plusieurs trajets (le menu qui vient de se fermer se rouvrait aussitôt).
                isPointerDown = true;
                pointerDownPos = pointerPosition;
            }
            else if (wasReleased && isPointerDown)
            {
                isPointerDown = false;
                if (Vector2.Distance(pointerDownPos, pointerPosition) < 45f)
                {
                    isActionClick = true;
                }
            }
        }

        if (isActionClick && Time.frameCount > suppressPointerInputUntilFrame)
        {
            // Bloquer si le joueur est en train de déployer une nouvelle unité depuis le QG.
            // Vérifie AUSSI lastPlacementActionFrame (pas seulement IsPlacingUnit) : sur un tap
            // simple, UnitSpawnerUI pose l'unité PUIS repasse IsPlacingUnit à false dans son propre
            // Update() — si celui-ci s'exécute avant le nôtre dans la même frame, IsPlacingUnit
            // seul ne suffit plus à détecter que ce relâchement de clic vient d'être consommé par
            // le placement (voir le commentaire sur lastPlacementActionFrame).
            if (UnitSpawnerUI.IsPlacingUnit || Time.frameCount <= UnitSpawnerUI.lastPlacementActionFrame) return;

            // Vérifier si le clic est sur un bouton de l'interface — un seul test générique
            // (UI Toolkit picking) couvre désormais la barre du bas, les menus contextuels et
            // le dock de déploiement, plus besoin de Rect codées en dur par écran.
            if (UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverOnGUI(pointerPosition)) return;

            // 1. Détection tolérante des unités en espace écran + Raycast 3D direct
            UnitAI closestUnit = null;
            // Rayon tolérant réduit une fois qu'une unité est DÉJÀ sélectionnée en Planification :
            // au départ (rien sélectionné), 75px aide à choisir une unité facilement même en tapant
            // un peu à côté. Mais une fois qu'on donne des ordres à une unité, un tap sur le sol/un
            // bâtiment juste à côté d'un allié regroupé ne doit PAS lui voler la commande en silence
            // (voir bug précédent) — on garde donc un rayon plus strict qui ne capte plus qu'un tap
            // VRAIMENT visé sur cette autre unité, permettant toujours de changer de cible sans
            // exiger un hit pixel-parfait sur son collider. Aligné sur les 45px déjà utilisés comme
            // tolérance tactile ailleurs dans cette méthode (seuil tap-vs-drag) plutôt qu'une valeur
            // arbitraire plus stricte (30px) : les unités finissent souvent regroupées après un
            // tour (combat/déplacement), un rayon trop serré rendait certaines injoignables au doigt.
            bool isGivingOrderToSelectedUnit = phaseActuelle == GamePhase.Planification && uniteSelectionnee != null;
            float maxTouchRadiusPx = isGivingOrderToSelectedUnit ? 45f : 75f;
            float closestScreenDist = maxTouchRadiusPx;

            Ray ray = Camera.main.ScreenPointToRay(pointerPosition);
            RaycastHit hit;
            Vector3 hitPoint = Vector3.zero;
            bool hasHit = false;

            if (Physics.Raycast(ray, out hit))
            {
                hitPoint = hit.point;
                hasHit = true;

                UnitAI hitUnit = hit.collider.GetComponent<UnitAI>() ?? hit.collider.GetComponentInParent<UnitAI>();
                if (hitUnit != null && hitUnit.isPlayerControlled && !hitUnit.isDead)
                {
                    closestUnit = hitUnit;
                }
            }
            else
            {
                Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                if (groundPlane.Raycast(ray, out float enter))
                {
                    hitPoint = ray.GetPoint(enter);
                    hasHit = true;
                }
            }

            if (closestUnit == null)
            {
                for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
                {
                    UnitAI unit = UnitAI.AllLivingUnits[i];
                    if (unit != null && unit.isPlayerControlled && !unit.isDead)
                    {
                        Vector3 screenPoint = Camera.main.WorldToScreenPoint(unit.transform.position + Vector3.up * 0.5f);
                        if (screenPoint.z > 0) // Devant la caméra
                        {
                            float dist = Vector2.Distance(pointerPosition, new Vector2(screenPoint.x, screenPoint.y));
                            if (dist < closestScreenDist)
                            {
                                closestScreenDist = dist;
                                closestUnit = unit;
                            }
                        }
                    }
                }
            }

            if (closestUnit != null)
            {
                if (menuPanel != null) menuPanel.SetActive(false);
                phaseActuelle = GamePhase.Planification;
                SelectionnerUnite(closestUnit.gameObject); // referme aussi tout menu ouvert (voir SelectionnerUnite)
                return;
            }

            // Une barricade (RoadBarrier) n'a pas de UnitAI — elle ne passe jamais par
            // SelectionnerUnite/uniteSelectionnee. Indépendant de toute unité déjà sélectionnée :
            // un tap DIRECT sur son collider est un signal fort ("je vise CETTE barricade"), donc
            // prioritaire sur "donner un ordre à l'unité sélectionnée" à cet endroit précis.
            if (hasHit && phaseActuelle == GamePhase.Planification && hit.collider != null)
            {
                RoadBarrier hitBarrier = hit.collider.GetComponent<RoadBarrier>() ?? hit.collider.GetComponentInParent<RoadBarrier>();
                if (hitBarrier != null)
                {
                    if (AnyOrderMenuOpen) FermerMenuContextuel(invokeCancelAction: true);
                    selectedBarricade = hitBarrier;
                    isBarricadeSelected = true;
                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
#if !UNITY_SERVER
                    ShowBarricadeMenu();
#endif
                    return;
                }
            }

            if (hasHit && phaseActuelle == GamePhase.Planification && uniteSelectionnee != null)
            {
                UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();

                // 0. Mortier / Artillerie : tout clic est une cible de tir direct
                if (unitAI != null && unitAI.isMortar)
                {
                    positionClicTemporaire = hitPoint;
                    isBuildingSelected = false;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = true;

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
                    ShowGroundCheckpointMenu();
#endif
                    return;
                }

                // 1. Détection clic sur porte
                Novgov.Interaction.DoorInteraction clickedDoor = null;
                if (hit.collider != null)
                {
                    clickedDoor = hit.collider.GetComponent<Novgov.Interaction.DoorInteraction>()
                               ?? hit.collider.GetComponentInParent<Novgov.Interaction.DoorInteraction>();
                }

                if (clickedDoor != null)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        if (TryFallbackAuSolPourBlinde(hitPoint)) return;
                        Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas entrer dans les bâtiments !");
                        // Referme tout menu resté ouvert d'une sélection PRÉCÉDENTE valide — sinon ce
                        // tap rejeté n'affiche qu'un toast par-dessus, et TERMINÉ confirmerait encore
                        // l'ancienne cible (voir FermerMenuContextuel).
                        if (AnyOrderMenuOpen)
                            FermerMenuContextuel(invokeCancelAction: true);
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        // Ce blocage ne montrait jusqu'ici RIEN à l'écran (juste un bip + un warning
                        // Console invisible en dehors de l'Éditeur) — le joueur croyait le tap ignoré
                        // en silence. On réutilise le même toast que pour un tap hors-NavMesh pour que
                        // "ce tap ne peut pas s'appliquer à cette unité" soit toujours visible.
#if !UNITY_SERVER
                        ShowInvalidTapFeedback();
#endif
                        return;
                    }

                    selectedDoor = clickedDoor;
                    selectedWindow = null;
                    selectedBuilding = null;
                    isBuildingSelected = false;
                    isDoorSelected = true;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    clickedDoor.SetHighlight(true);

                    if (clickedDoor.building != null && clickedDoor.building.tacticalVisibility != null)
                    {
                        clickedDoor.building.tacticalVisibility.SetPlanificationPreview(true);
                    }

                    if (unitAI != null && unitAI.currentBuilding == clickedDoor.building)
                    {
                        isExitDoorAction = true;
                        positionClicTemporaire = clickedDoor.GetOutsidePosition();
                    }
                    else
                    {
                        isExitDoorAction = false;
                        positionClicTemporaire = clickedDoor.doorData.position;
                    }

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
                    ShowDoorMenu();
#endif
                    return;
                }

                // 2. Détection clic sur fenêtre
                Novgov.Interaction.WindowInteraction clickedWindow = null;
                if (hit.collider != null)
                {
                    clickedWindow = hit.collider.GetComponent<Novgov.Interaction.WindowInteraction>()
                                 ?? hit.collider.GetComponentInParent<Novgov.Interaction.WindowInteraction>();
                }

                if (clickedWindow != null)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        if (TryFallbackAuSolPourBlinde(hitPoint)) return;
                        Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas utiliser les fenêtres !");
                        if (AnyOrderMenuOpen)
                            FermerMenuContextuel(invokeCancelAction: true);
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
#if !UNITY_SERVER
                        ShowInvalidTapFeedback();
#endif
                        return;
                    }

                    selectedWindow = clickedWindow;
                    selectedDoor = null;
                    selectedBuilding = null;
                    isBuildingSelected = false;
                    isWindowSelected = true;
                    isDoorSelected = false;
                    isGroundCheckpointSelected = false;
                    positionClicTemporaire = clickedWindow.GetInteriorStancePosition();

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
                    ShowWindowMenu();
#endif
                    return;
                }

                // 3. Détection Polygone de Bâtiment 2D/3D (Intact vs Ruines)
                BuildingStructure structure = BuildingStructure.FindBuildingAt(hitPoint);
                if (structure == null && hit.collider != null)
                {
                    structure = hit.collider.GetComponentInParent<BuildingStructure>();
                }

                bool isRubblePoint = DestructibleEnvironment.IsPositionInRubble(hitPoint) ||
                                     (structure != null && structure.GetComponent<DestructibleEnvironment>() != null && structure.GetComponent<DestructibleEnvironment>().isDestroyed);

                // CAS BÂTIMENT INTACT : L'infanterie tape dans le polygone
                if (structure != null && !isRubblePoint)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        if (TryFallbackAuSolPourBlinde(hitPoint)) return;
                        // Diagnostic : imprime le bâtiment matché + ses bornes vs le point tapé, pour
                        // distinguer un vrai tap sur le bâtiment d'un polygone d'empreinte trop large
                        // (débordant sur la rue) qui bloquerait un tap pourtant fait à côté.
                        Debug.LogWarning($"[TacticalPathManager] Les chars ne peuvent pas entrer dans les bâtiments intacts ! " +
                            $"(bâtiment='{structure.gameObject.name}', tap={hitPoint:F2}, bounds2D.min={structure.bounds2D.min:F2}, bounds2D.max={structure.bounds2D.max:F2})");
                        if (AnyOrderMenuOpen)
                            FermerMenuContextuel(invokeCancelAction: true);
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
#if !UNITY_SERVER
                        ShowInvalidTapFeedback();
#endif
                        return;
                    }

                    selectedBuilding = structure;
                    selectedDoor = null;
                    selectedWindow = null;
                    isBuildingSelected = true;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    positionClicTemporaire = hitPoint;

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
                    ShowBuildingMenu();
#endif
                    return;
                }

                // CAS SOL NORMAL / RUINES / TOIT EXISTANT
                bool isUnitPlanningInside = (unitAI != null && (unitAI.currentBuilding != null || (unitAI.tacticalPath.Count > 0 && unitAI.tacticalPath[unitAI.tacticalPath.Count - 1].action == NodeAction.EntrerBatiment)));
                bool isSelectedOnRoof = (unitAI != null && (unitAI.isRooftopSniper || unitAI.transform.position.y > 2.2f));

                UnityEngine.AI.NavMeshHit navHit;
                bool hasNavMesh = UnityEngine.AI.NavMesh.SamplePosition(hitPoint, out navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas);

                if (hasNavMesh || isUnitPlanningInside || isRubblePoint || isSelectedOnRoof)
                {
                    if (isRubblePoint || isUnitPlanningInside) positionClicTemporaire = new Vector3(hitPoint.x, 0.05f, hitPoint.z);
                    else if (isSelectedOnRoof) positionClicTemporaire = hitPoint;
                    else positionClicTemporaire = navHit.position;

                    isBuildingSelected = false;
                    selectedBuilding = null;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = true;

                    isNearBuildingWall = false;
                    if (!isSelectedOnRoof && !isUnitPlanningInside)
                    {
                        Collider[] nearby = Physics.OverlapSphere(positionClicTemporaire, 2.2f);
                        foreach (var c in nearby)
                        {
                            if (c.GetComponentInParent<BuildingStructure>() != null || c.gameObject.name.Contains("Building") || c.gameObject.name.Contains("Mur") || c.gameObject.name.Contains("Wall") || c.gameObject.name.Contains("Polygone"))
                            {
                                isNearBuildingWall = true;
                                break;
                            }
                        }
                    }

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
#if !UNITY_SERVER
                    ShowGroundCheckpointMenu();
#endif
                }
                else
                {
                    // Endroit non atteignable pour cette unité (hors NavMesh, pas de ruine/toit/
                    // intérieur applicable) : on le signale au joueur au lieu d'ignorer le tap en
                    // silence — il choisit alors un autre endroit, l'unité reste sélectionnée.
                    //
                    // IMPORTANT : si un menu d'une sélection PRÉCÉDENTE (valide) était encore ouvert,
                    // il faut le refermer ici comme le ferait ANNULER — sinon ce tap invalide ne fait
                    // qu'afficher un toast par-dessus un menu resté intact, dont TERMINÉ reste
                    // cliquable et confirme alors l'ANCIENNE position (pas celle qui vient d'être
                    // tapée et rejetée). Vu du joueur : "impossible d'aller ici" s'affiche, mais
                    // TERMINÉ trace quand même un trajet — vers l'endroit précédent, pas celui-ci.
                    if (AnyOrderMenuOpen)
                    {
                        FermerMenuContextuel(invokeCancelAction: true);
                    }

                    AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                    if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
#if !UNITY_SERVER
                    ShowInvalidTapFeedback();
#endif
                }
            }
        }
    }
}
