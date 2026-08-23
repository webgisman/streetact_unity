# Checklist sécurité

À vérifier avant, pendant et après le déploiement — chaque case sera cochée pendant le runbook
([01-deployment-vps.md](01-deployment-vps.md)).

## Système / SSH

- [ ] Connexion SSH par clé uniquement (`PasswordAuthentication no`).
- [ ] `PermitRootLogin prohibit-password` (pas de login root par mot de passe).
- [ ] `fail2ban` actif sur le service SSH.
- [ ] Mises à jour automatiques de sécurité (`unattended-upgrades`).
- [ ] Un utilisateur non-root (`deploy`) pour les opérations courantes.

## Réseau / Pare-feu

- [ ] UFW actif, avec `OpenSSH` autorisé **avant** `ufw enable` (jamais l'inverse).
- [ ] Seuls les ports 80, 443 et 7777 ouverts publiquement.
- [ ] **Aucun** `ports:` publié sur l'hôte pour `db`, `auth`, `rest` dans `docker-compose.yml`
      (vérifié : voir le fichier fourni — ces trois services n'ont pas de section `ports:`).
- [ ] Vérification post-déploiement : `docker compose ps` puis `ss -tlnp` sur l'hôte pour
      confirmer qu'aucun port inattendu n'écoute sur `0.0.0.0`.
- [ ] Comprendre le piège UFW+Docker (détaillé dans 01-deployment-vps.md §4) — ne jamais
      supposer qu'une règle `ufw deny` protège un port publié par Docker.

## Secrets

- [ ] `.env` réel jamais commité dans un dépôt git (`.gitignore` à vérifier si un repo est
      initialisé plus tard).
- [ ] `JWT_SECRET` généré aléatoirement (`openssl rand -base64 48`), pas la valeur d'exemple.
- [ ] `POSTGRES_PASSWORD` fort et unique à ce déploiement.
- [ ] La chaîne de connexion Postgres du serveur de jeu (`GAME_DB_CONNECTION`) n'est **jamais**
      envoyée au client — elle reste uniquement dans l'environnement du conteneur
      `game-server`.
- [ ] `ANON_KEY` (publique côté client, normal) et `SERVICE_ROLE_KEY` (jamais côté client,
      contourne RLS) bien distinguées — ne jamais embarquer `SERVICE_ROLE_KEY` dans le build
      Unity du téléphone.

## Base de données

- [ ] RLS (`Row Level Security`) activée sur toutes les tables exposées via PostgREST
      (`profiles`, `matches`, `match_participants`, `match_event_log` — fait dans
      `schema.sql`).
- [ ] Le serveur de jeu (qui bypass RLS via une connexion directe rôle `postgres`) est le seul
      composant qui écrit dans `match_turn_orders` et `match_event_log` — le client ne peut pas
      falsifier un résultat de combat car il n'a jamais d'accès en écriture direct à ces tables.
- [ ] Sauvegardes automatiques (`pg_dump` quotidien, voir 01-deployment-vps.md §9).

## Application

- [ ] Le serveur de jeu vérifie la signature JWT de chaque connexion avant d'accepter des
      messages autres que `auth`.
- [ ] Rate limiting basique sur `/auth/v1/` (déjà dans `nginx.conf` fourni,
      `limit_req_zone`) pour limiter le bruteforce de mots de passe.
- [ ] Le serveur ne fait *jamais* confiance aux positions/dégâts envoyés par le client — seules
      les *intentions* (`submit_turn`) sont reçues, tout le calcul de résultat est refait
      côté serveur à partir de l'état serveur, jamais à partir de valeurs fournies par le
      client.

## TLS

- [ ] Certificat Let's Encrypt en place avant toute utilisation avec de vrais comptes
      utilisateurs (mots de passe en clair sur HTTP = inacceptable, même pour un test).
- [ ] Le port 7777 (jeu) reste en clair sur TCP pour l'instant — acceptable pour le test à 2
      téléphones vu qu'aucune donnée de compte n'y transite (seul le JWT déjà émis via HTTPS y
      circule), mais à chiffrer (TLS sur le socket, ou tunneling) avant une diffusion plus
      large.

## Audit de production (2026-08-23) — corrigés

Les points ci-dessous ont été trouvés lors d'un audit complet avant mise en production et déjà
corrigés (code + base de données live) :

- [x] **`profiles.rating` modifiable directement par un client authentifié via PostgREST**
      (`PATCH /profiles?id=eq.<son-id> {"rating": 99999}`) — RLS protège la ligne mais pas les
      colonnes ; le rôle `authenticated` avait `update` sur toutes les colonnes de `profiles` via
      le `grant` générique de `bootstrap-db.sh`. Corrigé dans `schema.sql` (colonne restreinte à
      `username` uniquement, `rating` réservé à `service_role`) et appliqué en direct sur la base
      de production le 2026-08-23.
- [x] **Déni de service pré-authentification sur le port 7777** — un socket non authentifié
      pouvait annoncer une longueur de message jusqu'à 8 Mo (la limite gameplay normale) avant
      même l'envoi du JWT, permettant d'épuiser la RAM du VPS avec quelques centaines de
      connexions. Corrigé : `NetFraming.ReadMessage` accepte maintenant un plafond de taille
      différent selon le contexte, `GameServerBootstrap.HandleHandshake` l'utilise avec un
      plafond de 8 Ko (largement suffisant pour un message `auth`).
- [x] **Un message de tour malformé pouvait bloquer le serveur définitivement** — aucune
      exception n'était rattrapée pendant le traitement d'un match (`RunMatch`/
      `ApplyOrdersToUnits`), et `matchInProgress` restait bloqué à `true` pour toujours en cas
      d'exception, empêchant tout nouveau match de démarrer, sans crash ni redémarrage Docker
      possible (aucun healthcheck). Corrigé : `RunMatchGuarded` encapsule l'énumérateur du match
      et garantit l'abandon propre (match nul + fermeture des connexions +
      `matchInProgress = false`) sur toute exception ; `ApplyOrdersToUnits` valide maintenant les
      coordonnées (rejette NaN/Infinity), l'action (doit être une valeur valide de l'enum
      `NodeAction`) et la longueur du chemin (plafond 200 points) avant utilisation.
