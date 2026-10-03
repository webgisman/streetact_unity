using UnityEngine;

public partial class TacticalPathManager
{
    // ==========================================
    // ROUTAGE D'UN TAP SUR LE MONDE (barricade / mortier / porte / fenêtre / bâtiment / sol)
    //
    // Extrait de TacticalPathManager_Input.HandlePointerInput le 2026-09-19 — cette cascade ne
    // s'exécute qu'UNE FOIS que UnitSelectionResolver a déterminé qu'aucune unité alliée n'est visée
    // par ce tap ; elle décide alors ce que représente le point tapé pour l'unité actuellement
    // sélectionnée (ou pour une barricade, indépendamment de toute sélection). Aucun changement de
    // comportement par rapport à la version précédente : seul le découpage en fichiers change.
    // ==========================================

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

    /// <summary>Un point de sol est-il au contact d'un mur/bâtiment (rayon de 2,2m) ? Détermine si le
    /// menu SOL propose l'option de couverture "SE CACHER". Partagé avec
    /// TacticalPathManager_ContextMenu.TryFallbackAuSolPourBlinde, qui recalcule exactement le même
    /// test pour le même besoin — une seule définition évite qu'un futur ajustement du rayon ou des
    /// noms reconnus ne soit appliqué qu'à l'un des deux appelants.</summary>
    private static bool IsPositionNearBuildingWall(Vector3 pos)
    {
        Collider[] nearby = Physics.OverlapSphere(pos, 2.2f);
        foreach (var c in nearby)
        {
            if (c.GetComponentInParent<BuildingStructure>() != null || c.gameObject.name.Contains("Building") || c.gameObject.name.Contains("Mur") || c.gameObject.name.Contains("Wall") || c.gameObject.name.Contains("Polygone"))
                return true;
        }
        return false;
    }

    /// <summary>Son de clic joué à chaque sélection/confirmation valide (barricade, mortier, porte,
    /// fenêtre, bâtiment, sol) — un seul point d'appel, plutôt que 7 copies de la même ligne.</summary>
    private void PlayClickFeedback()
    {
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
    }

