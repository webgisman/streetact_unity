# 07 — Tester Novgov (état au 2026-10-03)

## 1. Tests automatiques et compilation (sans ouvrir Unity)

```powershell
.\Tools\run-tests.ps1            # auto-tests TacticalCore + compile-check client / serveur / Éditeur
.\Tools\run-tests.ps1 -TestsOnly # tests seulement (~5 s)
```

Limite : la combinaison « Éditeur + serveur » (`Assets/Editor` compilé avec `UNITY_SERVER`, ce que
fait une vraie build serveur) n'est pas couverte — un outil Éditeur qui utilise du code client doit
être entouré de `#if !UNITY_SERVER` (ex. `ConquestScreenPreview.cs`).

## 2. Deux joueurs dans l'Éditeur (Multiplayer Play Mode)

1. Menu **Window > Multiplayer > Multiplayer Play Mode** : cocher « Player 2 », puis Play.
2. Fenêtre principale : JOUER EN LIGNE → **JOUEUR 1** (bouton rouge). Autre fenêtre : **JOUEUR 2**.
   (`Novgov.Network.EditorTestPlayers` : Joueur 1 = `testlille1@novgov.test`, quartier de Lille Sud ;
   Joueur 2 = `testlille2@novgov.test`, quartier juste au Nord. La carte de chaque fenêtre se place
   sur le quartier de SON compte, sans demande de localisation.)
3. Attaquant : toucher le quartier de l'autre joueur → LANCER UN SIÈGE (écran d'attente).
   Défenseur : bandeau rouge « … assiège … » → DÉFENDRE (apparaît sous ~15 s).
4. Les deux : placer les troupes → CONFIRMER, puis à chaque tour tracer → FIN DE TOUR → regarder le
   rejeu, jusqu'à la victoire.

Un panneau « MODE TEST » (bas de l'écran dans les menus, jamais pendant une partie, Éditeur
uniquement, `EditorDebugOverlay`) rappelle ces étapes. Après un siège, le quartier visé est protégé
6 h et peut avoir changé de mains : pour rejouer tout de suite, remettre les deux comptes à leur état
de départ avec `Tools/db/reset_test_accounts.sql` (supprime leurs parties, sièges et rapports, rend
à chacun son quartier sans protection, classement à 1000 ; commande dans l'en-tête du fichier).

Ce que le jeu a décidé pour chaque tap (unité sélectionnée, menu ouvert, point refusé et pourquoi)
est écrit dans la console : lignes `[Tap]` de `Logs/Editor.log` (fenêtre principale) et de
`Library/VP/<id>/Logs/Editor.log` (joueur virtuel). Plus de panneau à l'écran : il couvrait des boutons.

## 3. Test de bout en bout contre la production (script)

```
python Tools/siege_battle_test.py
```

Joue les deux comptes de test sur le vrai serveur : reprend (ou déclare) un siège de Joueur 1 contre
le quartier de Joueur 2, vérifie que l'attaquant seul attend, que les deux reçoivent `match_found`
(équipes 1/2, carte du quartier), que `city_verify` reçoit une réponse, puis que le siège est rouvert
après le départ des deux. **Agit sur la base de production** (comptes de test uniquement).

## 4. Captures de l'écran Conquête (sans compte ni serveur)

```
Unity.exe -projectPath "<projet>" -executeMethod ConquestScreenPreview.Run -previewOut "<dossier>"
```

Lance la scène en Play Mode, affiche l'écran Conquête avec des propriétaires fictifs (les tuiles
OpenStreetMap sont réelles) et écrit des PNG en paysage 2400×1080, portrait 1080×2400 et fenêtre PC
1280×720 (dont l'écran de connexion avec le bloc MODE TEST), puis quitte l'Éditeur. Pas en `-batchmode`.

## 5. Déplacements d'infanterie sur les toits (vrai moteur, sans serveur)

```
Unity.exe -batchmode -projectPath "<projet>" -executeMethod RoofMovementPlaytest.Run -logFile roof.log
```

Charge le vrai quartier de test (66648,44110) en Play Mode, fait apparaître un fantassin et vérifie
avec le même code que le serveur de jeu : escalade d'un toit ; un tap de rue accroché à un décor reste
un point de sol ; toit → rue à 25 m (descente par un bord sur rue, arrivée au point, aucune
téléportation, plus inscrit sur le toit) ; toit → rue avec GUETTER ; toit → toit d'un autre immeuble
séparé par une rue. Code de sortie 0 = vert ; détail dans les lignes `[RoofPlaytest]` du journal.
Contre-épreuve faite le 2026-10-03 : l'ancien code échoue à 6 vérifications sur 8 (descente posée sur
le toit voisin, à 28,7 m de la cible).

## 6. Sur téléphone

APK de test : `AndroidTestBuildScript.BuildDebugApk` (voir `01-deployment-vps.md` §10 pour la commande),
sortie `build/Android/Novgov-Test.apk`, installation `adb install -r build/Android/Novgov-Test.apk`.