- [x] **Deathmatch sans plafond de tours** — contrairement à la Zone de Contrôle, deux joueurs
      qui se cachaient indéfiniment bloquaient l'unique emplacement de match du serveur pour
      toujours. Corrigé : plafond de 60 tours, départage par unités vivantes puis PV totaux,
      égalité parfaite = match nul.

## Audit de production (2026-08-23) — restant à faire avant diffusion large

Trouvés lors du même audit, pas encore corrigés (pas critiques, mais à traiter avant une
diffusion au-delà de tests restreints) :

- [ ] **Pas de timeout d'écriture socket côté serveur, pas de TCP keepalive.**
      `client.ReceiveTimeout` repasse à `0` (infini) après l'authentification
      (`GameServerBootstrap.cs`), et rien n'est configuré côté envoi. Si un client mobile perd le
      réseau ou s'endort sans fermeture propre (FIN/RST), et que son tampon socket se remplit,
      `PlayerConnection.Send()` (appelé depuis le thread principal, ex. le tick `turn_timer`
      chaque seconde) peut bloquer indéfiniment — gelant tout le serveur puisqu'un seul match
      tourne à la fois. Fix : fixer `SendTimeout` et un `ReceiveTimeout` raisonnable (30-60s)
      même après l'auth, traiter un timeout comme une déconnexion ; activer
      `SocketOptionName.KeepAlive` à l'acceptation.
- [ ] **Aucune limite de connexions/débit sur le port 7777 (type Slowloris).**
      `AcceptLoop` crée un thread par connexion entrante sans plafond, et rien ne limite le
      nombre de connexions par IP. Un client peut garder un socket ouvert indéfiniment en
      n'envoyant rien (ou en gardant le flux juste sous le timeout de 10s du handshake). Fix :
      plafonner les handshakes concurrents (ex. sémaphore ~50) et compter les connexions par IP.
- [ ] **Aucun healthcheck Docker sur `game-server`**, et pas de limite mémoire/CPU dans
      `docker-compose.yml`. Combiné aux deux points ci-dessus, un serveur "gelé mais vivant" ne
      redémarre jamais automatiquement. Fix : ajouter un healthcheck applicatif (ex. fichier de
      vivacité mis à jour à chaque tick `Update()`, ou auto-connexion TCP périodique) et des
      limites `mem_limit`/`cpus`.
- [ ] **Échec silencieux de l'écriture du rating.** Si le `PATCH /profiles` échoue (blip
      réseau), `MatchSessionManager.UpdateRatings` a déjà pré-calculé et stocké le nouveau
      rating/delta sur `PlayerConnection` avant de tenter l'écriture — le message `match_over`
      part avec ces valeurs même si l'écriture échoue, donc le client peut afficher "+15" alors
      que la base garde l'ancienne valeur. De même, un échec de la lecture GET précédente laisse
      `NewRating`/`RatingDelta` à leur valeur par défaut `0`, envoyée telle quelle. Fix : ne
      peupler/envoyer ces champs qu'après confirmation d'écriture réussie, ou exposer un indicateur
      explicite d'indisponibilité plutôt que la valeur par défaut `0`.
- [ ] **Un mode de matchmaking invalide/mal orthographié tombe silencieusement en Deathmatch**
      (`MatchSessionManager.cs`, aucune liste blanche de modes acceptés) — pas de crash, mais
      aucune erreur n'est renvoyée si un futur client envoie une valeur inattendue.
