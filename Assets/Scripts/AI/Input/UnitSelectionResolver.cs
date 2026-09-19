using UnityEngine;

namespace Novgov.Gestures
{
    /// <summary>Arbitre quelle unité alliée un tap doit sélectionner.
    ///
    /// RÉÉCRIT STRUCTURELLEMENT le 2026-09-19, suite au retour répété "la sélection du fantassin est
    /// problématique". L'ancienne version (TacticalPathManager.ResolveClosestPlayerUnit, extraite le
    /// 2026-09-12) comparait la distance-écran d'un hit Physics.Raycast DIRECT à celle du meilleur
    /// candidat d'une boucle tolérante (distance-écran depuis <see cref="UnitAI.SelectionAnchorWorldPos"/>
    /// de CHAQUE unité vivante du joueur), et laissait la boucle tolérante l'emporter dès qu'elle
    /// rapportait un candidat strictement plus proche — alors qu'un raycast direct est une VÉRITÉ DE
    /// TERRAIN (le doigt/curseur est PHYSIQUEMENT sur le collider de cette unité, dans la scène 3D
    /// réellement affichée) et ne devrait jamais perdre face à une heuristique de proximité
    /// approximative en 2D écran.
    ///
    /// C'est cette inversion de priorité — pas la détection de tap elle-même, déjà correcte — qui
    /// explique la famille de bugs "je tape précisément sur le fantassin, une autre unité (plus
    /// grosse, dont l'ancre projetée à l'écran passait juste un peu plus près du point tapé) se
    /// sélectionne à sa place". Un fantassin, plus petit à l'écran, est structurellement plus souvent
    /// victime de ce vol de sélection qu'un char (dont le grand collider capte presque toujours le
    /// raycast en premier lieu, masquant le problème pour ce type d'unité).
    ///
    /// Nouvelle règle, plus simple et strictement plus prévisible pour le joueur : un hit raycast
    /// direct gagne TOUJOURS, sans condition. La boucle tolérante par distance-écran n'intervient
    /// désormais QUE si le raycast n'a touché AUCUNE unité du joueur — exactement le cas qu'elle a
    /// été conçue pour rattraper (tap légèrement à côté du collider réel), jamais un cas où une cible
    /// a déjà été identifiée sans ambiguïté.</summary>
    public static class UnitSelectionResolver
    {
        public static UnitAI Resolve(Vector2 pointerPosition, Camera camera, UnitAI raycastUnit, float maxTouchRadiusPx)
        {
            // Vérité de terrain : ce raycast a physiquement touché le collider de cette unité. Rien
            // ne doit pouvoir le supplanter — voir la doc de la classe pour l'historique du bug que
            // cela corrige à la racine.
            if (raycastUnit != null) return raycastUnit;

            UnitAI closestUnit = null;
            float closestScreenDist = maxTouchRadiusPx;

            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI unit = UnitAI.AllLivingUnits[i];
                if (unit == null || !unit.isPlayerControlled || unit.isDead) continue;

                Vector3 screenPoint = camera.WorldToScreenPoint(unit.SelectionAnchorWorldPos);
                if (screenPoint.z <= 0) continue; // Derrière la caméra.

                float dist = Vector2.Distance(pointerPosition, new Vector2(screenPoint.x, screenPoint.y));
                if (dist < closestScreenDist)
                {
                    closestScreenDist = dist;
                    closestUnit = unit;
                }
            }

            return closestUnit;
        }
    }
}
