# Plan de test — 2 téléphones

**État (2026-08-29) : le VPS/serveur de jeu (doc 01, 04) sont faits et vérifiés en direct — voir
08-known-issues-and-todo.md §9. Ce qui reste avant de pouvoir dérouler ce plan : un build Android
récent avec les scripts réseau (doc 05), jamais encore testé sur un appareil réel.**

À exécuter une fois : VPS déployé (doc 01), auth fonctionnelle (doc 02), serveur de jeu
démarré (doc 04), client modifié (doc 05).

## Pré-requis

- Les deux téléphones ont un build récent de l'app pointant vers `https://novgov.com`
  (auth) et `novgov.com:7777` (jeu) — pas `localhost`, pas d'IP locale.
- Les deux téléphones sont sur des réseaux différents si possible (un en WiFi, un en 4G/5G) pour
  valider le comportement réseau réel, pas juste le même LAN.
- **Choisir le même mode de jeu sur les deux téléphones** (Deathmatch ou Zone de Contrôle) avant
  de lancer le matchmaking — `MatchSessionManager` n'apparie que deux joueurs ayant demandé le
  même mode (`join_matchmaking.mode`), deux files d'attente séparées. Se tromper de mode fait
  attendre indéfiniment dans deux files différentes.
- L'appariement est **FIFO public**, pas une invitation privée entre les deux téléphones : les 2
  premiers joueurs en attente sur le même mode sont appariés ensemble. Tant que vous êtes seuls
  sur le serveur (un seul match actif à la fois, voir "Portée V1" du README), vous serez
  forcément appariés l'un avec l'autre.

## Scénario 1 — Inscription et connexion

1. Téléphone A : créer un compte (`signup`), vérifier que le profil apparaît bien dans
   `public.profiles` (via `psql` sur le serveur : `select * from profiles;`).
2. Téléphone B : idem avec un second compte.
3. Fermer et rouvrir l'app sur les deux : vérifier que le refresh token reconnecte sans
   redemander le mot de passe.

## Scénario 2 — Matchmaking et déploiement manuel

**Mise à jour du 2026-08-29** : le déploiement n'est plus automatique — chaque joueur place
désormais sa propre escouade manuellement (voir `08-known-issues-and-todo.md` §10.2 et
`03-network-protocol.md`, "submit_deployment"/"deployment_result").

1. Téléphone A : "Multijoueur" → "Recherche d'adversaire...".
2. Téléphone B : idem, dans les ~10 secondes qui suivent.
3. Vérifier que les deux reçoivent `match_found` avec les bons `team_id` (1 et 2) et le bon nom
   d'adversaire.
4. Vérifier que le dock de déploiement s'ouvre automatiquement sur les deux téléphones, verrouillé
   sur le SEUL camp local (impossible de basculer sur "Ennemi" — les boutons Joueur/Ennemi et
   ESCOUADE IA/DÉPLOIEMENT AUTO doivent être masqués), avec un bouton "CONFIRMER LE DÉPLOIEMENT" à
   la place.
5. Placer manuellement 4 unités de combat (n'importe quel mélange Fantassin/Char Leopard/Véhicule
   Canon/Mortier) sur chaque téléphone, dans des zones différentes, puis confirmer sur les deux.
6. Vérifier que les deux téléphones reçoivent `deployment_result` avec les positions des DEUX
   camps, et voient exactement les mêmes positions pour l'adversaire (pas seulement pour soi-même).
7. **Cas limite à tester** : sur un des deux téléphones, attendre l'expiration du timer de 45s
   sans rien placer (ou sans confirmer) — vérifier que ce camp reçoit quand même un déploiement
   (repli automatique) et que la partie démarre normalement pour les deux joueurs.
8. **Cas limite à tester** : essayer de placer une unité très loin de sa propre zone (ex: au
   centre de la carte ou dans la zone adverse) — vérifier dans les logs serveur ou en observant la
   position reçue dans `deployment_result` qu'elle a bien été ramenée dans la zone légale (cercle
   de 22m autour du point d'ancrage du camp), pas rejetée en bloc ni acceptée telle quelle.

## Scénario 3 — Tour normal (les deux joueurs jouent)

1. Sur chaque téléphone, déplacer une unité et valider "FIN TOUR" avant l'expiration du timer
   (60s).
2. Vérifier que les deux téléphones reçoivent le même `turn_result` et rejouent la même
   animation (mêmes morts, mêmes tirs, même timing relatif) — le serveur calcule une seule fois
   (`MatchSessionManager.RunExecutionPhase`), les deux téléphones ne font que rejouer les mêmes
   snapshots reçus.
