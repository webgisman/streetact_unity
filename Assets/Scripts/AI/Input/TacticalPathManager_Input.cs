using UnityEngine;
using UnityEngine.InputSystem;
using Novgov.Gestures;

public partial class TacticalPathManager
{
    // ==========================================
    // DÉTECTION DU TAP/CLIC (sélection d'unité, ordres de mouvement, menus contextuels)
    //
    // RESTRUCTURÉ le 2026-09-19, suite au retour répété "la sélection du fantassin est
    // problématique". Ce fichier faisait auparavant ~680 lignes et gérait, dans UNE SEULE méthode,
    // la lecture bas niveau du pointeur, la détection tap-vs-glissement, l'arbitrage "quelle unité
    // sélectionner" ET toute la cascade porte/fenêtre/bâtiment/sol/barricade/mortier. Cette
    // concentration rendait le système fragile : chaque bug de sélection rapporté depuis 2026-09-06
    // (pivot de véhicule, char qui vole la sélection d'un fantassin, seuils non mis à l'échelle DPI...)
    // n'a été corrigé qu'au prix d'un nouveau réglage local, sans jamais pouvoir isoler ni rejouer
    // chaque mécanique séparément — jusqu'à ce que le bug RACINE (voir UnitSelectionResolver) reste
    // caché derrière ces correctifs ponctuels.
    //
    // Découpage désormais en place (Assets/Scripts/AI/Input/) :
    //   - PointerFrameReader        : lecture tactile/souris/pointeur générique — un seul point de
    //                                 vérité sur la priorité entre périphériques.
    //   - GestureScale              : échelle DPI, PARTAGÉE avec TacticalCamera (pincer/pivoter) —
    //                                 auparavant dupliquée à l'identique dans les deux fichiers.
    //   - TapGestureDetector        : tap-vs-glissement, isolé et testable indépendamment.
    //   - UnitSelectionResolver     : arbitrage "quelle unité" — réécrit pour qu'un raycast direct
    //                                 (vérité de terrain) gagne TOUJOURS face à la boucle tolérante,
    //                                 au lieu de pouvoir en être supplanté (root cause du vol de
    //                                 sélection du fantassin — voir la doc de cette classe).
    //   - TacticalPathManager_TapActionRouter.cs : la cascade barricade/mortier/porte/fenêtre/
    //                                 bâtiment/sol, une fois qu'aucune unité n'est visée.
    // Ce fichier n'orchestre plus que l'enchaînement de ces briques.
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

        // UN TAP DIRECT SUR UNE AUTRE UNITÉ CHANGE TOUJOURS LA SÉLECTION, MÊME PAR-DESSUS UN MENU
        // OUVERT (2026-09-19, retour "le menu de trajectoire interfère avec la sélection des autres
        // unités"). Root cause : le ContextMenu (ORDRE TACTIQUE) et le dock de déploiement occupent
        // une large bande du bas de l'écran (ContextMenuScreen.uss, .context-panel { width: 92% }) —
        // tant qu'un menu reste ouvert pour l'unité sélectionnée, TOUT tap dans cette bande était
        // intercepté par IsPointerOverOnGUI ci-dessous (voir cette méthode : elle capture tout
        // élément UI Toolkit dont le picking-mode est Position, y compris le panneau lui-même, pas
        // seulement ses boutons) et n'atteignait donc plus jamais le raycast 3D — un fantassin/char
        // qui apparaît visuellement dans cette même bande (fréquent : la caméra tactique centre
        // souvent la sélection courante, les alliés proches finissent donc bas-écran eux aussi)
        // devenait alors IMPOSSIBLE à sélectionner tant que le menu de l'unité précédente restait
        // ouvert, sans le moindre message d'erreur — d'où l'impression que "changer d'unité pendant
        // qu'on trace une trajectoire" ne marche pas.
        //
        // Un raycast DIRECT sur le collider d'une autre unité du joueur est un signal sans
        // ambiguïté (même principe que UnitSelectionResolver : un hit direct est une vérité de
        // terrain) — il gagne donc TOUJOURS sur un simple panneau d'ABSORPTION (fond du ContextMenu,
        // dock), avant même de savoir si ce point de l'écran en touche un. Seul le raycast direct
        // bénéficie de cette priorité : la boucle tolérante (tap approximatif, plus bas) reste, elle,
        // soumise au test d'UI complet comme avant.
        //
        // CORRECTIF 2026-09-19 (retour joueur EN PLEINE PARTIE, quelques heures après le
        // raisonnement ci-dessus) : "le bouton FIN DE TOUR n'est pas mis en avant, quand je clique
        // dessus je clique sur la carte". Le raisonnement d'origine ("rien ici ne peut voler un clic
        // de bouton, le bouton UI Toolkit reçoit son `clicked` indépendamment de ce raycast 3D")
        // était VRAI mais incomplet : le bouton reçoit bien SON `clicked` sans interférence — mais
        // RIEN n'empêchait ce même bloc de resélectionner AUSSI une unité en même temps, si elle se
        // trouve, en 3D, juste derrière ce bouton (fréquent pour un bandeau FIXE comme FIN DE TOUR :
        // les unités du joueur se projettent souvent dans cette même zone d'écran). Le clic sur le
        // bouton "fonctionnait" (fin de tour lancée) MAIS s'accompagnait d'un changement de
        // sélection totalement inattendu — vécu comme "le clic agit aussi sur la carte". Un VRAI
        // contrôle interactif (bouton, etc. — voir IsPointerOverInteractiveControl, qui NE couvre
        // PAS les simples panneaux d'absorption comme IsPointerOverOnGUI) doit donc, lui, continuer
        // à bloquer TOUT effet secondaire 3D, y compris celui-ci.
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

