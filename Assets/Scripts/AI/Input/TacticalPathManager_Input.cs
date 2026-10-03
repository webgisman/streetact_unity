using UnityEngine;
using UnityEngine.InputSystem;
using Novgov.Gestures;

public partial class TacticalPathManager
{
    // ==========================================
    // DÉTECTION DU TAP/CLIC — orchestration seulement ; les briques sont dans Assets/Scripts/AI/Input/ :
    //   - PointerFrameReader : lecture tactile/souris (priorité entre périphériques) ;
    //   - GestureScale : échelle DPI, partagée avec TacticalCamera ;
    //   - TapGestureDetector : tap ou glissement ;
    //   - UnitSelectionResolver : quelle unité (un raycast direct gagne toujours) ;
    //   - TacticalPathManager_TapActionRouter : barricade, mortier, porte, fenêtre, bâtiment, sol.
    // ==========================================

    private readonly TapGestureDetector tapGesture = new TapGestureDetector(45f);

    // Un bouton du ContextMenu (option/ANNULER/TERMINÉ/vue 3D) qui masque le menu peut faire fuiter
    // le RELÂCHEMENT de ce même clic physique vers un raycast 3D brut, dans la MÊME frame (voir les
    // commentaires dans ShowContextMenu/BindTacticalUI) — armé à Time.frameCount par ces boutons,
    // vérifié ci-dessous pour sauter la cascade d'actions une seule frame, sans retarder le tap
    // suivant (contrairement à un cooldown en secondes, qui bloquait aussi la sélection légitime
    // d'une autre unité juste après un TERMINÉ rapide).
    private int suppressPointerInputUntilFrame = -1;

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

