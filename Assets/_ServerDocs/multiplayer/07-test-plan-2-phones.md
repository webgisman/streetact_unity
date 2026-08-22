# Plan de test — 2 téléphones

À exécuter une fois : VPS déployé (doc 01), auth fonctionnelle (doc 02), serveur de jeu
démarré (doc 04), client modifié (doc 05).

## Pré-requis

- Les deux téléphones ont un build récent de l'app pointant vers `https://ton-domaine.com`
  (auth) et `ton-domaine.com:7777` (jeu) — pas `localhost`, pas d'IP locale.
- Les deux téléphones sont sur des réseaux différents si possible (un en WiFi, un en 4G/5G) pour
  valider le comportement réseau réel, pas juste le même LAN.

## Scénario 1 — Inscription et connexion

1. Téléphone A : créer un compte (`signup`), vérifier que le profil apparaît bien dans
   `public.profiles` (via `psql` sur le serveur : `select * from profiles;`).
2. Téléphone B : idem avec un second compte.
3. Fermer et rouvrir l'app sur les deux : vérifier que le refresh token reconnecte sans
   redemander le mot de passe.

## Scénario 2 — Matchmaking et déploiement

1. Téléphone A : "Multijoueur" → "Recherche d'adversaire...".
2. Téléphone B : idem, dans les ~10 secondes qui suivent.
3. Vérifier que les deux reçoivent `match_found` avec les bons `team_id` (1 et 2) et le bon nom
   d'adversaire.
4. Vérifier le déploiement automatique symétrique des deux escouades.

## Scénario 3 — Tour normal (les deux joueurs jouent)

1. Sur chaque téléphone, déplacer une unité et valider "FIN TOUR" avant l'expiration du timer.
2. Vérifier que les deux téléphones reçoivent le même `turn_result` et rejouent la même
   animation (mêmes morts, mêmes tirs, même timing relatif).
3. Vérifier en base (`match_event_log`) que les événements ont bien été persistés.

## Scénario 4 — Ghost / joueur absent (le cœur du mécanisme "zéro attente")

1. Téléphone A : jouer normalement son tour.
2. Téléphone B : **ne rien faire** (ou couper le WiFi/4G) jusqu'à expiration du timer de tour.
3. Vérifier :
   - Téléphone A reçoit `opponent_ghosted` et voit l'indicateur "Adversaire absent — IA de
     secours active".
   - Le tour se résout quand même dans les temps (pas d'attente infinie) — les unités de
     l'équipe B sont pilotées par `TacticalAIPlanner` (mouvement, tir, mise à couvert
     cohérents avec le comportement IA déjà connu du mode solo).
   - `match_participants.is_ghosted` passe à `true` pour le joueur B en base.
4. Reconnecter le téléphone B avant le tour suivant : vérifier qu'il reprend la main
   normalement (`is_ghosted` repasse à `false`) sans que le tour Ghost précédent soit remis en
   cause.

## Scénario 5 — Fin de partie

1. Poursuivre les tours jusqu'à élimination totale d'une équipe.
2. Vérifier `match_over` reçu des deux côtés avec le bon `winner_team`.
3. Vérifier `matches.status = 'finished'`, `ended_at` renseigné, en base.

## Ce qu'on observera en priorité (retour d'expérience à collecter)

- Latence perçue entre "FIN TOUR" et le début de l'animation reçue (round-trip + temps de
  simulation serveur).
- Cohérence visuelle entre ce que chaque téléphone affiche pour le même tour (les deux doivent
  voir exactement la même issue, puisque c'est le serveur qui a calculé le résultat une seule
  fois).
- Robustesse du Ghost si la coupure réseau du scénario 4 survient **pendant** l'envoi de
  `submit_turn` plutôt qu'avant (paquet perdu en cours de route) — vérifier que le serveur
  traite bien l'absence de confirmation comme "pas d'ordre reçu" et bascule en Ghost, plutôt que
  de planter en attendant une trame TCP incomplète.
