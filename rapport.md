# Rapport de session — 2026-09-11/12

## ⚡⚡⚡ MISE À JOUR 2026-09-12 (nuit) — lire en premier

Des correctifs supplémentaires (rédigés directement dans l'arborescence de travail, documentés dans
`Assets/_ServerDocs/multiplayer/08-known-issues-and-todo.md` §19.15) ont été trouvés NON commités :
confusion couleur/rôle d'équipe (joueur 2 voyait ses propres unités en rouge — c'est CE qu'était le
"role" du dernier retour), dérive de sélection de l'infanterie après un tour (root motion Mixamo
sortant le corps 3D de son collider), écran de fin de partie coupant brutalement le rejeu final, et
un exploit de posture (garder les bonus de couverture en bougeant). Les quatre sont réels et bien
diagnostiqués.

**Un vrai bug trouvé avant de committer** : `IsLocalPlayerTeam` (nouvelle méthode, `UnitSpawnerUI.cs`)
avait été déclarée À L'INTÉRIEUR du bloc `#if !UNITY_SERVER` alors qu'appelée depuis du code compilé
sans garde — cassait ENTIÈREMENT la compilation de la cible Serveur Dédié
(`Assembly-CSharp-Server.check.csproj`). Corrigé (méthode sortie du bloc, même convention que
`IsPointerOverOnGUI` juste en dessous). Sans cette vérification, ce correctif aurait empêché tout
futur build serveur de compiler. Vérifié : 80 tests moteur pur + 3 tests de simulation complète + les
3 compile-checks (Client/Serveur/Éditeur) tous verts après correction. Commit `8b5a0b6`, poussé sur
`origin/master`.

**Redéployé et vérifié une nouvelle fois** : serveur Linux (cache Bee vidé, rebuild propre — DLL
managé daté après le commit du correctif), conteneur `novgov-game-server-1-1` stable, port 7777
joignable de l'extérieur, sauvegarde prise avant. APK Android également rebuild frais (même
correctif, nécessaire pour la cohérence client/serveur).

---

## ⚡⚡ MISE À JOUR 2026-09-12 (soir) — lire en premier, remplace l'état "après-midi" ci-dessous

Ton retour suivant : « toujours dans le mode multijoueur deathmatch, les unité rentre dans les
polygones et il ya toujours des soucis de selection et de fin de tour et de positionnement et de
role ». Voici ce qui a été trouvé et fait, dans l'ordre.

### H. "Les unités rentrent dans les polygones" — VRAI BUG TROUVÉ ET CORRIGÉ (le plus concret des 5 points)

