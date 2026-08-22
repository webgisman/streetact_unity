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
