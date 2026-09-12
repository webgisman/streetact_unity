# Rapport de session — 2026-09-11/12

## ⚡ MISE À JOUR 2026-09-12 (après-midi) — lire en premier

Suite à ton retour ("gestion des unités catastrophique, il faut changer le mode de diagnostic, revois
tout, il y a du code mort, unifie le mode de calcul"), voici ce qui a été fait AUJOURD'HUI, par-dessus
la session de cette nuit (dont le détail original suit plus bas, §1-7).

### A. Déploiement — FAIT (tout ce qui était en attente hier soir)

Rebuild serveur Linux + APK Android depuis le code d'hier soir (le commit `427fc4b`), les DEUX vérifiés
frais (recherche des symboles `MaxMortarsPerTeam`/`shoot_target_id`/`SelectionAnchorWorldPos` dans le
binaire compilé, pas juste une date de fichier), et redéployés sur novgov.com. Serveur confirmé stable
(pas de crash-loop), logs propres, joignable de l'extérieur (`novgov.com:7777`), avec un conteneur de
sauvegarde (`novgov-game-server:backup_20260912_131126`) prêt en cas de souci. **Tout ce qui a été
corrigé hier soir ET aujourd'hui est maintenant EN LIGNE et dans l'APK
(`build/Android/Novgov-Test.apk`, 148 Mo, prêt à installer).**

### B. Nouveau mode de diagnostic — FAIT, c'est le changement le plus important

Le problème méthodologique d'hier soir : chaque bug de sélection n'était confirmé qu'en te demandant de
reproduire en direct puis en lisant ton `Editor.log` — lent, et ça te demandait de rester devant l'écran
à chaque fois. Corrigé : **un vrai test automatisé qui pilote le jeu lui-même**, sans avoir besoin de toi.

`Assets/Editor/TacticalSelectionAutoTest.cs` — exécutable à tout moment avec :
```
"C:\Program Files\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath "E:\NOVGOV\My project" -executeMethod TacticalSelectionAutoTest.RunAll -logFile un_chemin.log
```
Il crée de VRAIES unités (`UnitAI`) avec de vrais colliders (en invoquant leur vrai `Start()` par
réflexion, puisque Unity ne l'appelle normalement qu'en Play Mode), fait un vrai `Physics.Raycast`
depuis une vraie caméra oblique, et vérifie le résultat réel de la sélection — pas une simulation
séparée, le VRAI code du jeu (`TacticalPathManager.ResolveClosestPlayerUnit`, extrait de
`HandlePointerInput` exprès pour ça aujourd'hui) et le VRAI garde-fou de fin de tour
(`LancerExecutionTour`). **5/5 tests passent**, vérifiés en le faisant tourner réellement, pas juste en
relisant le code. C'est un vrai changement d'outillage, pas une promesse : la prochaine fois qu'un bug
de sélection est signalé, je peux écrire un test qui le reproduit et le vérifie moi-même, sans attendre
que tu retestes en direct à chaque itération.

### C. Ta demande "mets le calcul du Deathmatch sur tout le jeu même en solo" — DÉJÀ FAIT, vérifié dans le code

Bonne nouvelle en creusant : **c'est déjà le cas, depuis le 30 août**, pour tout ce qui est réellement
jouable aujourd'hui. Preuve directe (`MatchSessionManager_CombatLive.cs`, utilisé par Conquête ET
Entraînement) :

> « REMPLACE ENTIÈREMENT l'ancienne méthode (simulation Unity réelle : unit.ExecuterOrdres() pilotant
> NavMeshAgent + Physics.RaycastAll…). Le résultat officiel est maintenant calculé UNE FOIS,
> instantanément, par TacticalResolver.Resolve() — une fonction pure… Le NavMeshAgent/PhysX ne servent
> plus qu'au rendu visuel solo (mode Hors-Ligne) : ils n'influencent plus jamais un résultat
> multijoueur. »

Donc : Match à Mort, Zone de Contrôle, Conquête ET Entraînement utilisent TOUS le même moteur de calcul
déterministe (`TacticalResolver`, `Assets/Scripts/TacticalCore/`) — vérifié en lisant le code source,
pas supposé. La seule différence entre eux est la façon dont l'IA adverse choisit ses ordres
(`TacticalAIPlanner`, pour la garnison de Conquête), pas la façon dont ces ordres sont ensuite calculés.

**Ce qui n'utilise PAS ce moteur** : une portion entière de code (`TacticalPathManager_Execution.cs`,
branche déclenchée quand `MultiplayerMatchController.IsActive` est faux) qui fait tourner une vraie
simulation NavMeshAgent en temps réel — MAIS j'ai vérifié qu'**aucun bouton du menu actuel n'y mène** :
l'écran de choix de mode (`ModeSelectScreen.uxml`) ne propose que Conquête/Match à Mort/Zone de
Contrôle, et LES TROIS passent par le serveur (donc par `TacticalResolver`) avant même d'atteindre cette
branche. Concrètement : **ce bout de code semble mort** — plus personne ne peut l'atteindre en jouant
normalement. C'est probablement une partie du "code mort" que tu soupçonnais.