    /// <summary>Traite un tap/clic unique par frame (voir Update()) : clic droit pour annuler, tap
    /// détecté via <see cref="TapGestureDetector"/>, sélection d'unité via
    /// <see cref="UnitSelectionResolver"/>, puis délègue au routeur d'action tactique
    /// (RouteTapOnWorld, dans TacticalPathManager_TapActionRouter.cs) pour l'unité actuellement
    /// sélectionnée en phase de Planification.</summary>
    private void HandlePointerInput()
    {
        PointerFrame frame = PointerFrameReader.Read();

        // Clic Droit : Annulation rapide du menu ou du dernier checkpoint (PC uniquement — aucun
        // équivalent tactile, un clic droit n'a pas de sens sur un doigt).
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

        if (!tapGesture.TryDetectTap(frame, out Vector2 tapPosition)) return;
        if (Time.frameCount <= suppressPointerInputUntilFrame) return;

        // Bloquer si le joueur est en train de déployer une nouvelle unité depuis le QG.
        // Vérifie AUSSI lastPlacementActionFrame (pas seulement IsPlacingUnit) : sur un tap
        // simple, UnitSpawnerUI pose l'unité PUIS repasse IsPlacingUnit à false dans son propre
        // Update() — si celui-ci s'exécute avant le nôtre dans la même frame, IsPlacingUnit
        // seul ne suffit plus à détecter que ce relâchement de clic vient d'être consommé par
        // le placement (voir le commentaire sur lastPlacementActionFrame).
        if (UnitSpawnerUI.IsPlacingUnit || Time.frameCount <= UnitSpawnerUI.lastPlacementActionFrame) return;

        // Raycast 3D direct — calculé ICI, AVANT le test d'UI ci-dessous (2026-09-19 : voir le
        // paragraphe "UN TAP DIRECT SUR UNE AUTRE UNITÉ..." juste plus bas pour pourquoi l'ordre a
        // changé). Toujours calculé, même hors Planification/sans unité sélectionnée : hasHit/
        // hitPoint alimentent aussi RouteTapOnWorld plus bas.
        Ray ray = Camera.main.ScreenPointToRay(tapPosition);
        RaycastHit hit;
        Vector3 hitPoint = Vector3.zero;
        bool hasHit = false;
        UnitAI raycastUnit = null;

        if (Physics.Raycast(ray, out hit))
        {
            hitPoint = hit.point;
            hasHit = true;

            UnitAI hitUnit = hit.collider.GetComponent<UnitAI>() ?? hit.collider.GetComponentInParent<UnitAI>();
            if (hitUnit != null && hitUnit.isPlayerControlled && !hitUnit.isDead)
            {
                raycastUnit = hitUnit;
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

        // Un tap DIRECT sur une autre unité du joueur change toujours la sélection, même par-dessus un
        // panneau d'absorption (fond du menu d'ordre, dock) — mais jamais à travers un VRAI bouton
        // (IsPointerOverInteractiveControl) : toucher FIN DE TOUR ne doit pas aussi sélectionner l'unité
        // située derrière dans la scène.
        if (raycastUnit != null && !(UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverInteractiveControl(tapPosition)))
        {
            phaseActuelle = GamePhase.Planification;
            SelectionnerUnite(raycastUnit.gameObject); // referme aussi tout menu ouvert (voir SelectionnerUnite)
            TapDiagnosticOverlay.Report(DescribeSelectedUnitOwnership("Sélection DIRECTE (raycast)", raycastUnit));
            return;
        }

        // Vérifier si le clic est sur un bouton de l'interface — un seul test générique
        // (UI Toolkit picking) couvre désormais la barre du bas, les menus contextuels et
        // le dock de déploiement, plus besoin de Rect codées en dur par écran. N'est plus atteint
        // que pour un tap qui n'a touché AUCUNE unité directement (voir juste au-dessus).
        if (UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverOnGUI(tapPosition, out string absorbedBy))
        {
            TapDiagnosticOverlay.Report($"Tap absorbé par : {absorbedBy} — aucune action 3D.");
            return;
        }

        // Sélection tolérante (raycastUnit est null ici) : 75 px quand rien n'est sélectionné, 60 px une fois
        // qu'on donne des ordres à une unité (un tap à côté d'un allié ne doit pas lui voler la commande).
        // Seuils mis à l'échelle DPI (GestureScale).
        bool isGivingOrderToSelectedUnit = phaseActuelle == GamePhase.Planification && uniteSelectionnee != null;
        float maxTouchRadiusPx = (isGivingOrderToSelectedUnit ? 60f : 75f) * GestureScale.TouchDpiScale;

        bool hitExplicitTarget = false;
        if (hasHit && hit.collider != null && raycastUnit == null)
        {
            // Vérifier si le joueur tape intentionnellement sur un élément interactif ou un ennemi.
            if (hit.collider.GetComponentInParent<UnitAI>() != null ||
                hit.collider.GetComponentInParent<RoadBarrier>() != null ||
                hit.collider.GetComponentInParent<Novgov.Interaction.DoorInteraction>() != null ||
                hit.collider.GetComponentInParent<Novgov.Interaction.WindowInteraction>() != null)
            {
                hitExplicitTarget = true;
            }
        }

        // Si le joueur a explicitement tapé sur une cible (ennemi, barricade, porte, fenêtre),
        // on ne cherche PAS d'unité alliée tolérante à proximité : on veut interagir avec la cible visée.
        UnitAI closestUnit = hitExplicitTarget ? null : UnitSelectionResolver.Resolve(tapPosition, Camera.main, raycastUnit, maxTouchRadiusPx);

        if (closestUnit != null)
        {
            phaseActuelle = GamePhase.Planification;
            SelectionnerUnite(closestUnit.gameObject); // referme aussi tout menu ouvert (voir SelectionnerUnite)
            TapDiagnosticOverlay.Report(DescribeSelectedUnitOwnership($"Sélection TOLÉRANTE (rayon {maxTouchRadiusPx:F0}px, pas de hit direct)", closestUnit));
            return;
        }

        RouteTapOnWorld(hit, hasHit, hitPoint);
    }

    /// <summary>Trace, à chaque sélection, le camp de l'unité tapée et l'équipe locale — de quoi trancher
    /// un éventuel rapport « je contrôle les unités de l'autre joueur ».</summary>
    private static string DescribeSelectedUnitOwnership(string label, UnitAI unit)
    {
        // Novgov.Network.MultiplayerMatchController est déclarée dans un bloc #if !UNITY_SERVER (le
        // TYPE lui-même, pas seulement LocalTeamId) — inaccessible en compilation serveur, où ce
        // diagnostic n'a de toute façon aucun sens (pas de HUD à côté d'un serveur headless).
        string localTeamText = "n/a (solo)";
#if !UNITY_SERVER
        if (Novgov.Network.MultiplayerMatchController.Instance != null)
            localTeamText = Novgov.Network.MultiplayerMatchController.Instance.LocalTeamId.ToString();
#endif
        return $"{label} : {unit.gameObject.name} (teamID={unit.teamID}, isPlayerControlled={unit.isPlayerControlled}, " +
               $"localTeamId={localTeamText}).";
    }
}
