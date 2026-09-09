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

    /// <summary>Le collider touché fait-il partie de la COQUE du bâtiment (mur, toit, sol 2D,
    /// contour), par opposition au mobilier urbain que StreetPropsGenerator crée comme ENFANT du
    /// bâtiment mais pose hors de son empreinte (lampadaires, arbres, bancs) ?
    ///
    /// C'est ce qui permet de distinguer « le joueur montre ce bâtiment » de « le joueur montre la
    /// rue et le rayon a effleuré un décor ». Un simple GetComponentInParent&lt;BuildingStructure&gt;()
    /// ne les distingue pas : les deux remontent au même bâtiment.
    ///
    /// La remontée s'arrête AVANT le transform du bâtiment lui-même, sinon un lampadaire finirait
    /// par y aboutir et serait pris pour une façade. Un collider porté directement par le bâtiment
    /// compte, lui, comme sa coque.</summary>
    private static bool IsBuildingShellCollider(Collider col, BuildingStructure owner)
    {
        if (col == null || owner == null) return false;

        for (Transform t = col.transform; t != null && t != owner.transform; t = t.parent)
        {
            // Noms créés par CityGenerator pour la coque (voir CreateBuildingMeshes).
            if (t.name == "Walls" || t.name == "Roof" || t.name == "Footprint_2D" || t.name == "Outline_2D") return true;
        }

        return col.transform == owner.transform;
    }

    /// <summary>Ramène un point de tap sur la SURFACE DE TOIT réellement rendue à ces coordonnées.
    ///
    /// Indispensable pour une unité perchée (correctif 2026-09-04) : en vue de commandement (bridée
    /// à 75° d'inclinaison), un tap dans la bande de ~1,6 m le long de l'arête PROCHE de l'empreinte
    /// manque le collider de toit et frappe le quad de façade, qui court de y=-2 à y=hauteur
    /// (CityGenerator.AddWallSegment). Le point d'impact brut valait alors n'importe quelle hauteur
    /// intermédiaire — au-dessus du seuil de strate, donc pris pour un point de toit — et l'unité
    /// terminait le tour suspendue en l'air contre le mur. On resonde donc verticalement, comme le
    /// fait déjà ConfirmerBuildingAction, avec repli sur la hauteur déclarée du bâtiment.</summary>
    private static Vector3 ProjectOnRoofSurface(Vector3 hitPoint, BuildingStructure structureUnderTap)
    {
        BuildingStructure roofOwner = structureUnderTap != null ? structureUnderTap : BuildingStructure.FindBuildingAt(hitPoint);
        if (roofOwner == null) return hitPoint;

        float probeTop = Mathf.Max(roofOwner.height, hitPoint.y) + 4f;
        if (Physics.Raycast(new Vector3(hitPoint.x, probeTop, hitPoint.z), Vector3.down,
                            out RaycastHit roofSurface, probeTop + 2f,
                            UnitAI.WorldGeometryMask, QueryTriggerInteraction.Ignore)
            && roofSurface.point.y > UnitAI.RoofStrataThresholdY)
        {
            return new Vector3(hitPoint.x, roofSurface.point.y + 0.05f, hitPoint.z);
        }

        float fallbackHeight = (roofOwner.height > 0f) ? roofOwner.height : 6.0f;
        return new Vector3(hitPoint.x, fallbackHeight + 0.05f, hitPoint.z);
    }

    /// <summary>L'unité est-elle (ou sera-t-elle, une fois son tour exécuté) postée sur un toit ?
    ///
    /// Consulte la hauteur RÉELLE de l'unité, MAIS AUSSI le dernier nœud déjà planifié dans son
    /// tacticalPath (pas encore exécuté) — un Escalade en attente compte déjà comme "sur le toit",
    /// un Descendre en attente annule cet état. Sans ce second test (correctif 2026-09-05), un
    /// second tap sur le MÊME toit après avoir mis en file "MONTER SUR LE TOIT" (mais avant
    /// l'exécution du tour, seul moment où isRooftopSniper devient vrai) rouvrait le menu BÂTIMENT
    /// au lieu du menu "DÉPLACEMENT TOIT" — rendant GUETTER/ATTENDRE inatteignables sur ce second
    /// point pour tout le tour. Miroir exact de la règle déjà correcte dans
    /// TacticalPathManager_ContextMenu.ShowGroundCheckpointMenu (unitIsAlreadyOnRoof) : les deux
    /// DOIVENT rester identiques, l'une décide QUEL menu router, l'autre QUOI y afficher.</summary>
    private static bool IsUnitAlreadyOnOrHeadedToRoof(UnitAI unitAI)
    {
        if (unitAI == null) return false;

        bool onRoof = unitAI.isRooftopSniper || unitAI.transform.position.y > UnitAI.RoofStrataThresholdY;

        if (unitAI.tacticalPath.Count > 0)
        {
            TacticalNode lastNode = unitAI.tacticalPath[unitAI.tacticalPath.Count - 1];
            if (lastNode.action == NodeAction.Descendre) onRoof = false;
            else if (lastNode.action == NodeAction.Escalade || lastNode.position.y > UnitAI.RoofStrataThresholdY) onRoof = true;
        }

        return onRoof;
    }

    /// <summary>Retire le dernier point posé par l'unité sélectionnée, son hologramme et son tracé.
    /// Appelée par le clic DROIT (PC) et par le bouton "↶" de la barre tactique (tactile) — voir
    /// TacticalPathManager_UI.BindTacticalUI : jusqu'au 2026-09-07 seul le clic droit y menait, donc
    /// aucun joueur sur téléphone ne pouvait annuler un point mal placé.</summary>
    public void AnnulerDernierPoint()
    {
        if (uniteSelectionnee == null) return;
        UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
        if (unitAI == null || unitAI.tacticalPath.Count == 0) return;

        unitAI.RemoveLastTacticalNode();
        DessinerTousLesChemins();
        if (Camera.main != null)
            AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        Debug.Log($"[{unitAI.gameObject.name}] ↩️ Dernier checkpoint supprimé.");
    }

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
                AnnulerDernierPoint();
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

            // `if`, PAS `else if` (correctif 2026-09-04) : l'appui et le relâchement d'un tap bref
            // arrivent dans la MÊME frame dès que la cadence baisse (ce qui était précisément le cas,
            // voir le coût du tracé et de l'A*) ou quand le joueur tape vite. Avec un `else if`, seule
            // la branche d'appui s'exécutait, isActionClick n'était jamais levé et le tap était
            // PUREMENT PERDU — isPointerDown restait même armé avec une position périmée. C'est le
            // "la sélection ne répond plus" : le joueur tape sur son unité, rien ne se passe.
            if (wasReleased && isPointerDown)
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
            // exiger un hit pixel-parfait sur son collider.
            // 2026-09-06 : 45px -> 60px — remonté sur retour explicite ("je ne peux pas définir de
            // trajectoire pour d'autres unités après la première") : à 45px, un tap légèrement
            // imprécis sur une AUTRE unité (surtout petite/lointaine en vue oblique, ou dans un
            // groupe serré) tombait sous ce seuil et se retrouvait interprété comme un ordre pour
            // l'unité déjà sélectionnée au lieu d'une sélection de la nouvelle — donnant
            // l'impression que "les autres unités ne répondent plus". Un tap PRÉCIS pile sur le
            // collider d'une autre unité fonctionnait déjà quel que soit ce seuil (voir
            // "ARBITRAGE PAR DISTANCE-ÉCRAN" juste en dessous, le raycast direct l'emporte toujours
            // si la boucle tolérante ne trouve rien) — ce réglage ne concernait que les taps
            // "presque" sur la cible. Toujours plus strict que 75px (protection contre le vol de
            // commande toujours en place, juste moins agressive).
            bool isGivingOrderToSelectedUnit = phaseActuelle == GamePhase.Planification && uniteSelectionnee != null;
            float maxTouchRadiusPx = isGivingOrderToSelectedUnit ? 60f : 75f;
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

            // ARBITRAGE PAR DISTANCE-ÉCRAN (correctif 2026-09-06). Avant, un hit du Physics.Raycast
            // direct sur un allié gagnait INCONDITIONNELLEMENT (la boucle tolérante ci-dessous,
            // seule logique capable de départager plusieurs unités proches, était gardée par
            // `if (closestUnit == null)` — jamais exécutée dans ce cas). Tant que les unités étaient
            // de simples badges plats masqués en vue Commandement, le rayon ne pouvait quasiment
            // jamais toucher qu'un point au ras du sol de l'unité visée : pas de conséquence. Depuis
            // le retrait de ce masquage, le vrai modèle 3D (hauteur réelle, formes très différentes
            // d'un type à l'autre) reste visible en 2D — et la vue Commandement reste de toute façon
            // OBLIQUE (bridée à 75°, jamais un vrai zénithal, voir TacticalPathManager_ContextMenu),
            // donc un modèle peut désormais en occulter visuellement un AUTRE dans une formation
            // resserrée : le rayon touche alors l'unité de devant/occultante, pas celle réellement
            // tapée, sans le moindre indice pour le joueur. On calcule maintenant TOUJOURS le
            // meilleur candidat par distance à l'écran (comme avant), et on ne laisse le hit direct
            // l'emporter que s'il reste, LUI, au moins aussi proche du tap que ce candidat — sinon
            // l'unité réellement la plus proche du point tapé gagne à sa place.
            UnitAI raycastUnit = closestUnit;
            closestUnit = null;
            {
                for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
                {
                    UnitAI unit = UnitAI.AllLivingUnits[i];
                    if (unit != null && unit.isPlayerControlled && !unit.isDead)
                    {
                        // SelectionAnchorWorldPos (pas transform.position) : pour un blindé, le
                        // pivot d'import peut être décalé de plusieurs mètres du centre visuel réel
                        // (voir son commentaire dans UnitAI.cs) — root cause confirmée du rapport
                        // "difficile de sélectionner les unités en multijoueur" (2026-09-09).
                        Vector3 screenPoint = Camera.main.WorldToScreenPoint(unit.SelectionAnchorWorldPos);
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

            if (raycastUnit != null)
            {
                if (closestUnit == null || closestUnit == raycastUnit)
                {
                    // Rien de plus proche trouvé par la boucle tolérante (ou c'est la même unité) :
                    // le hit direct l'emporte, exactement comme avant ce correctif — comportement
                    // inchangé pour le cas normal (tap franc sur une unité isolée), et filet de
                    // sécurité si l'unité touchée est un grand véhicule dont le pivot ne tombe pas
                    // sous le doigt malgré un hit bien réel sur son collider.
                    closestUnit = raycastUnit;
                }
                else
                {
                    // Un AUTRE allié a été trouvé strictement plus proche du tap à l'écran que le
                    // meilleur candidat courant : comparer aussi la distance-écran DE l'unité
                    // touchée par le raycast, pas seulement celle du hit lui-même, avant de
                    // trancher.
                    Vector3 raycastScreenPoint = Camera.main.WorldToScreenPoint(raycastUnit.SelectionAnchorWorldPos);
                    float raycastScreenDist = raycastScreenPoint.z > 0
                        ? Vector2.Distance(pointerPosition, new Vector2(raycastScreenPoint.x, raycastScreenPoint.y))
                        : float.MaxValue;
                    if (raycastScreenDist <= closestScreenDist) closestUnit = raycastUnit;
                    // sinon closestUnit reste l'autre allié, réellement plus proche du tap.
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
                // Même règle de propriété que la sélection d'unité (isPlayerControlled) : on ne peut
                // ouvrir le menu (et donc retirer via ConfirmerRetirerBarricade) que sur une barricade
                // de son propre camp (équipe 1), jamais celle de l'adversaire IA — sans ce garde,
                // taper une barricade ennemie la détruisait gratuitement.
                if (hitBarrier != null && hitBarrier.teamID == 1)
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
                // Le joueur désigne-t-il VRAIMENT ce bâtiment ? Vrai si le point tapé est dans
                // l'empreinte, ou si le rayon a touché la coque du bâtiment (mur, toit, sol 2D).
                bool pointingAtBuilding = structure != null;
                if (structure == null && hit.collider != null)
                {
                    structure = hit.collider.GetComponentInParent<BuildingStructure>();
                    pointingAtBuilding = IsBuildingShellCollider(hit.collider, structure);
                }

                // FAÇADE RASÉE PAR LA VUE OBLIQUE : le bâtiment n'a été trouvé que par le collider
                // touché, ET ce collider n'appartient pas à la coque du bâtiment — c'est donc du
                // mobilier urbain (lampadaire, arbre, banc) que StreetPropsGenerator crée comme
                // ENFANT du bâtiment mais pose HORS de son empreinte. Le joueur montrait la rue et le
                // rayon a effleuré un décor au passage (la caméra de commandement est bridée à 75°
                // d'inclinaison, un immeuble masque donc une bande de rue derrière lui). Ce repli
                // existait déjà mais n'était appliqué qu'aux blindés : l'infanterie recevait le menu
                // BÂTIMENT, qui ne propose aucun "aller là".
                //
                // IsBuildingShellCollider est indispensable (correctif 2026-09-04). Sans lui, le
                // repli se déclenchait aussi sur la FAÇADE elle-même, une fois sur deux : les murs
                // sont des quads d'épaisseur nulle posés exactement sur les arêtes de l'empreinte
                // (CityGenerator.AddWallSegment), le point d'impact tombe donc pile SUR l'arête, et
                // le test de parité de ContainsPoint2D (comparaison stricte) basculait d'un côté ou
                // de l'autre selon l'erreur flottante du raycast. Deux taps sur le même pixel
                // donnaient des menus différents : ENTRER / MONTER SUR LE TOIT devenait un tirage au
                // sort, et une unité déjà à l'intérieur était renvoyée dehors.
                if (structure != null && !pointingAtBuilding && unitAI != null && !unitAI.isRooftopSniper
                    && TryFallbackAuSolPourBlinde(hitPoint))
                {
                    return;
                }

                bool isRubblePoint = DestructibleEnvironment.IsPositionInRubble(hitPoint) ||
                                     (structure != null && structure.GetComponent<DestructibleEnvironment>() != null && structure.GetComponent<DestructibleEnvironment>().isDestroyed);

                // Une unité DÉJÀ sur le toit de CE bâtiment qui tape son propre toit veut s'y déplacer,
                // pas recevoir le menu "entrer / monter sur le toit". Sans cette exception, tout tap
                // dans l'empreinte ouvrait le menu BÂTIMENT et le déplacement de toit à toit — le
                // premier intérêt d'un poste haut — était purement et simplement inatteignable : le
                // seul ordre proposé restait "MONTER SUR LE TOIT", là où l'unité se tenait déjà.
                bool tapOnOwnRooftop = false;
                if (structure != null && !isRubblePoint && unitAI != null && !unitAI.isTank && IsUnitAlreadyOnOrHeadedToRoof(unitAI))
                {
                    // Le bâtiment "tenu" est celui du dernier nœud planifié s'il y en a un (l'unité
                    // n'y est pas encore physiquement), sinon sa position réelle actuelle.
                    Vector3 standingRef = unitAI.transform.position;
                    if (unitAI.tacticalPath.Count > 0) standingRef = unitAI.tacticalPath[unitAI.tacticalPath.Count - 1].position;
                    BuildingStructure standingOn = BuildingStructure.FindBuildingAt(standingRef);
                    tapOnOwnRooftop = (standingOn == structure);
                }

                // CAS BÂTIMENT INTACT : L'infanterie tape dans le polygone
                if (structure != null && !isRubblePoint && !tapOnOwnRooftop)
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
                bool isSelectedOnRoof = IsUnitAlreadyOnOrHeadedToRoof(unitAI);

                UnityEngine.AI.NavMeshHit navHit;
                bool hasNavMesh = UnityEngine.AI.NavMesh.SamplePosition(hitPoint, out navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas);

                if (hasNavMesh || isUnitPlanningInside || isRubblePoint || isSelectedOnRoof)
                {
                    if (isRubblePoint || isUnitPlanningInside) positionClicTemporaire = new Vector3(hitPoint.x, 0.05f, hitPoint.z);
                    else if (isSelectedOnRoof) positionClicTemporaire = ProjectOnRoofSurface(hitPoint, structure);
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
