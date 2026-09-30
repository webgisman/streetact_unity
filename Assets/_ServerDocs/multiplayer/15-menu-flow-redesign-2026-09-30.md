# 15 — Refonte du parcours des menus : écran CONQUÊTE (2026-09-30)

Retours joueur, dans l'ordre :
1. « la logique du menu après la première interface (choix solo et multijoueur) est catastrophique,
   non intuitive et pas du tout compréhensible pour un joueur lambda » ;
2. « après multijoueur il y a COMBATTRE, supprimer ça, je veux juste Conquête ; dans Conquête il y
   aura la gestion et le mode carte, qui doit être beau pour choisir la carte à côté, avec du son ;
   une carte "neutre", ça veut dire quoi, un joueur lambda ne va rien comprendre ».

Changement **client uniquement** (UI Toolkit + contrôleurs) : aucun changement de protocole, de
serveur ni de base → **aucun redéploiement serveur** ; il faut un nouveau build client (APK).

## Parcours

**Avant** : `JOUER EN LIGNE` → boîte système « Autoriser la localisation ? » sans explication →
chargement → connexion → hub de 8 boutons (dont 2 ouvrant le même écran) → carte à 4 boutons de
boussole « ATTAQUER NORD (Neutre) » et coordonnées de tuile brutes.

**Après** : `JOUER EN LIGNE` → compte (connexion / création / reconnexion automatique) → **une seule
fois** : écran « OÙ INSTALLER VOTRE QG ? » qui explique pourquoi la position est lue, puis la boîte
système → chargement du quartier → **CONQUÊTE**, deux onglets :