    /// <summary>Referme le menu resté ouvert d'une sélection précédente, joue le son d'erreur et
    /// affiche le toast "tap invalide" — la même séquence de 3 étapes qui suit chaque refus de tap
    /// (porte/fenêtre/bâtiment pour un blindé sans repli sol, ou point hors NavMesh).</summary>
    private void RejectTapWithFeedback()
    {
        if (AnyOrderMenuOpen) FermerMenuContextuel(invokeCancelAction: true);
        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
#if !UNITY_SERVER
        ShowInvalidTapFeedback();
#endif
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

        // Point hors de l'empreinte : c'est la RUE, donc un point de sol (2026-10-03). Avant, un tap
        // de rue qui touchait un décor (arbre, lampadaire, rattaché au bâtiment le plus proche)
        // devenait un point « à hauteur de toit » suspendu au-dessus de la rue (repli ci-dessous) ;
        // l'unité perchée marchait alors jusqu'au bord de son toit et s'y arrêtait, au lieu de
        // descendre — exactement le retour du joueur : « il marche un peu sur le toit et s'arrête ».
        if (roofOwner == null || !IsOnOrJustInsideFootprint(roofOwner, hitPoint)) return GroundPointUnder(hitPoint);

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

    /// <summary>Dans l'empreinte, ou pile sur son bord : un tap sur la façade proche frappe le mur
    /// exactement sur l'arête de l'empreinte (voir ProjectOnRoofSurface), où le test de parité
    /// bascule d'un côté ou de l'autre — 0,5 m vers le centre, le point est franchement dedans.</summary>
    private static bool IsOnOrJustInsideFootprint(BuildingStructure building, Vector3 point)
    {
        if (building.ContainsPoint2D(point)) return true;
        Vector3 towardCenter = building.centroid - point;
        towardCenter.y = 0f;
        return towardCenter.sqrMagnitude > 0.0001f && building.ContainsPoint2D(point + towardCenter.normalized * 0.5f);
    }

    /// <summary>Point de sol (NavMesh de la rue) sous un point tapé.</summary>
    private static Vector3 GroundPointUnder(Vector3 point)
    {
        Vector3 ground = new Vector3(point.x, 0.05f, point.z);
        if (UnityEngine.AI.NavMesh.SamplePosition(ground, out UnityEngine.AI.NavMeshHit nav, 4.5f, UnityEngine.AI.NavMesh.AllAreas)
            && nav.position.y <= UnitAI.RoofStrataThresholdY)
        {
            return nav.position;
        }
        return ground;
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

    /// <summary>Décide ce que représente un tap qui n'a désigné AUCUNE unité alliée (voir
    /// UnitSelectionResolver, déjà consulté par l'appelant) : une barricade, une cible de mortier,
    /// une porte, une fenêtre, un bâtiment, ou un simple point de déplacement au sol/ruines/toit.</summary>
    private void RouteTapOnWorld(RaycastHit hit, bool hasHit, Vector3 hitPoint)
    {
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
                PlayClickFeedback();
#if !UNITY_SERVER
                ShowBarricadeMenu();
#endif
                TapDiagnosticOverlay.Report($"Menu BARRICADE ouvert : {hitBarrier.gameObject.name} @ {hitPoint:F1}.");
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

                PlayClickFeedback();
#if !UNITY_SERVER
                ShowGroundCheckpointMenu();
#endif
                TapDiagnosticOverlay.Report($"Cible MORTIER posée : {unitAI.gameObject.name} -> {hitPoint:F1}.");
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
                    if (TryFallbackAuSolPourBlinde(hitPoint))
                    {
                        TapDiagnosticOverlay.Report($"{unitAI.gameObject.name} (lourde) : porte refusée, repli SOL accepté près de {hitPoint:F1}.");
                        return;
                    }
                    Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas entrer dans les bâtiments !");
                    // Ce blocage ne montrait jusqu'ici RIEN à l'écran (juste un bip + un warning
                    // Console invisible en dehors de l'Éditeur) — le joueur croyait le tap ignoré
                    // en silence. RejectTapWithFeedback réutilise le même toast que pour un tap
                    // hors-NavMesh pour que "ce tap ne peut pas s'appliquer à cette unité" soit
                    // toujours visible, et referme tout menu resté ouvert d'une sélection PRÉCÉDENTE
                    // valide (sinon TERMINÉ confirmerait encore l'ancienne cible).
                    RejectTapWithFeedback();
                    TapDiagnosticOverlay.Report($"REFUS : {unitAI.gameObject.name} (lourde) ne peut pas entrer par une porte (aucun repli sol dispo).");
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

                PlayClickFeedback();
#if !UNITY_SERVER
                ShowDoorMenu();
#endif
                TapDiagnosticOverlay.Report($"Menu PORTE ouvert : {unitAI.gameObject.name}, bâtiment={clickedDoor.building?.gameObject.name}.");
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
                    if (TryFallbackAuSolPourBlinde(hitPoint))
                    {
                        TapDiagnosticOverlay.Report($"{unitAI.gameObject.name} (lourde) : fenêtre refusée, repli SOL accepté près de {hitPoint:F1}.");
                        return;
                    }
                    Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas utiliser les fenêtres !");
                    RejectTapWithFeedback();
                    TapDiagnosticOverlay.Report($"REFUS : {unitAI.gameObject.name} (lourde) ne peut pas utiliser une fenêtre (aucun repli sol dispo).");
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

                PlayClickFeedback();
#if !UNITY_SERVER
                ShowWindowMenu();
#endif
                TapDiagnosticOverlay.Report($"Menu FENÊTRE ouvert : {unitAI.gameObject.name}, fenêtre id={clickedWindow.windowData?.id}.");
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
            //
            // Vaut aussi pour une unité perchée (2026-10-03) : elle en était exclue, et ce tap de rue
            // devenait un déplacement sur son propre toit (voir tapOnOwnRooftop) — le menu SOL
            // propose au contraire de DESCENDRE dans la rue.
            if (structure != null && !pointingAtBuilding && unitAI != null
                && TryFallbackAuSolPourBlinde(hitPoint))
            {
                TapDiagnosticOverlay.Report($"{unitAI.gameObject.name} : façade rasée par la vue oblique près de {structure.gameObject.name}, repli SOL accepté.");
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
            if (structure != null && pointingAtBuilding && !isRubblePoint && unitAI != null && !unitAI.isTank && IsUnitAlreadyOnOrHeadedToRoof(unitAI))
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
                    if (TryFallbackAuSolPourBlinde(hitPoint))
                    {
                        TapDiagnosticOverlay.Report($"{unitAI.gameObject.name} (lourde) : bâtiment {structure.gameObject.name} refusé, repli SOL accepté près de {hitPoint:F1}.");
                        return;
                    }
                    // Diagnostic : imprime le bâtiment matché + ses bornes vs le point tapé, pour
                    // distinguer un vrai tap sur le bâtiment d'un polygone d'empreinte trop large
                    // (débordant sur la rue) qui bloquerait un tap pourtant fait à côté.
                    Debug.LogWarning($"[TacticalPathManager] Les chars ne peuvent pas entrer dans les bâtiments intacts ! " +
                        $"(bâtiment='{structure.gameObject.name}', tap={hitPoint:F2}, bounds2D.min={structure.bounds2D.min:F2}, bounds2D.max={structure.bounds2D.max:F2})");
                    RejectTapWithFeedback();
                    TapDiagnosticOverlay.Report($"REFUS : {unitAI.gameObject.name} (lourde) ne peut pas entrer dans {structure.gameObject.name} (aucun repli sol dispo, tap={hitPoint:F1}).");
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

                PlayClickFeedback();
#if !UNITY_SERVER
                ShowBuildingMenu();
#endif
                TapDiagnosticOverlay.Report($"Menu BÂTIMENT ouvert : {unitAI.gameObject.name} -> {structure.gameObject.name}.");
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

                isNearBuildingWall = (!isSelectedOnRoof && !isUnitPlanningInside) && IsPositionNearBuildingWall(positionClicTemporaire);

                PlayClickFeedback();
#if !UNITY_SERVER
                ShowGroundCheckpointMenu();
#endif
                TapDiagnosticOverlay.Report(
                    $"Point SOL posé : {unitAI.gameObject.name} -> {positionClicTemporaire:F1} " +
                    $"(tap brut={hitPoint:F1}, NavMesh={(hasNavMesh ? $"OK à {Vector3.Distance(hitPoint, navHit.position):F1}m" : "absent")}, " +
                    $"ruine={isRubblePoint}, toit={isSelectedOnRoof}, intérieur={isUnitPlanningInside}).");
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
                RejectTapWithFeedback();
                // Le rapport le plus utile de tous pour "je tape dans le vide, rien ne se passe" —
                // dit EXACTEMENT pourquoi ce point précis a été jugé inatteignable : aucun point
                // NavMesh walkable dans les 4,5m (voir NavMesh.SamplePosition ci-dessus) ET aucune
                // des exceptions (ruine/toit/intérieur) ne s'appliquait. hit.collider (nom du VRAI
                // objet touché par le rayon, s'il y en a un) permet souvent de voir directement la
                // cause : un collider de décor (lampadaire, banc) sans NavMesh sous lui, par exemple.
                TapDiagnosticOverlay.Report(
                    $"REJETÉ (hors NavMesh) : {unitAI?.gameObject.name} -> tap={hitPoint:F1}, " +
                    $"collider touché={(hit.collider != null ? hit.collider.name : "aucun (plan de sol de secours)")}, " +
                    $"bâtiment détecté={(structure != null ? structure.gameObject.name : "aucun")}, " +
                    $"ruine={isRubblePoint}, toit={isSelectedOnRoof}, intérieur={isUnitPlanningInside}.");
            }
        }
    }
}
