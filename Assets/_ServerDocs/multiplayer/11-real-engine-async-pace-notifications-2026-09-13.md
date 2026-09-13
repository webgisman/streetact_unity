# Vrai moteur pour tous les modes + rythmes 5 min/6h + notifications Supabase (2026-09-13)

**Statut : implémenté, compile (build serveur Linux headless relancé 4 fois au total dans cette
session, sans erreur à chaque fois). Pas testé en conditions réelles (pas de 2ème client disponible
dans cette session, et la base de données de test n'a pas la nouvelle migration appliquée). À tester
au retour de l'utilisateur.**

Ce document complète [10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md](10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md)
— demande explicite de l'utilisateur, reçue après correction sur ce que "vrai moteur Unity" veut dire
concrètement dans ce projet (voir [09-real-unity-combat-investigation-2026-09-13.md](09-real-unity-combat-investigation-2026-09-13.md)) :
1. Le vrai moteur physique Unity (`NavMeshAgent`/`Physics.RaycastAll` temps réel) pour TOUS les modes,
   pas seulement Deathmatch/Zone de Contrôle.
2. Notifications via Supabase (pas Brevo, explicitement écarté).
3. Deux rythmes de jeu sélectionnables : "fast" (5 min max/tour, déjà existant) et "async" (6h
   max/tour, nouveau).

## 1. Vrai moteur pour tous les modes

**Nouveau fichier** : `Assets/Scripts/Server/MatchSessionManager_CombatRealEngine.cs` —
`RunExecutionPhaseRealEngine` remplace **partout** `RunExecutionPhase` (qui appelait
`TacticalResolver.Resolve()`, la même fonction pure/déterministe qu'avant) :
- Deathmatch/Zone de Contrôle (`RunMatchLive`)
- Conquête (`RunConquestSkirmish`, `MatchSessionManager_Conquest.cs`)
- Entraînement contre l'IA (`practice_ai`, même fichier)

Ce n'est plus un calcul instantané qui renvoie un résultat à rejouer — c'est littéralement le même
code que le Solo (`TacticalPathManager_Execution.LancerExecutionTour`/`UnitAI.ExecuterOrdres()`,
jamais touchés) : NavMeshAgent fait vraiment avancer les unités, `UnitAI_Combat.Update()` scanne et
tire en temps réel via `Physics.RaycastAll`, pendant un vrai laps de temps serveur (jusqu'à ~110s par
tour, voir `ComputeRealEngineSafetyCap`), échantillonné en Snapshots au même rythme que le client
attend déjà (`TickDurationMs`).

**Brouillard de guerre reconstruit sur la vraie primitive** : `UnitAI.IsUnitSpottedByTeam` (déjà
utilisée par `TacticalAIPlanner` en Solo) remplace `ComputeVisibleUnitIds` — avec la même exception
qu'avant ("un ennemi mort reste visible", `IsUnitSpottedByTeam` renvoie sinon toujours faux pour une
cible morte).

**Correctif fait en cours de route** : `UnitSpawnerUI.SpawnUnitAt` ne marquait `isPlayerControlled`
que pour l'équipe 1 côté serveur (hérité de la Conquête, où l'équipe 2 est toujours une garnison IA).
Pour Deathmatch/Zone de Contrôle, les deux équipes sont maintenant forcées à `isPlayerControlled =
true` juste après le déploiement — sinon `ExecuterOrdres()` sur l'équipe 2 aurait pu emprunter des
branches de code pensées pour un ennemi IA, jamais voulues ici.

**Simplification assumée** : `shooting`/`shoot_target_id` dans chaque Snapshot (purement cosmétique
côté client, pour le tracé de tir) sont approximés via `UnitAI.GetVisibleEnemy()` — "un ennemi visible
à portée à cet instant" — faute d'un champ public exposant l'état d'animation de tir exact de
`UnitAI_Combat`. Sans effet sur PV/positions/morts, qui restent la vérité intégrale du vrai moteur.