- **CARTE** — la vraie carte OpenStreetMap (tuiles zoom 17 = exactement une Zone de Conquête) du
  quartier du joueur au centre et de ses 8 voisins, chaque case colorée par son statut :
  **LIBRE** (personne ne l'occupe) · **À VOUS** · **⚔ <pseudo>** (tenu par un autre joueur) ·
  **PROTÉGÉ** (sort d'un siège) · assombri = **hors de portée**. Toucher une case ouvre sa fiche qui
  dit en clair ce qu'elle est et ce que le bouton va faire : PRENDRE CE QUARTIER (gratuit, immédiat),
  M'Y RENDRE, LANCER UN SIÈGE (les 3 étapes expliquées), AMÉLIORER / RECRUTER. Portée = règle serveur
  exacte (`MatchSessionManager_Conquest.RunConquestRequest`) : sans aucun quartier, n'importe quel
  quartier libre ; ensuite seulement ceux qui touchent (4 directions) un quartier possédé. Nouveau
  joueur : la case du centre (son QG), libre, est présélectionnée.
- **GESTION** — « Comment ça marche ? » (la boucle de jeu en 4 lignes), SIÈGES (pastille), CASERNE,
  MES QUARTIERS, CLASSEMENT, se déconnecter du compte.

En-tête commun : MENU (retour Solo / En ligne, session gardée), profil + PA, RAPPORTS (pastille),
alerte « quartier assiégé » avec bouton DÉFENDRE, avis ponctuel (ex : raison d'une déconnexion).

Sons (`Novgov.UI.UiSfx`, une AudioSource 2D, clips mis en cache) : bip d'onglet, confirmation de
sélection, voix radio « Roger, will do » (prendre, s'y rendre, défendre) et « Target locked »
(siège), fanfare à la prise d'un quartier (`ProceduralAudioBuilder.CreateVictoryFanfareSound`),
son d'erreur sur un refus.

**Supprimés de l'interface** : COMBATTRE / Match à mort / Contrôle de zone / Entraînement (écran
BattleSelect), REJOUER en fin de partie en ligne. `StartDeathmatch` / `StartZoneControl` /
`StartPracticeVsAI` restent dans `MultiplayerMatchController` sans appelant UI (le serveur les gère
toujours).

## Vocabulaire (joueur lambda)

`Novgov.UI.QuartierText` : une Zone s'appelle un **quartier**, repéré par rapport au QG (« quartier
au Nord-Est de votre QG », « à l'Est… »), jamais par ses coordonnées de tuile. Les textes envoyés par
le serveur (rapports `siege_won`/`siege_lost`/`under_attack`) sont réécrits à l'affichage
(`HumanizeServerText`) — pas de redéploiement. « Neutre » → **LIBRE (personne ne l'occupe)**,
« Ennemi » → **tenu par <pseudo>** (pseudos lus dans `profiles`), « AP » → **PA**.

## Bugs de navigation corrigés

| Écran | Avant | Après |
|---|---|---|
| Attente « Recherche d'adversaire… » | **aucun moyen d'en sortir** | ANNULER ; après appariement : QUITTER LA PARTIE (double appui) |
| En partie (solo et en ligne) | **aucun moyen de quitter** sans tuer l'appli | bouton MENU → PAUSE (solo : vraie pause, Reprendre / Recommencer / Menu principal ; en ligne : Quitter) |
| Carte / résultat de conquête | « RETOUR AU HUB » / « OK » renvoyaient au menu Solo/En ligne | retour à la Conquête, même onglet (`MultiplayerMatchController.ReturnToHub`) |
| Fin de partie en ligne | rechargement → menu Solo/En ligne → chargement + reconnexion | RETOUR À LA CONQUÊTE direct (`GameManagerUI.ReloadSceneThen(OpenOnlineHub)`) |
| Fin de partie solo « Rejouer » | renvoyait au menu principal | relance vraiment le solo, + MENU PRINCIPAL |
| Classement « Fermer » | `HideAll()` → écran vide, impasse | superposition, l'écran d'origine reste dessous |
| « DÉCONNEXION » | ne déconnectait pas le compte | MENU (garde la session) et « Se déconnecter du compte » séparés |
| JOUER SOLO après un rechargement | lambda liée une fois = instance `GameManagerUI` de la 1re scène → dock et barre tactique masqués | passe par `GameManagerUI.Instance` |
| Menus en ligne | taps/glissés traversaient jusqu'à la caméra 3D derrière | classe `ui-blocker` reconnue par `UnitSpawnerUI.IsPointerOverOnGUI` |
| Déconnexion en pleine partie | raison affichée nulle part | avis en tête de la Conquête (survit au rechargement) |
| Message serveur reçu juste après ANNULER | pouvait rouvrir un écran de partie | ignoré hors attente/partie |
| Caserne | descriptions Char Léopard / Véhicule canon inversées, Drone achetable mais inutilisable | corrigé, Drone « BIENTÔT » |

## Fichiers

- Écrans : `ConquestScreen.uxml/.uss` (nouveau, remplace `ModeSelectScreen`, `ZoneMapScreen` et
  `BattleSelectScreen`, supprimés), `LocationPromptScreen.uxml`, `PauseMenuScreen.uxml` (nouveaux).
- Scripts : `ZoneMapController` (réécrit : onglets + carte), `UiSfx`, `QuartierText`,
  `InGameMenuController` (nouveaux) ; `MultiplayerMatchController` (état `Hub` = Conquête,
  `EnterHub`, `ReturnToHub`, `OpenManagementScreen`, annulation, `QuitCurrentMatch`) ;
  `GameManagerUI` (`ReloadSceneThen`, `EnsureOnlineZoneReady`, `AcquireHomeZoneFromGps`).
- Outil : `Assets/Editor/ConquestScreenPreview.cs` — captures de l'écran Conquête (paysage + portrait)
  en Play Mode avec des propriétaires fictifs, sans compte ni serveur :
  `Unity.exe -projectPath "…" -executeMethod ConquestScreenPreview.Run -previewOut "<dossier>"`.

## Vérifié / non vérifié

Vérifié : tests + compile-check des 3 configurations verts, import Unity sans erreur, captures de
l'écran Conquête relues (paysage 2400x1080 et portrait 1080x2400 — la fiche en portrait a été
corrigée d'après elles), APK de test généré (`build/Android/Novgov-Test.apk`, 155 Mo, 2026-09-30,
à installer via `adb install -r`). **Non vérifié sur un vrai téléphone** (aucun appareil
branché ce jour) : le parcours complet compte → localisation Android → Conquête → prise d'un
quartier, et le son sur appareil.
