using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Fonction de résolution PURE : (état, ordres des deux équipes) -> (nouvel état, journal
    /// d'événements). Remplace la simulation Unity (NavMeshAgent + Physics.RaycastAll pendant
    /// plusieurs secondes) comme source du résultat OFFICIEL — le rendu Unity rejoue ensuite le
    /// journal d'événements pour le spectacle, sans jamais recalculer quoi que ce soit.
    ///
    /// Toutes les valeurs numériques sont extraites de l'audit du code solo (2026-08-30, voir
    /// TacticalTypes.cs pour la liste des fichiers sources) — rien n'est approximé ou improvisé.
    /// </summary>
    public static class TacticalResolver
    {
        public const float TickSeconds = 0.25f; // durée réelle simulée d'un pas — MatchSessionManager s'aligne dessus pour le rythme visuel client
        private const float MoveStepDistance = 1f; // mètres parcourus par tick, granularité du déplacement simulé

        public static List<TacticalEvent> Resolve(TacticalWorldState state, List<UnitOrders> ordersTeam1, List<UnitOrders> ordersTeam2, List<Vector2> mortarStrikes)
        {
            var events = new List<TacticalEvent>();

            // 1. Frappes de zone D'ABORD : la destruction doit être visible pour le déplacement/tir
            // qui suit dans la MÊME résolution (voir "Terrain Dynamique" dans l'architecture).
            if (mortarStrikes != null)
            {
                foreach (var strike in mortarStrikes) ApplyAreaDamage(state, strike, events);
            }

            var orderByUnitId = new Dictionary<string, UnitOrders>();
            foreach (var o in ordersTeam1) orderByUnitId[o.unitId] = o;
            foreach (var o in ordersTeam2) orderByUnitId[o.unitId] = o;

            var movers = state.units
                .Where(u => !u.isDead && orderByUnitId.ContainsKey(u.id) && orderByUnitId[u.id].path.Count > 0)
                .OrderBy(u => u.id, System.StringComparer.Ordinal) // ordre STABLE, jamais dépendant du hash/plateforme
                .ToList();

            var progress = new Dictionary<string, int>();
            foreach (var u in movers) progress[u.id] = 0;

            // 2. Boucle principale : déplacement pas à pas ET combat continu (cadence de tir
            // réelle, voir UnitAI_Combat.cs §1.1) tant que quelqu'un bouge encore OU qu'un
            // engagement reste possible — même structure en deux temps que l'ancien
            // ExecuterTourCoroutine (mouvement, puis fenêtre de combat courte), fusionnée ici en
            // une seule boucle puisqu'il n'y a plus de temps réel à attendre.
            int tick = 0;
            const int maxTicks = 800; // filet de sécurité (200s simulées) — jamais une boucle infinie
            bool anyActivity = true;
            while (anyActivity && tick < maxTicks)
            {
                tick++;
                anyActivity = false;

                foreach (var unit in movers)
                {
                    if (unit.isDead) continue;
                    var order = orderByUnitId[unit.id];
                    int idx = progress[unit.id];
                    if (idx >= order.path.Count) continue;

                    Vector2 target = order.path[idx];
                    Vector2 newPos = MoveTowards(unit.position, target, MoveStepDistance);
                    unit.position = newPos;
                    events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Move, unitId = unit.id, position = newPos, tick = tick });
                    anyActivity = true;

                    if (Vector2.Distance(newPos, target) < 0.01f)
                    {
                        progress[unit.id] = idx + 1;
                        if (progress[unit.id] >= order.path.Count) ApplyEndOfPathEffects(unit, order);
                    }

                    // Interruption Overwatch (NOUVELLE fonctionnalité, voir TacticalTypes.cs) : un
                    // ennemi qui surveille cette position tire AVANT que qui que ce soit d'autre
                    // ne bouge.
                    CheckOverwatchInterrupt(state, unit, events, tick);
                    if (unit.isDead) progress[unit.id] = order.path.Count;
                }

                // Combat continu (UnitAI_Combat.Update, cadence réelle par cooldown d'arme) : à
                // CHAQUE tick, toute unité vivante dont le cooldown est écoulé engage sa meilleure
                // cible visible. S'applique aux unités immobiles ET à celles en cours de
                // déplacement (une unité peut tirer tout en marchant, comme en solo).
                if (ResolveContinuousCombat(state, events, tick)) anyActivity = true;
            }

            return events;
        }

        private static Vector2 MoveTowards(Vector2 from, Vector2 to, float maxStep)
        {
            float sqrDist = GeometryMath.SqrDistance(from, to);
            if (sqrDist <= maxStep * maxStep) return to;
            float dist = Mathf.Sqrt(sqrDist);
            Vector2 dir = (to - from) / dist;
            return from + dir * maxStep;
        }

        /// <summary>Applique les postures/transitions arrivées en bout de chemin — miroir direct
        /// des NodeAction du rapport d'audit §2.6-2.10. isGuarding/isCamouflaged ne sont JAMAIS
        /// remis à false automatiquement ici : fidèle à l'original (rapport §8.2), ils persistent
        /// jusqu'à une prise de dégâts (camouflage) ou indéfiniment (guet — particularité connue
        /// de l'ancien code, reproduite telle quelle).</summary>
        private static void ApplyEndOfPathEffects(TacticalUnit unit, UnitOrders order)
        {
            if (order.setGuarding) unit.isGuarding = true;
            if (order.setCamouflaged) unit.isCamouflaged = true;

            if (order.setGarrisonWindow)
            {
                unit.isGarrisoned = true;
                unit.windowNormal = order.windowNormalToSet;
            }
            if (order.setGarrisonDoor)
            {
                unit.isGarrisoned = true;
                unit.windowNormal = null; // pas de restriction de cône pour une garnison de porte (rapport §2.9)
            }
            if (order.enterBuildingId >= 0)
            {
                unit.currentBuildingId = order.enterBuildingId;
            }
            if (order.exitBuilding)
            {
                // SortirBatiment appelle aussi LeaveGarrison() dans l'original (rapport §2.8) :
                // sortir annule la garnison même si elle ne venait pas d'une fenêtre de CE bâtiment.
                unit.currentBuildingId = -1;
                unit.isGarrisoned = false;
                unit.windowNormal = null;
            }
            if (order.setPositionY.HasValue)
            {
                unit.outputY = order.setPositionY;
                unit.zStrata = order.setPositionY.Value > 2.2f ? ZStrata.Toit : ZStrata.Sol; // seuil exact UnitAI.isRooftopSniper
            }
        }

        private static void CheckOverwatchInterrupt(TacticalWorldState state, TacticalUnit mover, List<TacticalEvent> events, int tick)
        {
            foreach (var watcher in state.units)
            {
                if (watcher.isDead || watcher.team == mover.team || watcher.watchTrigger == null) continue;
                if (!TriggerCrossed(watcher.watchTrigger, mover.position)) continue;
                if (!LineOfSight.HasLineOfSight(state, watcher.position, mover.position, watcher.visionType)) continue;

                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.OverwatchTriggered, unitId = watcher.id, targetUnitId = mover.id, position = mover.position, tick = tick });
                ApplyDamage(state, watcher, mover, events, tick);
                watcher.watchTrigger = null; // un seul tir d'interruption par Overwatch, par tour
            }
        }

        private static bool TriggerCrossed(OverwatchTrigger trigger, Vector2 point)
        {
            if (trigger.linePointA.HasValue && trigger.linePointB.HasValue)
            {
                return DistancePointToSegment(point, trigger.linePointA.Value, trigger.linePointB.Value) < 1f;
            }
            return GeometryMath.InsideCone(trigger.origin, trigger.facing, trigger.cosHalfAngle, trigger.range, point);
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float sqrLen = ab.x * ab.x + ab.y * ab.y;
            if (sqrLen < 0.0001f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(((p.x - a.x) * ab.x + (p.y - a.y) * ab.y) / sqrLen);
            Vector2 proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        /// <summary>UnitAI_Combat.Update (§1.1) : chaque unité vivante non-mortier dont le cooldown
        /// est écoulé engage la cible la plus proche visible (GetVisibleEnemy) et lui tire dessus,
        /// puis relance son cooldown. Renvoie vrai si au moins un tir a eu lieu (pour garder la
        /// boucle principale active tant que des échanges de tirs sont encore possibles, comme
        /// l'ancienne "fenêtre de combat" de 2s en fin de mouvement).</summary>
        private static bool ResolveContinuousCombat(TacticalWorldState state, List<TacticalEvent> events, int tick)
        {
            bool anyShot = false;
            var shooters = state.units.Where(u => !u.isDead && !u.isMortar).OrderBy(u => u.id, System.StringComparer.Ordinal).ToList();

            foreach (var shooter in shooters)
            {
                shooter.cooldownRemaining -= TickSeconds;
                if (shooter.cooldownRemaining > 0f) continue;
                if (shooter.weaponCooldownSeconds <= 0f) continue; // pas d'arme de tir direct (ex: mortier)

                TacticalUnit target = FindClosestEngageableTarget(state, shooter);
                if (target == null) continue;

                ApplyDamage(state, shooter, target, events, tick);
                shooter.cooldownRemaining = shooter.weaponCooldownSeconds;
                anyShot = true;
            }
            return anyShot;
        }

        /// <summary>GetVisibleEnemy (UnitAI_Combat.cs:242-337) : la cible ENNEMIE VIVANTE NON
        /// CAMOUFLÉE la plus proche dans la portée d'engagement, avec ligne de vue — "le plus
        /// proche qui passe le test", pas "le meilleur tactiquement", fidèle à l'original.</summary>
        private static TacticalUnit FindClosestEngageableTarget(TacticalWorldState state, TacticalUnit shooter)
        {
            TacticalUnit best = null;
            float bestSqrDist = float.MaxValue;
            foreach (var target in state.units)
            {
                if (target.isDead || target.team == shooter.team || target.isCamouflaged) continue;
                float sqrDist = GeometryMath.SqrDistance(shooter.position, target.position);
                if (sqrDist >= bestSqrDist) continue;
                if (!LineOfSight.CanEngageTarget(state, shooter, target)) continue;
                best = target;
                bestSqrDist = sqrDist;
            }
            return best;
        }

        // Distance de couverture d'une barricade encore debout — RoadBarrier.cs:193-207 (2.2m exact).
        private const float BarricadeCoverDistance = 2.2f;

        /// <summary>Priorité stricte garnison > barricade > guet (UnitAI.cs:665-682, if/else if —
        /// jamais cumulée). Renvoie le multiplicateur à appliquer aux dégâts BRUTS.</summary>
        private static float ComputeCoverMultiplier(TacticalWorldState state, TacticalUnit target)
        {
            if (target.isGarrisoned) return 0.25f; // -75%
            foreach (var barricade in state.barricades)
            {
                if (!barricade.BlocksSight) continue;
                if (DistancePointToSegment(target.position, barricade.p1, barricade.p2) <= BarricadeCoverDistance) return 0.4f; // -60%
            }
            if (target.isGuarding) return 0.5f; // -50%
            return 1f;
        }

        /// <summary>UnitAI.TakeDamage (UnitAI.cs:665-725), ordre exact : bonus d'embuscade du
        /// TIREUR camouflé (x1.75, casse son camouflage), PUIS réduction de couverture de la
        /// CIBLE, PUIS plancher de 1 dégât minimum, PUIS troncature en entier (pas d'arrondi —
        /// health -= (int)amount dans l'original). Le camouflage de la CIBLE casse
        /// INCONDITIONNELLEMENT dès qu'elle encaisse un dégât, qu'il soit réduit ou non.</summary>
        private static void ApplyDamage(TacticalWorldState state, TacticalUnit shooter, TacticalUnit target, List<TacticalEvent> events, int tick)
        {
            float rawDamage = shooter.weaponDamage;

            if (shooter.isCamouflaged)
            {
                rawDamage *= 1.75f; // UnitAI_Combat.cs:411-416 — bonus d'embuscade, un seul coup
                shooter.isCamouflaged = false;
            }

            float coverMultiplier = ComputeCoverMultiplier(state, target);
            float finalDamage = Mathf.Max(1f, rawDamage * coverMultiplier);
            int damage = (int)finalDamage; // troncature, pas Mathf.Round — fidèle à l'original

            target.health -= damage;
            target.isCamouflaged = false; // UnitAI.cs:685 — casse toujours, même si le tireur n'était pas camouflé

            events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Shot, unitId = shooter.id, targetUnitId = target.id, position = target.position, damage = damage, tick = tick });

            if (target.health <= 0 && !target.isDead)
            {
                target.isDead = true;
                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Death, unitId = target.id, position = target.position, tick = tick });
            }
        }

        // MortarShell.cs:17-18 — constantes exactes (explosionRadius, maxDamage).
        private const float MortarExplosionRadius = 6.5f;
        private const float MortarMaxDamage = 150f;

        /// <summary>MortarShell.ApplySplashDamage (§6 du rapport). Trois cibles distinctes, trois
        /// formules distinctes — ne jamais les uniformiser :
        ///   - Unités : dégâts Lerp(30,150,%) SI ligne de vue dégagée (bloqué en tout-ou-rien par
        ///     un mur/une barricade, pas de réduction partielle).
        ///   - Bâtiments (TacticalBuilding, PV partagé) : dégâts Lerp(100,375,%) (MortarShell.cs,
        ///     maxDamage*2.5 = 375) et AUCUN test de mur (l'environnement encaisse même à travers
        ///     un autre bâtiment).
        ///   - Barricades : formule LINÉAIRE SANS PLANCHER, dmg = 150 * (1 - dist/rayon) — pas un
        ///     Lerp(30,...), une vraie différence de formule avec les unités (RoadBarrier.cs:212-228).
        /// </summary>
        private static void ApplyAreaDamage(TacticalWorldState state, Vector2 impact, List<TacticalEvent> events)
        {
            // --- Unités ---
            foreach (var unit in state.units)
            {
                if (unit.isDead) continue;
                float dist = Vector2.Distance(unit.position, impact);
                if (dist > MortarExplosionRadius) continue;
                if (!LineOfSight.HasLineOfSight(state, impact, unit.position, VisionType.Normale)) continue; // bloqué en tout-ou-rien

                float damagePercent = 1f - (dist / MortarExplosionRadius);
                int damage = Mathf.RoundToInt(Mathf.Lerp(30f, MortarMaxDamage, damagePercent));
                ApplyDamage(state, new TacticalUnit { id = "mortar", weaponDamage = damage }, unit, events, 0);
            }

            // --- Bâtiments (pool de PV partagé, aucun test de mur — rapport §6/§7) ---
            foreach (var building in state.buildings)
            {
                if (building.destroyed) continue;

                float closestDist = float.MaxValue;
                foreach (var wall in state.wallSegments)
                {
                    if (wall.buildingId != building.id) continue;
                    closestDist = Mathf.Min(closestDist, DistancePointToSegment(impact, wall.p1, wall.p2));
                }
                if (closestDist > MortarExplosionRadius) continue;

                float damagePercent = Mathf.Clamp01(1f - (closestDist / MortarExplosionRadius));
                float damage = Mathf.Lerp(100f, MortarMaxDamage * 2.5f, damagePercent); // 100 à 375
                building.health -= damage;

                if (building.health <= 0f && !building.destroyed)
                {
                    DestroyBuilding(state, building, events);
                }
            }

            // --- Barricades (formule linéaire sans plancher, RoadBarrier.cs:212-228) ---
            foreach (var barricade in state.barricades)
            {
                if (barricade.Destroyed) continue;
                float dist = DistancePointToSegment(impact, barricade.p1, barricade.p2);
                if (dist > MortarExplosionRadius) continue;
                float factor = 1f - Mathf.Clamp01(dist / MortarExplosionRadius);
                barricade.hp -= MortarMaxDamage * factor;
            }
        }

        /// <summary>DestructibleEnvironment.DestroyEnvironment (§7) : tous les murs de ce bâtiment
        /// cessent de bloquer EN MÊME TEMPS (pas de destruction progressive mur par mur), et toute
        /// unité dont la position tombe dans l'empreinte au sol (marge 1m) est écrasée (9999
        /// dégâts — mort certaine, même à travers une couverture).</summary>
        private static void DestroyBuilding(TacticalWorldState state, TacticalBuilding building, List<TacticalEvent> events)
        {
            building.destroyed = true;
            events.Add(new TacticalEvent { kind = TacticalEvent.Kind.WallDestroyed, buildingId = building.id, tick = 0 });

            if (building.footprint == null) return;
            state.grid?.OpenBuildingInterior(building.footprint);
            foreach (var unit in state.units)
            {
                if (unit.isDead) continue;
                bool inside = unit.currentBuildingId == building.id || IsInsideWithMargin(building.footprint, unit.position, 1f);
                if (!inside) continue;

                unit.health = -9999;
                unit.isDead = true;
                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Death, unitId = unit.id, position = unit.position, tick = 0 });
            }
        }

        private static bool IsInsideWithMargin(List<Vector2> footprint, Vector2 point, float margin)
        {
            if (GeometryMath.PointInPolygon(footprint, point)) return true;
            if (margin <= 0f) return false;
            // Approximation suffisante de la marge : teste aussi les 4 points décalés — évite un
            // vrai offset de polygone (coûteux et inutile ici, l'empreinte est petite face à 1m).
            return GeometryMath.PointInPolygon(footprint, point + new Vector2(margin, 0)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(-margin, 0)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(0, margin)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(0, -margin));
        }
    }
}
