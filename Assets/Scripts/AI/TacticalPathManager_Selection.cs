using System.Collections.Generic;
using UnityEngine;

public partial class TacticalPathManager
{
    // ==========================================
    // SÉLECTION D'UNITÉ (joueur direct, cloche "unité blessée", cycle depuis la squad-bar)
    // ==========================================

    private void SelectionnerUnite(GameObject unite)
    {
        if (unite != null)
        {
            UnitAI unitAI = unite.GetComponent<UnitAI>();

            // On ne peut sélectionner que les unités du joueur. Tous les appelants actuels
            // (ResolveClosestPlayerUnit, RefreshSquadBar/CycleSelectGroup, la cloche "blessés")
            // filtrent déjà isPlayerControlled avant d'arriver ici, mais ce garde reste la SEULE
            // protection si l'un d'eux rate ce filtre un jour — et jusqu'ici, en cas d'échec,
            // `uniteSelectionnee = unite` était affecté INCONDITIONNELLEMENT plus haut (avant ce
            // test), qu'il soit valide ou pas : la cible rejetée devenait quand même "la sélection
            // courante" en interne (aucun cercle, aucun son, mais isGivingOrderToSelectedUnit
            // passait quand même à vrai dans HandlePointerInput, rétrécissant le rayon de tap
            // tolérant à 60px). Vu du joueur : ce clic ne faisait RIEN, et tant qu'aucune sélection
            // VALIDE n'avait eu lieu depuis, TOUTE autre unité tolérait moins l'imprécision du tap
            // suivant — donnant l'impression qu'il fallait d'abord réussir à sélectionner UNE unité
            // pour que "la sélection des autres" se débloque. On ne touche donc plus du tout à
            // uniteSelectionnee ni à l'ancienne sélection tant que la cible n'est pas validée.
            if (unitAI == null || !unitAI.isPlayerControlled)
            {
                Debug.LogWarning($"[TacticalPathManager] Sélection rejetée sur '{unite.name}' " +
                    $"(unitAI={(unitAI != null)}, isPlayerControlled={(unitAI != null && unitAI.isPlayerControlled)}).");
                return;
            }

            // Désélectionner l'ancienne unité si nécessaire (seulement maintenant que la nouvelle
            // cible est confirmée valide).
            if (uniteSelectionnee != null && uniteSelectionnee != unite)
            {
                UnitAI ancienneUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (ancienneUnitAI != null) ancienneUnitAI.SetSelected(false);
            }

            FermerMenuContextuel(invokeCancelAction: true);

            uniteSelectionnee = unite;
            unitAI.SetSelected(true); // Activer le cercle visuel

            // Si l'unité est à l'intérieur d'un bâtiment, ouvrir le toit en preview immédiate !
            if (unitAI.currentBuilding != null && !unitAI.isRooftopSniper)
            {
                if (unitAI.currentBuilding.tacticalVisibility != null)
                {
                    unitAI.currentBuilding.tacticalVisibility.SetPlanificationPreview(true);
                }
            }

            // Son de sélection
            AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateHoverSound(), Camera.main.transform.position);
            PlayUnitVoiceLine(unitAI, isSelection: true);
            Debug.Log("Unité sélectionnée : " + unite.name);
            isPathsDirty = true;
        }
        else
        {
            if (uniteSelectionnee != null)
            {
                UnitAI ancienneUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (ancienneUnitAI != null) ancienneUnitAI.SetSelected(false);
            }

            // Toute désélection invalide de fait un menu contextuel en attente — centralisé via
            // FermerMenuContextuel pour que TOUS les appelants (clic sur le vide, fin de tour...)
            // bénéficient de la même remise à zéro complète (y compris currentMenuCancelAction/
            // ConfirmAction et le masquage du ContextMenu), sans dupliquer ce reset ici.
            FermerMenuContextuel(invokeCancelAction: true);

            uniteSelectionnee = null;
            isPathsDirty = true;

            // Réinitialiser les previews de bâtiments non occupés
            foreach (var b in BuildingStructure.AllBuildings)
            {
                if (b != null && b.tacticalVisibility != null && !b.IsAnyUnitInside())
                {
                    b.tacticalVisibility.SetPlanificationPreview(false);
                }
            }
            Debug.Log("Toutes les unités sont désélectionnées.");
        }
    }

    /// <summary>Cloche de notification (haut-droit) : sélectionne la prochaine unité alliée
    /// vivante en dessous de 50% de vie, pour que le joueur puisse réagir sans devoir la
    /// repérer visuellement sur la carte. Ne fait rien si aucune unité n'est en difficulté.</summary>
    private void SelectionnerProchaineUniteBlessee()
    {
        // Liste complète (pas juste "la première trouvée différente de la sélection actuelle") pour
        // pouvoir cycler correctement à travers 3+ unités blessées simultanées : sans ça, un appui
        // répété oscillait indéfiniment entre les deux premières de la liste, les suivantes n'étant
        // jamais atteintes.
        List<UnitAI> wounded = new List<UnitAI>();
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || !u.isPlayerControlled) continue;
            float pct = u.maxHealth > 0 ? (float)u.health / u.maxHealth : 1f;
            if (pct < 0.5f) wounded.Add(u);
        }
        if (wounded.Count == 0) return;

        int currentIndex = uniteSelectionnee != null ? wounded.FindIndex(u => u.gameObject == uniteSelectionnee) : -1;
        UnitAI candidate = wounded[(currentIndex + 1) % wounded.Count];

        phaseActuelle = GamePhase.Planification;
        SelectionnerUnite(candidate.gameObject);
    }

    /// <summary>Nombre d'unités alliées vivantes en dessous de 50% de vie — affiché en pastille
    /// sur la cloche de notification.</summary>
    private int CountWoundedPlayerUnits()
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || !u.isPlayerControlled) continue;
            float pct = u.maxHealth > 0 ? (float)u.health / u.maxHealth : 1f;
            if (pct < 0.5f) count++;
        }
        return count;
    }

    /// <summary>
    /// Petites répliques radio pour donner du caractère aux ordres (voir Assets/Resources/Sounds/)
    /// — une paire sélection/accusé de réception pour l'infanterie, une autre pour le reste
    /// (blindés, véhicules canon, mortiers).
    /// </summary>
    private void PlayUnitVoiceLine(UnitAI unitAI, bool isSelection)
    {
        if (unitAI == null || Camera.main == null) return;
        bool isInfantry = !unitAI.isMortar && !unitAI.isTank;
        string clipName = isSelection
            ? (isInfantry ? "Sounds/yes_sir_rex_sneaky_laugh" : "Sounds/target_locked_radio_deep")
            : (isInfantry ? "Sounds/ok_ill_do_it_rex" : "Sounds/roger_will_do_radio");
        AudioClip clip = Resources.Load<AudioClip>(clipName);
        if (clip != null) AudioSource.PlayClipAtPoint(clip, Camera.main.transform.position);
    }
}