Root cause trouvée en lisant `TacticalResolver.ExpandOrder` (le calcul de trajet, partagé
client/serveur) : le correctif du 2026-09-06 (« un char peut foncer dans un bâtiment ») ne
redirigeait une destination tombant dans l'empreinte d'un bâtiment vers l'extérieur QUE pour
`unit.isTank`. Toute autre unité (fantassin sans ordre d'entrée explicite, véhicule canon, mortier)
tombant sur la même situation — une destination ordinaire qui recouvre géométriquement un bâtiment
que le joueur n'a jamais demandé à visiter — subissait exactement le même bug que les chars avant
correction : `Pathfinding.FindPath` échoue (la cellule d'arrivée est creusée non-franchissable),
et son repli en ligne droite (jamais vérifié pour les non-chars) traverse le mur tout droit.

**Pourquoi ça n'avait jamais été détecté avant** : les deux tests existants qui manipulent un
bâtiment (`TestResolveMovementAvoidsBuilding`, `TestInfantryLongPathAroundBuildingLogged`) visent
toujours une destination DERRIÈRE le bâtiment (donc hors de son empreinte) — jamais une destination
qui tombe DANS l'empreinte sans ordre d'entrée. C'était l'angle mort exact.

**Corrigé** : la redirection s'applique maintenant à TOUTE unité (plus seulement `isTank`), tant
qu'elle n'est pas en train d'entrer/rester dans le bâtiment visé explicitement. 4 nouveaux tests
automatisés (`Assets/Editor/TacticalCoreSelfTest_BuildingIntrusion.cs`) : fantassin redirigé,
véhicule (non-char) redirigé, char toujours redirigé (non-régression du correctif du 09-06), et une
entrée EXPLICITE (menu "Entrer") continue de bien fonctionner. Les 80 tests du moteur pur passent
(76 avant + ces 4), plus les 3 tests de simulation de partie complète, plus les 3 compile-checks —
tout vert. Commit `68ec437`.

**Ce bug explique très probablement aussi une bonne partie du point "positionnement"** : une unité
à moitié encastrée dans une façade de bâtiment (comme le décrivait ton retour) donne exactement
l'impression d'un problème de positionnement — et une unité visuellement à l'intérieur d'un mur
peut aussi expliquer une partie des soucis de sélection rapportés (le raycast/la sélection tactile
peut se comporter bizarrement sur une unité dont le collider est en partie noyé dans la géométrie
du bâtiment). Pas une certitude absolue (aucun repro direct de TA partie), mais c'est la même classe
de bug exacte que celle décrite, et la correction est vérifiée par test, pas une supposition.

### Sur "sélection" et "fin de tour" spécifiquement — réaudité, rien de NOUVEAU trouvé au-delà de H

J'ai relu tout le code de sélection (`TacticalPathManager_Selection.cs`, `_Input.cs`) et du garde-fou
de fin de tour (`_Execution.cs`, `AnyPlayerUnitWithoutOrders`/`pendingEndTurnConfirmUntil`) avec un
œil neuf, en supposant que les corrections d'hier (bug tank/fantassin, garde-fou double-tap) étaient
peut-être incomplètes plutôt que résolues. Résultat honnête : je n'ai pas trouvé de second défaut de
code distinct — juste confirmé que le garde-fou de fin de tour est un comportement VOULU (double-tap
de confirmation si au moins une unité vivante n'a encore aucun point de trajectoire ce tour-ci,
ajouté exprès le 2026-09-12 suite à un retour précédent), pas un bug. Mon hypothèse la plus probable
reste que le bug H ci-dessus (unités visuellement dans les murs) explique une bonne partie de ce qui
a été perçu comme des soucis de sélection/positionnement persistants — mais je n'ai aucune preuve
directe de TA session pour trancher avec certitude entre "c'était le même bug" et "il y a encore
autre chose que je n'ai pas reproduit". Si le problème persiste concrètement après ce déploiement,
la prochaine étape la plus utile serait un `Editor.log` ou une capture d'écran du moment précis où
ça arrive — je peux alors écrire un test qui reproduit exactement ce cas, comme convenu.

### Sur "rôle" — pas de piste solide trouvée, sens précis pas clair

J'ai vérifié l'assignation d'équipe/allégeance (`isPlayerControlled`, posé à partir de `teamID` à
chaque déploiement et à chaque apparition d'unité ennemie via le brouillard de guerre) : rien
d'anormal trouvé dans le code. Je n'ai pas de piste concrète pour ce point précis — "rôle" pourrait
vouloir dire plusieurs choses (allégeance d'équipe, type d'unité/rôle de combat, menu d'actions
proposé selon le type) et je n'ai trouvé aucun bug de code correspondant à aucune de ces lectures.
Si tu peux préciser ce que tu as vu exactement (quelle unité, quel effet observé), je peux creuser
plus efficacement que par relecture de code à l'aveugle.

### Piège d'outillage trouvé (encore) en redéployant — le vrai stale-build gotcha, une variante plus sournoise

En vérifiant la fraîcheur du build serveur (mtime du DLL managé vs heure du commit du correctif —
la méthode déjà documentée), le TOUT PREMIER build a produit un DLL antérieur au commit : cache Bee
périmé (`Library/Bee`/`Library/PlayerDataCache` supprimés puis rebuild propre — a corrigé le
problème). Mais une seconde surprise, plus subtile, est apparue en enchaînant le build Android juste
après : le processus `Unity.exe` du build serveur avait déjà rendu la main (exit code 0 signalé) AVANT
d'avoir réellement terminé tout son travail en arrière-plan (compilation Bee asynchrone) — le build
Android suivant a donc échoué immédiatement (verrou de projet déjà pris par l'instance encore
active), et il a fallu explicitement ATTENDRE la disparition de tout processus `Unity.exe` (pas
seulement le retour du processus lanceur) avant de considérer un build comme réellement terminé.
Documenté ici pour la prochaine fois : ne jamais enchaîner deux builds batch-mode dos à dos sans
vérifier `tasklist`/`Get-Process Unity` d'abord.

### Redéploiement — FAIT et vérifié (serveur ET APK)

Serveur Linux rebuild (propre, cache vidé) et redéployé sur novgov.com : conteneur
`novgov-game-server-1-1` stable (`Up`, pas de crash-loop), logs propres (`236 bâtiments créés`, zéro
échec), port `7777` confirmé joignable de l'extérieur. Sauvegarde de sécurité prise avant
(`novgov-game-server:backup_<horodatage>` via `docker commit`).

APK Android également rebuild frais (`build/Android/Novgov-Test.apk`, mtime 20h21, largement après
le commit du correctif `68ec437` à 19h55 — nécessaire pour que la PRÉVISION client du trajet, qui
partage le même `TacticalResolver.ExpandOrder`, corresponde à ce que le serveur calcule
réellement). Build confirmé réussi dans le log (`[AndroidTestBuildScript] Build Android réussi`).

---

## ⚡ MISE À JOUR 2026-09-12 (après-midi) — lire en premier

Suite à ton retour ("gestion des unités catastrophique, il faut changer le mode de diagnostic, revois
tout, il y a du code mort, unifie le mode de calcul"), voici ce qui a été fait AUJOURD'HUI, par-dessus
la session de cette nuit (dont le détail original suit plus bas, §1-7).

**En bref (détail de chaque point ci-dessous) :**
- **A.** Déploiement de tout ce qui était en attente d'hier soir — FAIT
- **B.** Nouveaux outils de test automatisés (Éditeur, pas juste le moteur pur) — FAIT, 8 tests verts
- **C.** "Mets le calcul du Deathmatch sur tout le jeu" — DÉJÀ FAIT depuis le 30 août, vérifié
- **D.** Nettoyage de code mort — FAIT (2 suppressions sûres) + 1 gros morceau probablement mort flagué, pas supprimé
- **E.** Sur la sélection : une barre d'escouade fiable existe déjà, méconnue
- **F.** Destruction de bâtiment enfin transmise au client — AJOUTÉ, testé, déployé
- **G.** Tirs de mortier enfin validés (anti-triche) — AJOUTÉ, déployé

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

### G bis. Test automatisé étendu à la barre d'escouade + un piège d'outillage important trouvé et documenté

Ajouté un 6e test : la barre d'escouade (cycle par type d'unité, coin haut-droit) fonctionne bien —
vérifié en conditions réelles, pas supposé. En l'écrivant, trouvé un vrai piège qui aurait pu fausser
silencieusement de futurs tests : la cible de build active d'Unity (Serveur/Client) est un réglage
PERSISTANT du projet, pas remis à zéro entre deux commandes séparées. Après un rebuild du serveur
Linux, toute commande de test suivante SANS préciser explicitement "-buildTarget StandaloneWindows64
-standaloneBuildSubtarget Player" restait silencieusement en mode Serveur — rendant invisible tout le
code client (dont la barre d'escouade elle-même) sans le moindre message d'erreur clair. Documenté en
tête des deux fichiers de test pour que ça ne recommence jamais.

### G. Sécurité : les tirs de mortier n'étaient validés par RIEN — CORRIGÉ, déployé

Trouvé en révisant le reste de la liste connue (§19.9.5) : un client modifié pouvait attacher un ordre
"TirMortier" à N'IMPORTE QUELLE unité (même un Fantassin) et viser N'IMPORTE OÙ sur la carte — le
serveur ajoutait la frappe sans la moindre vérification de type d'unité ni de portée. Corrigé
symétriquement dans les deux moteurs (Deathmatch/Zone de Contrôle ET Conquête/Entraînement, qui
partagent le même calcul) : seule une VRAIE unité Mortier peut désormais déclencher une frappe, et
seulement à moins de 120m d'elle-même (même portée que le mode solo). Redéployé une 3e fois (serveur
seul, ce correctif ne concerne pas le client).

### État git

Tout committé et poussé sur `origin/master` :
- `427fc4b` (hier soir — voir §1-6 plus bas)
- `fd39b73` (aujourd'hui — refactor + test auto + nettoyage code mort)
- `200f954` (aujourd'hui — extension du test auto au garde-fou fin de tour)
- `95ff4df` (aujourd'hui — destruction de bâtiment transmise au réseau, §F ci-dessus)
- `e7e0adc` (aujourd'hui — mise à jour de ce rapport)
- `02d0938` (aujourd'hui — validation anti-triche des tirs de mortier, §G ci-dessus)

**novgov.com est actuellement sur `02d0938`, rebuild+redéployé et vérifié stable/joignable une
dernière fois à 13h51.** L'APK (`build/Android/Novgov-Test.apk`) date du build précédent (`95ff4df`,
13h45) — le dernier correctif (§G) est purement serveur, il n'y a rien à changer côté client pour
lui.

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