        // 1. Détection tolérante des unités en espace écran (raycastUnit est garanti null ici, voir
        // juste au-dessus — seule la boucle par distance-écran de UnitSelectionResolver.Resolve
        // s'applique encore à ce stade).
        // Rayon tolérant réduit une fois qu'une unité est DÉJÀ sélectionnée en Planification :
        // au départ (rien sélectionné), 75px aide à choisir une unité facilement même en tapant
        // un peu à côté. Mais une fois qu'on donne des ordres à une unité, un tap sur le sol/un
        // bâtiment juste à côté d'un allié regroupé ne doit PAS lui voler la commande en silence
        // (voir bug précédent) — on garde donc un rayon plus strict qui ne capte plus qu'un tap
        // VRAIMENT visé sur cette autre unité, permettant toujours de changer de cible sans
        // exiger un hit pixel-parfait sur son collider.
        // 2026-09-06 : 45px -> 60px — remonté sur retour explicite ("je ne peux pas définir de
        // trajectoire pour d'autres unités après la première"). 2026-09-13 : ces deux seuils sont
        // mis à l'échelle DPI (GestureScale.TouchDpiScale, désormais partagée avec TacticalCamera).
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

    /// <summary>Preuve, à chaque sélection, de QUI possède réellement l'unité tapée — ajoutée
    /// 2026-09-19 suite au rapport "je crois qu'il y a un inversement de rôle... je peux jouer avec
    /// les unités de l'autre joueur" en pleine partie. L'audit du code (déploiement, apparition
    /// brouillard de guerre, filtrage serveur par équipe) n'a trouvé aucune incohérence — mais
    /// n'ayant pas pu reproduire le rapport en direct, la seule façon de trancher DÉFINITIVEMENT la
    /// prochaine fois est une preuve écran au moment exact de la sélection : teamID de l'unité,
    /// isPlayerControlled (déjà vérifié true par UnitSelectionResolver à ce stade, réaffiché ici
    /// explicitement plutôt que supposé), et l'équipe locale telle que
    /// MultiplayerMatchController.LocalTeamId la connaît à cet instant précis — si ces valeurs
    /// disent "tout est cohérent" alors qu'un blindé rouge répond quand même à la souris, la cause
    /// n'est PAS dans ce fichier et il faut regarder ailleurs (fenêtre Multiplayer Play Mode
    /// confondue avec l'autre joueur, par exemple — les deux affichent chacune leur PROPRE bannière
    /// d'équipe, voir MultiplayerMatchController.RefreshHudStaticFields).</summary>
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
