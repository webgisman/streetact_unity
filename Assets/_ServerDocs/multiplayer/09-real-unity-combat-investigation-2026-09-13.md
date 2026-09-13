# Investigation "vrai moteur Unity côté serveur" (2026-09-13) — ARRÊTÉE avant implémentation, voir pourquoi

**Statut : investigation terminée, implémentation NON commencée.** Ce document existe parce que la
prémisse de la tâche demandée ("bascule complète du jeu vers un mode où le serveur calcule avec le
vrai moteur Unity 3D") s'est révélée fausse en lisant le code réel — continuer aurait livré un
changement qui ne fait pas ce qui était demandé, avec un vrai coût de régression (perte de la
scalabilité "milliers de parties en parallèle" acquise le 2026-08-30) pour ZÉRO bénéfice réel. Le
travail a donc été arrêté ici, documenté, plutôt que poussé à l'aveugle pendant les 3h demandées.

## Ce qui était demandé

Faire basculer Deathmatch/Zone de Contrôle (aujourd'hui : résolveur pur `TacticalCore`, voir
`04-unity-headless-server.md`) vers le même modèle que la Conquête, présentée plus tôt dans la
conversation comme "le vrai moteur Unity/PhysX tournant sur le serveur" — l'alternative dite
"Option A" discutée avec l'utilisateur.

## Ce que le code montre RÉELLEMENT (lu directement, pas supposé)

`MatchSessionManager_CombatLive.cs` — la famille de méthodes utilisée EXCLUSIVEMENT par
`RunConquestSkirmish` (`RunDeploymentPhase`, `RunPlanningPhase`, `RunExecutionPhase`,
`ApplyForPlayer`) — a été relue ligne par ligne. Son `RunExecutionPhase` (lignes 181-300+) :

1. Convertit les vraies `UnitAI` de la scène en `TacticalUnit` (données pures) via `BuildTacticalUnit`.
2. Appelle **`TacticalResolver.Resolve(worldState, ordersTeam1, ordersTeam2, mortarStrikes)`** —
   **exactement la même fonction pure/déterministe** que la famille "Pure" utilisée par Deathmatch/
   Zone de Contrôle. Pas de `Physics.RaycastAll`, pas de `NavMeshAgent.CalculatePath`, pas de boucle
   de combat temps réel — le calcul de qui touche qui, qui meurt, qui se déplace où, est **strictement
   le même code** dans les deux familles.
3. Réapplique ensuite le résultat sur les vraies `UnitAI`/`BuildingStructure` (position, PV, fenêtre
   occupée, dégâts de bâtiment via `DestructibleEnvironment.TakeDamage`) — uniquement pour que la
   scène serveur reste un miroir cohérent (dégâts de mesh réels, état de fenêtre partagé) et que les
   clients puissent rejouer une animation.

**Conclusion directe** : la Conquête n'exécute PAS "le vrai moteur physique Unity" pour décider des
combats. Elle utilise le même résolveur pur que Deathmatch/Zone de Contrôle, simplement enveloppé
autour de vraies `UnitAI` de scène (pour la géométrie GPS réelle et les effets de destruction). C'est
la raison même pour laquelle `04-unity-headless-server.md` dit explicitement, en tête de section
"Principe" : *"Le calcul de combat lui-même ne tourne plus en temps réel du tout"* — et précise que
l'ancien texte parlant d'une "simulation NavMeshAgent temps réel" a été **remplacé**, pas juste
déprécié. Vérifié aussi : ce document n'a plus de section "Historique" en bas (contrairement à ce
que sa propre note de tête laisse penser) — l'ancienne architecture temps réel a été retirée du
document, pas archivée.

**Donc : il n'existe aujourd'hui, dans ce projet, AUCUN chemin de code "vrai moteur 3D Unity/PhysX
qui calcule le combat".** Basculer Deathmatch/Zone de Contrôle vers la famille "Live" de la Conquête
n'aurait rien changé au fond du calcul (même `TacticalResolver.Resolve()`) — seulement réintroduit la
contrainte "un seul combat à la fois par processus" (vraie scène/NavMesh partagée), pour laquelle
tout le chantier du 2026-08-30 ("Option B", voir `project_novgov_scaling_2026-08-30` en mémoire de
session) a justement été fait pour s'en débarrasser. C'est une régression pure, sans aucune
contrepartie en fidélité de simulation puisque le calcul est identique des deux côtés.

## Correction d'une affirmation faite plus tôt dans la conversation

Plus tôt dans l'échange, il a été dit que la Conquête "fait tourner de vraies `UnitAI` avec du vrai
`Physics.OverlapSphere` pour les dégâts de mortier, en temps réel" comme illustration du modèle "vrai
Unity". C'était imprécis : `Physics.OverlapSphere`/dégâts de zone bien réels existent dans le pipeline
(voir plus loin dans `RunExecutionPhase`), mais **la décision qui touche/rate, qui se déplace où**
vient du même `TacticalResolver.Resolve()` déterministe — pas d'un vrai calcul physique en temps réel
équivalent à ce qui tourne en Solo. Correction faite ici pour que cette conversation reste exacte.

## Ce qu'il faudrait VRAIMENT construire pour obtenir "le vrai moteur Unity 3D" côté serveur

Ce serait un chantier **neuf**, pas une bascule de code existant :
- Retirer l'appel à `TacticalResolver.Resolve()` et laisser tourner, côté serveur, la boucle de
  combat temps réel `UnitAI_Combat.Update()` (scan de cible, `Physics.RaycastAll`, tir) tick par tick,
  comme en Solo — ce qui existait AVANT le rewrite du 2026-08-30 et qui a été délibérément retiré.
- Ça réintroduit **exactement** les problèmes discutés en long dans cette conversation : PhysX/NavMesh
  non garantis déterministes (même sur une seule machine, d'une exécution à l'autre — voir le
  commentaire de `TacticalTypes.cs:12`), donc un risque de désync/incohérence dans le résultat renvoyé
  aux deux joueurs, pour un gain de fidélité qui n'a en réalité aucune raison d'être puisque le
  résolveur pur donne déjà EXACTEMENT les mêmes règles (portées, dégâts, ligne de vue, garnison).
- Coût de capacité : retour à "un combat à la fois par processus" (scène/NavMesh vivants), comme la
  Conquête aujourd'hui — sauf qu'on perdrait ce gain pour rien, le résolveur restant la même boîte
  noire déterministe qu'aujourd'hui.

## Recommandation

Ne pas construire ça. Si le sujet de fond reste "je veux plus confiance dans la fidélité du calcul
serveur", le vrai levier reste ce qui a été proposé plus tôt dans la conversation : étoffer les tests
de parité automatiques (`Assets/Editor/TacticalCoreSelfTest_*`) entre le comportement `UnitAI` de
référence et `TacticalResolver`, pas remplacer un moteur déterministe par un autre moteur
déterministe identique enveloppé différemment.

## État du dépôt

Branche `feature/real-unity-server-authority` créée, contient uniquement ce document (aucun code de
jeu modifié) — le correctif de sélection d'unité (`TacticalPathManager_Selection.cs`) trouvé plus tôt
dans la session a été commité séparément sur `master` (commit `d87c06d`), avant la création de cette
branche, puisqu'il s'agit d'un correctif indépendant déjà validé.
