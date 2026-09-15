using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Résolution de tour par le VRAI moteur Unity (NavMeshAgent + Physics.RaycastAll temps réel via
    /// UnitAI.ExecuterOrdres()/UnitAI_Combat), demandée explicitement par l'utilisateur le 2026-09-13
    /// pour remplacer TacticalResolver.Resolve() (voir
    /// 09-real-unity-combat-investigation-2026-09-13.md et 11-real-engine-switch-2026-09-13.md pour
    /// tout le contexte et les risques déjà discutés — non-déterminisme PhysX/NavMesh inter-appareils,
    /// désormais accepté en connaissance de cause).
    ///
    /// Reprend EXACTEMENT le même déroulé que TacticalPathManager.LancerExecutionTour/
    /// ExecuterTourCoroutine (Solo, jamais touché) — c'est littéralement le même code de simulation,
    /// simplement piloté ici par le serveur au lieu d'un bouton "FIN DE TOUR" côté client, et
    /// échantillonné en Snapshots réseau au lieu d'être rendu directement à l'écran.
    /// </summary>
    public partial class MatchSessionManager
    {
        private const float RealEngineBaseMovementAllowance = 45.0f;
        private const float RealEngineTacticalWaitSeconds = 31.0f;
        private const float RealEngineAbsoluteCapSeconds = 110.0f;
        private const float RealEngineCombatGraceSeconds = 2.0f;

        /// <summary>Même formule que TacticalPathManager.ComputeExecutionSafetyCap (voir ce fichier
        /// pour le détail commenté), dupliquée ici plutôt que rendue publique côté client pour ne
        /// jamais risquer de changer le comportement Solo par un couplage accidentel.</summary>
        private static float ComputeRealEngineSafetyCap(List<UnitAI> units)
        {
            int maxWaitNodes = 0;
            foreach (var u in units)
            {
                if (u == null || u.isDead || u.tacticalPath == null) continue;
                int waitNodes = 0;
                for (int n = 0; n < u.tacticalPath.Count; n++)
                {
                    if (u.tacticalPath[n].action == TacticalPathManager.NodeAction.Attendre30s) waitNodes++;
                }
                if (waitNodes > maxWaitNodes) maxWaitNodes = waitNodes;
            }
            return Mathf.Min(RealEngineBaseMovementAllowance + RealEngineTacticalWaitSeconds * maxWaitNodes, RealEngineAbsoluteCapSeconds);
        }

        private static bool AnyUnitMovingOrActing(List<UnitAI> units)
        {
            foreach (var u in units)
            {
                if (u != null && !u.isDead && u.IsMovingOrActing()) return true;
            }
            return false;
        }

        private static bool AnyUnitInActiveCombat(List<UnitAI> units)
        {
            foreach (var u in units)
            {
                if (u != null && !u.isDead && u.HasActiveTargetInRange()) return true;
            }
            return false;
        }

        /// <summary>Équivalent "vrai moteur" de RunExecutionPhase (MatchSessionManager_CombatLive.cs,
        /// laissée intacte, plus appelée par RunMatchLive depuis ce changement). Ne calcule PLUS rien
        /// via TacticalResolver.Resolve() — lance la VRAIE exécution (UnitAI.ExecuterOrdres(), NavMesh
        /// + tir temps réel) et échantillonne l'état réel des UnitAI à intervalle régulier, exactement
        /// comme le fait déjà le Solo (TacticalPathManager_Execution.cs), jamais touché ici.
        ///
        /// IMPORTANT : appelant responsable de s'assurer qu'AUCUNE UnitAI des deux camps n'a
        /// isPlayerControlled=false avant cet appel (voir RunMatchLive) — ExecuterOrdres() ne planifie
        /// lui-même RIEN, mais une unité restée isPlayerControlled=false ailleurs dans le pipeline
        /// pourrait rester intégralement passive (ni ordre humain ni IA), un silence différent du
        /// "tenir la position en se défendant" voulu pour un joueur ghosté.
        ///
        /// p1 PEUT être null depuis 2026-09-13 (résolution HEADLESS d'un siège de Zone, voir
        /// MatchSessionManager_Siege.ResolveSiegeNow — ni l'attaquant ni le défenseur n'ont de
        /// connexion live pendant la résolution, la simulation elle-même n'en a jamais eu besoin,
        /// seul l'envoi des snapshots en fin de tour en dépendait).</summary>
        private IEnumerator RunExecutionPhaseRealEngine(int turnNumber, PlayerConnection p1, PlayerConnection p2)
        {
            DateTime executionStartUtc = DateTime.UtcNow;

            var allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude).Where(u => !u.isDead).ToList();
            var unitById = allUnits.ToDictionary(u => u.gameObject.name);

            // Photo de la santé de chaque bâtiment AVANT ce tour, pour détecter les destructions
            // tick par tick (les dégâts sont appliqués en temps réel par le vrai moteur — mortiers/
            // tirs de char touchent directement DestructibleEnvironment.TakeDamage, comme en Solo —
            // on ne fait ici qu'OBSERVER, jamais recalculer de dégâts nous-mêmes).
            var buildingsSnapshot = new List<BuildingStructure>(BuildingStructure.AllBuildings);
            var wasDestroyed = new bool[buildingsSnapshot.Count];
            for (int i = 0; i < buildingsSnapshot.Count; i++)
            {
                var env = buildingsSnapshot[i] != null ? buildingsSnapshot[i].GetComponent<DestructibleEnvironment>() : null;
                wasDestroyed[i] = env != null && env.isDestroyed;
            }

            // Lance RÉELLEMENT l'exécution (NavMeshAgent.SetDestination, coroutines de checkpoint,
            // UnitAI_Combat.Update() qui scanne/tire en temps réel) — identique à ce que fait
            // TacticalPathManager.LancerExecutionTour ligne 131 pour le Solo.
            foreach (var unit in allUnits) unit.ExecuterOrdres();

            var snapshots = new List<Snapshot>();
            float tickIntervalSec = TickDurationMs / 1000f;
            float safetyCap = ComputeRealEngineSafetyCap(allUnits);
            float elapsed = 0f;
            int tickIndex = 0;

            while (elapsed < safetyCap && AnyUnitMovingOrActing(allUnits))
            {
                yield return new WaitForSeconds(tickIntervalSec);
                elapsed += tickIntervalSec;
                if (currentMatchMode == "zone_control" && CaptureZone.Instance != null) CaptureZone.Instance.Tick();
                snapshots.Add(CaptureRealEngineSnapshot(tickIndex++, allUnits, buildingsSnapshot, wasDestroyed));
            }

            float combatGrace = 0f;
            while (combatGrace < RealEngineCombatGraceSeconds && AnyUnitInActiveCombat(allUnits))
            {
                yield return new WaitForSeconds(tickIntervalSec);
                combatGrace += tickIntervalSec;
                if (currentMatchMode == "zone_control" && CaptureZone.Instance != null) CaptureZone.Instance.Tick();
                snapshots.Add(CaptureRealEngineSnapshot(tickIndex++, allUnits, buildingsSnapshot, wasDestroyed));
            }

            // Toujours au moins UN tick — sinon un tour où rien ne bouge/ne tire (ex : les deux camps
            // ATTENDENT) enverrait un turn_result sans le moindre Snapshot, que le client ne saurait
            // pas rejouer.
            if (snapshots.Count == 0)
            {
                if (currentMatchMode == "zone_control" && CaptureZone.Instance != null) CaptureZone.Instance.Tick();
                snapshots.Add(CaptureRealEngineSnapshot(0, allUnits, buildingsSnapshot, wasDestroyed));
            }

            foreach (var unit in allUnits)
            {
                if (unit == null) continue;
                unit.StopAllCoroutines();
                unit.ResetOrderState();
            }

            Snapshot[] snapshotsForTeam1 = FilterRealEngineSnapshotsForTeam(snapshots, unitById, 1);
            Snapshot[] snapshotsForTeam2 = FilterRealEngineSnapshotsForTeam(snapshots, unitById, 2);

            if (p1 != null) p1.HasAckedTurnResult = false;
            if (p2 != null) p2.HasAckedTurnResult = false;

            if (p1 != null && !p1.IsDisconnected)
            {
                p1.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam1 });
            }
            if (p2 != null && !p2.IsDisconnected)
            {
                p2.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam2 });
            }

            // BARRIÈRE DE DÉPART SYNCHRONE (2026-09-16, rapport utilisateur : "pas de mouvement
            // simultané et synchro entre les unités alliées, ni entre alliées et ennemies"). Avant
            // ceci, chaque client démarrait PlaySnapshotsCoroutine dès la réception de SON PROPRE
            // "turn_result" — or les deux payloads n'ont ni la même taille (snapshotsForTeam1/2 sont
            // filtrés différemment par le brouillard de guerre, voir FilterRealEngineSnapshotsForTeam)
            // ni le même ordre d'envoi (p1.Send() puis p2.Send() ci-dessus), donc les deux clients ne
            // recevaient JAMAIS leur payload au même instant — chacun rejouait bien ses propres
            // unités ET celles de l'adversaire en parfait synchronisme LOCAL (une seule boucle
            // d'interpolation partagée, voir PlaySnapshotsBody côté client), mais les DEUX ÉCRANS
            // étaient décalés l'un par rapport à l'autre, ce qui se lit comme "rien n'est synchro"
            // pour deux joueurs qui comparent leurs appareils côte à côte.
            //
            // Correctif : chaque client, en recevant "turn_result", le met en cache et renvoie
            // immédiatement "turn_result_ack" SANS démarrer sa lecture (voir
            // MultiplayerMatchController.OnTurnResultReceived) ; le serveur attend ici les deux accusés
            // de réception (borné, pour ne jamais bloquer indéfiniment un joueur dont l'adversaire a
            // décroché) puis envoie "turn_playback_start" aux deux dans la foulée — c'est CE signal,
            // minuscule et découplé du gros payload de simulation, qui déclenche réellement
            // PlaySnapshotsCoroutine des deux côtés.
            const float MaxPlaybackAckWaitSeconds = 3.0f;
            float ackWait = 0f;
            while (ackWait < MaxPlaybackAckWaitSeconds)
            {
                bool p1Ready = p1 == null || p1.IsDisconnected || p1.HasAckedTurnResult;
                bool p2Ready = p2 == null || p2.IsDisconnected || p2.HasAckedTurnResult;
                if (p1Ready && p2Ready) break;

                if (p1 != null) DrainMessages(p1, turnNumber);
                if (p2 != null) DrainMessages(p2, turnNumber);

                yield return null;
                ackWait += Time.deltaTime;
            }

            if (p1 != null && !p1.IsDisconnected) p1.Send(new NetMessage { type = "turn_playback_start", turn_number = turnNumber });
            if (p2 != null && !p2.IsDisconnected) p2.Send(new NetMessage { type = "turn_playback_start", turn_number = turnNumber });

            double totalMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] Tour {turnNumber} (VRAI MOTEUR) : {snapshots.Count} tick(s) réels, exécution+envoi en {totalMs:F1}ms de temps SERVEUR (le temps RÉEL de résolution était ~{elapsed + combatGrace:F1}s, contre quelques ms pour TacticalResolver.Resolve() — voir 11-real-engine-switch-2026-09-13.md).");
        }

        /// <summary>Un seul tick d'échantillonnage — lit l'état RÉEL de chaque UnitAI à cet instant.
        /// "shooting"/"shoot_target_id" sont une APPROXIMATION assumée (voir doc) : UnitAI_Combat
        /// n'expose aucun champ public "cible actuellement engagée", donc on réutilise
        /// UnitAI.GetVisibleEnemy() (déjà utilisé pour la détection de cible) comme proxy — un ennemi
        /// visible en portée à cet instant, jamais un état d'animation garanti frame-exact. Purement
        /// cosmétique côté client (tracé de tir), sans effet sur les PV/positions/morts, qui restent
        /// la vérité intégrale du vrai moteur.</summary>
        private Snapshot CaptureRealEngineSnapshot(int t, List<UnitAI> units, List<BuildingStructure> buildingsSnapshot, bool[] wasDestroyed)
        {
            var pos = new Dictionary<string, Vector2>();
            var rotation = new Dictionary<string, float>();
            var health = new Dictionary<string, int>();
            var dead = new Dictionary<string, bool>();
            var shooting = new Dictionary<string, bool>();
            var shootTarget = new Dictionary<string, string>();
            var yByUnit = new Dictionary<string, float>();

            foreach (var u in units)
            {
                if (u == null) continue;
                string id = u.gameObject.name;
                pos[id] = new Vector2(u.transform.position.x, u.transform.position.z);
                yByUnit[id] = u.transform.position.y;
                rotation[id] = u.transform.eulerAngles.y;
                health[id] = u.health;
                dead[id] = u.isDead;

                UnitAI visibleEnemy = !u.isDead ? u.GetVisibleEnemy() : null;
                shooting[id] = visibleEnemy != null;
                shootTarget[id] = visibleEnemy != null ? visibleEnemy.gameObject.name : null;
            }

            int[] destroyedThisTick = Array.Empty<int>();
            var destroyedList = new List<int>();
            for (int i = 0; i < buildingsSnapshot.Count; i++)
            {
                if (wasDestroyed[i]) continue;
                var env = buildingsSnapshot[i] != null ? buildingsSnapshot[i].GetComponent<DestructibleEnvironment>() : null;
                if (env != null && env.isDestroyed)
                {
                    wasDestroyed[i] = true;
                    destroyedList.Add(i);
                }
            }
            if (destroyedList.Count > 0) destroyedThisTick = destroyedList.ToArray();

            var livingUnitsOnly = units.Where(u => u != null).ToList();
            Snapshot snap = CaptureTacticalSnapshot(t, pos, rotation, health, dead, shooting, shootTarget, livingUnitsOnly, yByUnit, destroyedThisTick);
            return snap;
        }

        /// <summary>Équivalent "vrai moteur" de FilterSnapshotsForTeam (MatchSessionManager_CombatLive.cs)
        /// — utilise UnitAI.IsUnitSpottedByTeam (le VRAI calcul, Physics.RaycastAll inclus, déjà
        /// utilisé par TacticalAIPlanner en Solo) au lieu de ComputeVisibleUnitIds (qui lit
        /// TacticalWorldState, absent de ce chemin). Un adversaire MORT reste toujours visible (même
        /// exception que ComputeVisibleUnitIds, voir son commentaire "Un adversaire mort reste
        /// visible") : IsUnitSpottedByTeam renvoie FAUX pour une cible déjà morte, ce n'est donc pas
        /// un simple appel direct — il faut le court-circuiter explicitement pour les morts.</summary>
        private static Snapshot[] FilterRealEngineSnapshotsForTeam(List<Snapshot> fullSnapshots, Dictionary<string, UnitAI> unitById, int observingTeam)
        {
            var result = new Snapshot[fullSnapshots.Count];
            for (int s = 0; s < fullSnapshots.Count; s++)
            {
                var src = fullSnapshots[s];
                var visibleUnits = new List<UnitState>();
                foreach (var us in src.units)
                {
                    if (us.team_id == observingTeam || us.dead)
                    {
                        visibleUnits.Add(us);
                        continue;
                    }
                    if (unitById.TryGetValue(us.unit_id, out UnitAI realUnit) && UnitAI.IsUnitSpottedByTeam(realUnit, observingTeam))
                    {
                        visibleUnits.Add(us);
                    }
                }
                result[s] = new Snapshot
                {
                    t = src.t,
                    units = visibleUnits.ToArray(),
                    zone_progress_team1 = src.zone_progress_team1,
                    zone_progress_team2 = src.zone_progress_team2,
                    destroyed_building_ids = src.destroyed_building_ids
                };
            }
            return result;
        }
    }
}