Je ne l'ai **pas supprimé** : c'est un morceau important (toute la logique de mouvement/combat en temps
réel), et si je me trompe sur son inaccessibilité (un mode caché que je n'aurais pas trouvé, un projet de
le remettre plus tard), le supprimer à tort serait difficile à rattraper. Je te laisse confirmer avant
qu'on y touche.

### D. Nettoyage de code mort — FAIT, prudent

Un balayage complet de `Assets/Scripts/` (724 méthodes/champs privés, 138 classes, chaque fichier
`.cs`/`.uxml`/`.unity`/`.prefab`/`.asset` du projet — 320 fichiers) a trouvé et confirmé (zéro référence
nulle part, vérifié indépendamment) exactement deux choses vraiment mortes, maintenant supprimées :
- `Assets/Scripts/UI/UIAnimator.cs` (fichier entier, utilitaires d'animation UI Toolkit jamais appelés).
- `SupabaseDatabaseClient.UnityWebRequestExtensions` (méthodes d'extension async/await jamais utilisées
  — le reste du fichier fonctionne par coroutines/callbacks).

Une chose flaguée mais PAS touchée, à ta décision : `MultiplayerMatchController.InstanceStaleSeconds`
(const jamais utilisée) sent le début d'une fonctionnalité jamais branchée (filtrer les instances de
serveur obsolètes dans le matchmaking) plutôt qu'un oubli — et cette zone de code date de l'ancienne
architecture "pool de plusieurs instances serveur", elle-même remplacée depuis par une seule instance +
un pool de threads (voir `08-known-issues-and-todo.md`, bandeau d'avertissement en haut de fichier). Je
n'ai pas cherché à démêler ça aujourd'hui — risque de casser le matchmaking en croyant nettoyer.

### E. Sur "la gestion des unités est catastrophique"

En plus des correctifs déjà listés (§5 plus bas), une chose qui existe déjà et que tu ne connais peut-être
pas : il y a une **barre d'escouade** (coin haut-droit, à côté du radar) qui groupe tes unités par type
(mortier/char/canon/infanterie) — cliquer dessus fait défiler la sélection et RECENTRE LA CAMÉRA sur
chaque unité de ce type, SANS passer par le tap 3D ni ses soucis d'occlusion/angle de caméra. C'est déjà
la façon la plus fiable de sélectionner tes unités aujourd'hui, même après le correctif de tap — je le
mentionne parce que ça a l'air d'être resté peu visible/peu utilisé.

### F. Destruction de bâtiment enfin transmise au client (§19.9.3 de la liste connue) — AJOUTÉ, testé, DÉPLOYÉ

Un bâtiment détruit par un mortier ne le devenait que côté serveur — les deux joueurs le voyaient
rester intact à l'écran. Corrigé : le serveur transmet maintenant quels bâtiments viennent d'être
détruits, et le client rejoue la destruction visuelle (gravats, écroulement) SANS jamais recalculer
de dégâts (le serveur a déjà décidé qui meurt) ni retirer le bâtiment de sa liste interne (ce qui
aurait décalé l'identité de tous les bâtiments suivants pour le reste de la partie — un piège trouvé
et évité avant d'écrire le moindre code, pas après). Vérifié par 3 nouveaux tests automatisés
(`Assets/Editor/TacticalNetworkReplayAutoTest.cs`, 3/3 verts) : pas de dégâts fantômes sur une unité
voisine, pas de décalage d'index, pas d'effet de bord si le même bâtiment est signalé deux fois.

Limite assumée et documentée (pas corrigée) : le mode Conquête (actuellement inatteignable depuis le
menu, voir §C plus haut) retire encore ses bâtiments détruits de sa PROPRE liste côté serveur à
chaque tour (comportement existant, non touché) — sur une partie à plusieurs tours, ça pourrait
décaler les identifiants entre client et serveur. Non corrigé pour ne pas toucher aux hypothèses de
l'IA (`TacticalAIPlanner`) sans pouvoir tester en conditions réelles ; sans impact sur Match à Mort/
Zone de Contrôle (qui ne fonctionnent pas comme ça).

### Redéploiement (encore) — FAIT

Serveur + APK rebuild une seconde fois cet après-midi pour inclure ce dernier point, vérifiés frais
(présence de `destroyed_building_ids`/`ApplyNetworkDestruction` dans les deux binaires) et redéployés
sur novgov.com — conteneur stable, joignable de l'extérieur, sauvegarde
`novgov-game-server:backup_20260912_134...` disponible en cas de souci.

### État git

Tout committé et poussé sur `origin/master` :
- `427fc4b` (hier soir — voir §1-6 plus bas)
- `fd39b73` (aujourd'hui — refactor + test auto + nettoyage code mort)
- `200f954` (aujourd'hui — extension du test auto au garde-fou fin de tour)
- `95ff4df` (aujourd'hui — destruction de bâtiment transmise au réseau, §F ci-dessus)

---

## Rapport original de la nuit (2026-09-11/12), pour référence

Résumé de tout ce qui a été fait cette nuit sur le mode multijoueur Deathmatch, dans l'ordre.

## 1. Saccade du mouvement en multijoueur — CORRIGÉ, déployé et vérifié en direct

Le rejeu d'un tour téléportait chaque unité d'1m toutes les 250ms au lieu d'un mouvement fluide.
`MultiplayerMatchController.PlaySnapshotsBody` interpole maintenant frame par frame (Lerp/Slerp) entre
deux positions de tick au lieu de figer la position puis sauter à la suivante.

## 2. Combat silencieux en multijoueur — CORRIGÉ, déployé et vérifié en direct

Les combats ne montraient rien (pas de flash, pas de traceur, pas de son, pas d'effet d'impact) car le
rejeu réseau appelait seulement `SetNetworkHealth`/`SetNetworkAnimState`, jamais le vrai code de tir.
Ajouté `shoot_target_id` au protocole réseau + `UnitAI.PlayNetworkShotEffects`/`PlayNetworkHitReaction`
(purement cosmétiques, ne touchent jamais aux PV — les dégâts restent calculés une seule fois côté
serveur). Le mortier est volontairement exclu (la résolution ne garde pas trace du vrai tireur/tick pour
un tir de mortier — voir le code, `ApplyAreaDamage` — corriger ça correctement demanderait de toucher au
moteur déterministe pur, jugé trop risqué pour une session sans retest possible).

**Ces deux premiers points ont été rebuild (serveur Linux + APK Android) et redéployés sur novgov.com
la nuit dernière, vérifiés stables et joignables.** Les points suivants (3 à 6) sont dans le commit mais
**pas encore rebuild/redéployés**.

## 3. Plafond de 2 mortiers par camp — AJOUTÉ

Avant, un camp pouvait aligner jusqu'à 4 mortiers (seule limite : le budget en points). Ajouté
`MaxMortarsPerTeam = 2`, appliqué côté client (`UnitSpawnerUI`, message immédiat si dépassé) ET côté
serveur (`MatchSessionManager_Deployment.FilterRosterToBudget`, pour qu'un client modifié ne puisse pas
contourner la règle).

## 4. Garde-fou sur FIN DE TOUR — AJOUTÉ

Un vrai clic sur FIN DE TOUR (pas un bug de sélection) terminait le tour après un seul ordre donné à une
seule unité — confirmé directement dans `Editor.log` (trace `Clickable → OnPointerUp →
LancerExecutionTour`). Désormais, si au moins une unité vivante du joueur n'a encore reçu aucun ordre ce
tour-ci, un premier tap sur FIN DE TOUR affiche un avertissement au lieu de terminer le tour ; un second
tap dans les 3 secondes confirme. Aucun autre bouton/écran modifié.

## 5. Bug de sélection réel trouvé et corrigé : un char/canon pouvait voler la sélection à un fantassin voisin

Root cause confirmée en lisant le `Editor.log` d'une vraie session de test (pas une supposition) : la
même unité (un char) était re-sélectionnée plusieurs fois de suite malgré des taps visant clairement une
autre unité à côté. `UnitAI.SelectionAnchorWorldPos` utilisait le centre 3D complet du modèle pour
comparer les distances à l'écran — correct en X/Z (correctif du 9 septembre), mais pour un véhicule haut
(~1-2m), ce centre est à mi-hauteur de la carrosserie, pas au sol. En vue oblique ("Commandement"), ça
décale sa position projetée à l'écran assez pour chevaucher la zone de tap d'un fantassin voisin posé au
sol. Corrigé en ramenant l'ancre au niveau du sol (`transform.position.y`) tout en gardant X/Z du centre
du collider. **Reproduit et vérifié corrigé en direct** (log confirmant la sélection correcte des deux
unités, camp bleu ET camp rouge, après le correctif).

Un symptôme signalé en cours de route ("le canon ne bouge pas mais le fantassin oui") s'est avéré être
autre chose : la zone protégée du radar (coin haut-droit de l'écran, pour ne pas donner d'ordre par
accident en lisant le radar) a une taille MINIMUM (140px) qui ne rétrécit pas sur un tout petit écran —
dans la fenêtre de test réduite ("Player 2" en Multiplayer Play Mode), cette zone couvre une bien plus
grande partie de l'écran que sur un vrai téléphone, et a avalé un tap de déplacement qui n'avait rien à
voir avec le radar. **Pas corrigé** : ce n'est pas vraiment un bug de code (le radar est réellement
affiché aussi grand dans une fenêtre réduite), plutôt un artefact de test dans une fenêtre trop petite —
ça ne devrait pas se reproduire de la même façon sur un vrai écran de téléphone. À garder en tête si ce
symptôme précis ("toujours la même unité qui ne peut pas recevoir d'ordre") revenait en testant dans une
petite fenêtre.

Un incident distinct et non reproduit une seconde fois : à un moment, tout le camp rouge ne pouvait plus
rien sélectionner du tout (alors que le bleu fonctionnait), corrigé tout seul après un simple
redémarrage du Play Mode. Root cause non trouvée avec certitude — possiblement un artefact propre aux
redémarrages répétés de Play Mode pendant les tests (recompilation de script en cours de partie), pas
confirmé comme affectant une vraie partie à 2 appareils réels.

**Méthode utilisée pour tout ce point 5** : ajout de logs de diagnostic temporaires
(`[TapDiag]`), lecture du VRAI `Editor.log` de la bonne fenêtre juste après reproduction par le joueur,
puis retrait des logs une fois la cause confirmée — jamais de correctif appliqué à l'aveugle sur la base
d'une supposition non vérifiée.

**Point technique à retenir pour la suite** : en Multiplayer Play Mode (Unity 6), chaque "Joueur
Virtuel" tourne dans son PROPRE processus Unity.exe avec son propre `Editor.log`, sous
`Library/VP/<id>/Logs/Editor.log` — jamais le `Logs/Editor.log` du projet, qui n'est que celui de
l'éditeur principal. Retrouver le bon fichier :
`Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Select ProcessId,CommandLine` puis lire
l'argument `-logFile` de la ligne de commande.

## 6. Nettoyage de messages joueur

Deux messages affichaient un nom technique brut (`VehiculeCanon`, ou le nom interne
`Fantassin_2_4`) au lieu d'un nom lisible (`Véhicule Canon`, `Fantassin`) — petite source de confusion
possible, corrigée par cohérence avec le reste des messages du dock de déploiement.

## 7. Nouveau test automatisé : simulation de partie complète

Le harnais de tests (`Tools/run-tests.ps1`, hors-Éditeur) avait 76 tests, chacun isolé sur UN mécanisme.
Aucun ne jouait une partie de bout en bout. Ajouté (`Tools/TacticalCoreTests/
TacticalCoreSelfTest_FullMatchSim.cs`) : une simulation complète de Deathmatch — déploiement de 4
unités/camp (respectant EXACTEMENT les 3 plafonds réels : 8 points, 6 unités, 2 mortiers max), puis
jusqu'à 10 tours de résolution réelle (`TacticalResolver.Resolve`), avec vérification qu'aucune unité
déjà morte ne rejoue, qu'aucune unité vivante ne finit à PV négatifs ou nuls, qu'un vrai combat a bien
eu lieu, et que la partie se termine proprement. Plus deux tests dédiés au nouveau plafond de mortiers
(coupe bien exactement le 3e, jamais un roster déjà valide). **Le harnais est maintenant à 79 tests, tous
verts, les 3 configurations de compilation (client/serveur/éditeur) aussi.**

Cette simulation tourne au niveau du moteur pur (`TacticalCore`), pas dans l'Éditeur Unity réel.
**Mise à jour du 12/09 : ce n'est plus toute la limite** — voir §B tout en haut de ce fichier, un vrai
test Éditeur automatisé pilote maintenant aussi la vraie sélection/le vrai garde-fou de fin de tour.

## Ce qui restait à faire (état à la fin de la nuit — voir la mise à jour tout en haut pour l'état actuel)

1. ~~Rebuild + redéploiement du serveur Linux et de l'APK Android pour les points 3 à 6~~ **FAIT le
   12/09 après-midi, voir §A tout en haut.**
2. **Un vrai test à 2 joueurs** (idéalement 2 vrais téléphones) reste la seule chose qu'aucun test
   automatisé ne remplace — toujours à faire.
3. Le "gros" incident du point 5 (camp rouge totalement bloqué, résolu par un simple redémarrage) n'a
   toujours pas de cause confirmée — à surveiller si ça revient.
4. Le reste de la liste connue des lacunes du mode Deathmatch (§19.9 de
   `Assets/_ServerDocs/multiplayer/08-known-issues-and-todo.md` : destruction de bâtiment non transmise
   au client, postures GUETTER/CAMOUFLAGE jamais réinitialisées, etc.) toujours pas touché — resté
   hors-scope par choix, pas oublié.

## Commits

`427fc4b`, `fd39b73`, `200f954` sur `origin/master` — `git log --oneline -5` pour le détail.