3. ~~Vérifier en base (`match_event_log`)~~ **Pas encore possible** : ce log détaillé par tour
   n'est pas branché (limitation assumée V1, voir 08-known-issues-and-todo.md §5) — seules
   `matches`/`match_participants` sont écrites (statut, équipes, résultat final).

## Scénario 4 — Joueur absent (le cœur du mécanisme "zéro attente")

**Correction par rapport à l'intention initiale** : il n'y a **pas** de substitut IA pour un
joueur absent (contrairement à ce qui était envisagé) — ses unités restent simplement immobiles
ce tour-ci (`MatchSessionManager.ApplyForPlayer`, `unit.ClearTacticalPath()`), sans planification
`TacticalAIPlanner`. Le scénario ci-dessous reflète le comportement réel :

1. Téléphone A : jouer normalement son tour.
2. Téléphone B : **ne rien faire** (ou couper le WiFi/4G) jusqu'à expiration du timer de tour.
3. Vérifier :
   - Téléphone A reçoit `opponent_ghosted` (`reason: "disconnected"` ou `"timeout"`) et voit
     l'indicateur "Adversaire absent".
   - Le tour se résout quand même dans les temps (pas d'attente infinie) — les unités de
     l'équipe B restent simplement immobiles ce tour-ci (pas de mouvement, pas de tir, pas de
     mise à couvert automatique).
   - **Pas de champ `is_ghosted` en base** à vérifier — ce comportement n'est pas persisté,
     seulement signalé en temps réel à l'adversaire via le message réseau.
4. Reconnecter le téléphone B avant le tour suivant : vérifier qu'il reprend la main normalement
   dès le tour suivant (aucun état "Ghost" à réinitialiser, c'est recalculé à chaque tour selon
   si `submit_turn` a été reçu ou non).
5. **Limitation connue à tester explicitement** : si la coupure réseau du téléphone B est
   **totale** (pas juste un tour manqué — vraie déconnexion TCP), il ne peut pas *reprendre* cette
   même partie en se reconnectant : il rejoindrait une nouvelle recherche de match, pendant que
   son ancienne équipe reste immobile jusqu'à la fin de la partie en cours (voir le commentaire en
   tête de `MatchSessionManager.cs`, "Limitation connue V1").
6. **Mise à jour du 2026-08-29** : le téléphone B qui verrouille son écran ou passe l'app en
   arrière-plan (bouton Accueil) ferme désormais la connexion proprement et immédiatement
   (`GameServerClient.OnApplicationPause`), au lieu de laisser une socket morte — vérifier que
   l'indicateur "Adversaire absent" apparaît bien côté A dès ce moment-là plutôt qu'après un délai
   incohérent. Vérifier aussi qu'une coupure réseau silencieuse (mode avion sans fermer l'app) est
   toujours détectée dans un délai raisonnable (~20s) grâce au nouveau timeout socket fini côté
   serveur — voir §10.3 de `08-known-issues-and-todo.md`.

## Scénario 5 — Fin de partie

1. Poursuivre les tours jusqu'à élimination totale d'une équipe.
2. Vérifier `match_over` reçu des deux côtés avec le bon `winner_team`.
3. Vérifier `matches.status = 'finished'`, `ended_at` renseigné, en base.

## Ce qu'on observera en priorité (retour d'expérience à collecter)

- Latence perçue entre "FIN TOUR" et le début de l'animation reçue (round-trip + temps de
  simulation serveur).
- Cohérence visuelle entre ce que chaque téléphone affiche pour le même tour (les deux doivent
  voir exactement la même issue, puisque c'est le serveur qui a calculé le résultat une seule
  fois). **Point précis à re-vérifier après le 2026-08-29** : le tout premier test réel à 2
  téléphones avait révélé un tir/de la fumée visibles sur un seul des deux écrans (simulation de
  combat locale fantôme sur le téléphone en attente réseau) — corrigé (voir
  `08-known-issues-and-todo.md` §10.4), mais jamais encore reconfirmé par un vrai test.
- Robustesse du Ghost si la coupure réseau du scénario 4 survient **pendant** l'envoi de
  `submit_turn` plutôt qu'avant (paquet perdu en cours de route) — vérifier que le serveur
  traite bien l'absence de confirmation comme "pas d'ordre reçu" et bascule en Ghost, plutôt que
  de planter en attendant une trame TCP incomplète.