**Ce que ça coûte, redit clairement** : `TacticalResolver.Resolve()`/tout le reste de
`Assets/Scripts/TacticalCore/` restent intacts mais ne sont plus appelés par AUCUN chemin serveur —
le non-déterminisme PhysX/NavMesh inter-appareils (voir 09-...) et la contrainte "un match à la fois
par processus" (voir 10-...) s'appliquent maintenant à TOUS les modes, pas seulement Deathmatch/Zone
de Contrôle.

## 2. Rythmes "fast" (5 min) et "async" (6h)

**Nouveau champ réseau** : `NetMessage.turn_pace` ("fast" par défaut, ou "async"), envoyé dans
`join_matchmaking`. Deux joueurs ne sont appariés que s'ils veulent le MÊME mode ET le MÊME rythme —
voir les 2 nouvelles files d'attente (`waitingDeathmatchAsync`/`waitingZoneControlAsync`) dans
`MatchSessionManager.cs`.

**Le vrai problème que ce découpage résout** : depuis le passage au vrai moteur, `matchInProgress`
est un verrou GLOBAL tenu pour toute la durée d'un match (voir 10-...). Un tour "fast" de 5 minutes
rend ça un délai d'attente acceptable pour les autres matchs en file. Un tour de 6h ne le serait PAS —
un seul match async bloquerait TOUS les autres (tous modes/rythmes confondus) pendant des heures si
rien de spécial n'était fait. D'où :

**`MatchSessionManager_AsyncPause.cs`** (nouveau) — un match async NE GARDE JAMAIS la scène/le verrou
entre deux tours :
1. Après chaque tour (y compris le tout premier, juste après le déploiement), l'effectif réel
   (`UnitAI.AllLivingUnits`) est sérialisé — position/rotation/PV/type/équipe — dans une nouvelle
   colonne `matches.paused_roster_json`, les GameObjects sont détruits
   (`UnitSpawnerUI.ClearAllUnits()`), et `matchInProgress` repasse à `false`.
2. L'attente des deux soumissions (`WaitForBothOrdersAsync`, jusqu'à 6h) ne touche plus du tout à la
   scène — juste `DrainMessages` sur les deux `PlayerConnection`, exactement comme avant mais avec un
   délai de 21600s au lieu de 300s. Pendant ce temps, n'importe quel autre match (fast, async, ou une
   Conquête) peut utiliser librement le processus.
3. Une fois les deux ordres reçus (ou le délai écoulé) : reprise du verrou (attend si occupé par un
   autre match à cet instant), rechargement de la carte, **respawn** de l'effectif sauvegardé avec
   EXACTEMENT les mêmes noms de GameObject qu'avant la pause (indispensable : les ordres soumis
   pendant l'attente référencent les unités par ce nom — `NetMessage.UnitOrder.unit_id` — un nom
   différent au respawn les aurait rendus silencieusement inapplicables), puis application des ordres
   normalement.

**MISE À JOUR (même session, suite à demande explicite "c'est critique")** : garnison de
fenêtre/intérieur de bâtiment/guet/camouflage/perché sur toit survivent maintenant à une pause —
`PausedUnitDto` porte `is_garrisoned`/`garrison_building_id`/`garrison_window_id`/
`current_building_id`/`is_guarding`/`is_camouflaged`/`is_rooftop_sniper`, identifiés par INDEX dans
`BuildingStructure.AllBuildings`/`BuildingWindow.id` (les seules références stables qui survivent à
la destruction des GameObjects), reconstruits après le rechargement de la carte au réveil. La
progression de Zone de Contrôle survit aussi (`CaptureZone.RestoreProgress`, nouveau setter — les
champs avaient un setter privé). Voir `MatchSessionManager_AsyncPause.cs`.

**Simplifications restantes, assumées** :
- Le chemin tactique en cours n'est pas persisté — sans conséquence, il est déjà vide au moment
  précis où une pause a lieu (juste après `ResetOrderState()` en fin d'exécution).
- Les cooldowns d'arme repartent à zéro après une reprise — avantage mineur et temporaire pour
  l'unité concernée, jugé hors de portée.
- Le déploiement initial garde son délai de 5 minutes (`DeploymentSeconds`) même en rythme async — pas
  de rythme séparé pour cette phase, jugé hors de portée de la demande initiale.
- La restauration bâtiment/fenêtre suppose que `BuildingStructure.AllBuildings` se reconstruit dans
  le MÊME ordre après rechargement de la carte (génération déterministe à partir des mêmes données —
  déjà l'hypothèse retenue ailleurs pour `city_verify`). Si jamais un bâtiment/une fenêtre reste
  introuvable au réveil (ne devrait structurellement pas arriver), repli sûr : l'unité perd sa
  garnison plutôt que de pointer vers une référence incohérente (avertissement journalisé).

**Migration base de données requise** (pas encore appliquée sur le VPS réel — voir §3) :
`alter table public.matches add column if not exists paused_roster_json jsonb;` (dans `schema.sql`
§8, à appliquer manuellement via `psql` sur toute base déjà déployée, même pattern que les
migrations passées listées en §8bis de `01-deployment-vps.md`).

## 3. Notifications via Supabase (pas Brevo)

Nouvelle table `public.notifications` (schema.sql §9) — RLS activée, un joueur ne voit que les
siennes, ne peut marquer que `read_at` (jamais écrire son propre contenu). Le serveur de jeu écrit une
ligne (`WriteNotification`, `MatchSessionManager_AsyncPause.cs`) dès qu'un joueur en rythme async n'a
pas encore soumis alors que son adversaire l'a déjà fait — "C'est ton tour".

**Ce qui N'EST PAS fait** : aucun code client ne lit ni n'affiche ces notifications — ce commit ne
couvre que l'écriture serveur + le schéma + les policies RLS. Il faudrait, côté client, une requête
PostgREST (`GET /notifications?user_id=eq.<self>&read_at=is.null`) à l'ouverture de l'application (ou
au premier plan) et un affichage (badge/toast/écran dédié) — pas construit dans cette session.

**Aucun envoi d'email/push réel** : "notification Supabase" ici veut dire une ligne dans Postgres,
lisible par le client — pas un email. GoTrue (le service d'auth déjà en place) ne propose pas
d'endpoint pour envoyer un email arbitraire, seulement ses propres emails d'authentification
(confirmation, lien magique, reset) — ce n'est donc pas une option pour une notification de jeu
générique, d'où ce choix de simple table.

## Comment tester au retour

1. **Appliquer la migration** sur la base de test/VPS (`schema.sql` §8 et §9 — colonne
   `paused_roster_json` + table `notifications`), sinon `PersistPausedRoster`/`WriteNotification`
   échoueront silencieusement (juste un `Debug.LogWarning`, le match continue mais rien n'est
   sauvegardé/notifié).
2. Rebuild le serveur (déjà fait avec succès dans cette session, voir `build_server_2026-09-13d.log`
   à la racine du projet).
3. Deux clients rejoignent en rythme "fast" (comportement quasi inchangé, à part le vrai moteur —
   vérifier qu'un tour se déroule bien en temps réel côté serveur, pas instantanément).
4. Deux clients rejoignent en rythme "async" : soumettre un tour avec un seul joueur, vérifier
   qu'une ligne apparaît dans `notifications` pour l'autre, attendre un peu, soumettre le second
   ordre, vérifier que le tour se résout bien et que les DEUX joueurs revoient leurs unités aux bonnes
   positions/PV après reprise (c'est le point le plus fragile de ce chantier — jamais exécuté
   réellement).
5. Pendant qu'un match async est "en pause" (entre deux tours), vérifier qu'un AUTRE match (fast ou
   async) peut bien démarrer et tourner sur le même processus — c'est tout l'intérêt du système.

## Comment revenir en arrière

Chaque couche est indépendante et réversible séparément :
- Revenir au résolveur pur pour tous les modes : remplacer les 3 appels à
  `RunExecutionPhaseRealEngine` par `RunExecutionPhase` (`MatchSessionManager_MatchLive.cs`,
  `MatchSessionManager_Conquest.cs` ×2).
- Désactiver le rythme async : ignorer `NetMessage.turn_pace` côté serveur (le forcer à "fast"), ou
  simplement ne jamais router de joueurs vers les files `*Async`.
- Notifications : ne pas appeler `WriteNotification` — la table reste inoffensive si vide.
