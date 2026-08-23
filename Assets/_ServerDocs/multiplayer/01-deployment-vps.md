# Déploiement sur le VPS Hetzner

Ce document sera suivi étape par étape quand tu me donneras l'IP, l'utilisateur SSH et le
chemin vers ta clé privée. Chaque étape est vérifiable avant de passer à la suivante — en
particulier les règles de pare-feu, pour ne jamais te couper l'accès SSH par erreur.

## 0. Pré-requis avant de commencer

- Un VPS Hetzner avec Ubuntu 22.04 ou 24.04 LTS.
- Un accès SSH par **clé** (pas de mot de passe root en clair, ni collé dans le chat).
- Un nom de domaine (optionnel mais recommandé pour le TLS Nginx — sinon on utilisera l'IP nue
  en HTTP pour les premiers tests, TLS viendra après).

## 1. Audit initial (lecture seule, aucune modification)

```bash
ssh -i <clé> root@<ip> 'lsb_release -a; free -h; df -h; nproc; ufw status; docker --version || echo "docker absent"'
```

Objectif : connaître l'OS, la RAM/CPU dispo (dimensionnement Supabase + Unity headless), et si
Docker/UFW sont déjà configurés (ne pas écraser une config existante sans regarder).

## 2. Sécurisation de base du système

```bash
apt update && apt upgrade -y
apt install -y fail2ban unattended-upgrades
# Compte non-root dédié au déploiement (éviter de tout faire en root au quotidien)
adduser deploy && usermod -aG sudo deploy
```

- Authentification SSH par clé uniquement : dans `/etc/ssh/sshd_config`,
  `PasswordAuthentication no`, `PermitRootLogin prohibit-password`.
- `fail2ban` protège SSH contre le bruteforce.

## 3. Installation de Docker

```bash
curl -fsSL https://get.docker.com | sh
usermod -aG docker deploy
systemctl enable docker
```

## 4. ⚠️ Le piège UFW + Docker

**Docker manipule iptables directement et contourne les règles UFW par défaut.** Si tu fais
`ufw deny 5432` en pensant fermer Postgres, un conteneur qui publie `5432:5432` reste accessible
depuis l'extérieur malgré UFW — Docker insère ses propres règles dans la chaîne `DOCKER-USER`,
en amont de la chaîne `INPUT` qu'UFW gère.

**La vraie protection : ne jamais publier les ports internes sur l'hôte.** Dans notre
`docker-compose.yml`, seuls `nginx` (80/443) et le serveur de jeu Unity (7777) exposent un port
sur l'hôte. Postgres, GoTrue et PostgREST ne communiquent qu'à l'intérieur du réseau Docker
interne — ils n'ont *aucun* `ports:` dans le compose, donc UFW n'a même pas besoin de les
bloquer.

En complément, on ajoute quand même une règle explicite dans `DOCKER-USER` pour bloquer tout
trafic externe vers le sous-réseau Docker, en clean-up de sécurité :

```bash
iptables -I DOCKER-USER -i eth0 -d <sous-réseau-docker-compose> -j DROP
```

(l'IP exacte du sous-réseau sera déterminée après `docker compose up` avec `docker network inspect`).

## 5. Configuration UFW (pour SSH, HTTP/S, et le port de jeu)

```bash
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 7777/tcp
ufw enable
```

**Ordre important** : on active `ufw allow OpenSSH` et on vérifie que la connexion SSH reste
active dans un second terminal AVANT de faire `ufw enable`, pour ne jamais se retrouver
verrouillé dehors.

## 6. Déploiement de la stack Docker Compose

```bash
mkdir -p /opt/streetact && cd /opt/streetact
# copier docker-compose.yml, .env, nginx/, schema.sql, bootstrap-db.sh, et le build headless Unity
docker compose up -d db
docker compose logs -f db   # attendre "database system is ready to accept connections"

chmod +x bootstrap-db.sh && ./bootstrap-db.sh   # rôles Supabase + migrations GoTrue (voir note ci-dessous)
docker compose exec -T db psql -h 127.0.0.1 -U supabase_admin -d postgres -v ON_ERROR_STOP=1 -f /dev/stdin < schema.sql

docker compose up -d auth rest
docker compose up -d game-server
docker compose up -d nginx
```

On démarre service par service pour repérer immédiatement lequel plante, plutôt que
`docker compose up -d` en un bloc.

**Pourquoi `bootstrap-db.sh` et pas juste `docker compose up -d auth` ?** L'image
`supabase/postgres` utilisée ne pré-crée pas les rôles Supabase (anon/authenticated/
service_role/authenticator/supabase_auth_admin/postgres) — seul `supabase_admin` existe au
démarrage. Plus gênant : le migrateur interne de GoTrue a un bug reproductible (testé sur
v2.170.0 et v2.196.0 le 2026-08-22) où sa migration MFA ne persiste pas ce qu'elle crée,
faisant échouer en boucle toutes les migrations suivantes. `bootstrap-db.sh` crée les rôles
nécessaires puis applique lui-même, via psql, toutes les migrations officielles de GoTrue
(récupérées depuis github.com/supabase/auth) en contournant son migrateur cassé. Une fois fait,
`docker compose up -d auth` démarre directement sans tenter de migration.

## 7. Vérifications après déploiement

```bash
curl -s http://localhost/auth/v1/health
curl -s http://localhost/rest/v1/ -H "apikey: $ANON_KEY"
nc -zv localhost 7777
```

## 8. TLS (Let's Encrypt) — une fois un nom de domaine pointé sur le VPS

```bash
apt install -y certbot python3-certbot-nginx
certbot --nginx -d ton-domaine.com
```

## 8bis. Migrations appliquées manuellement après le déploiement initial

`schema.sql` n'est exécuté qu'au tout premier démarrage de Postgres (`docker-entrypoint-initdb.d`,
ignoré si le volume `db-data` existe déjà). Toute évolution de schéma après coup doit être
appliquée manuellement sur la base déjà déployée via `psql` (voir §3, "Connexion Postgres
directe"), en plus de la mise à jour de `schema.sql` pour les futurs déploiements neufs.

- **2026-08-23** : ajout de `public.matches.mode` (deathmatch / zone_control) —
  `ALTER TABLE public.matches ADD COLUMN IF NOT EXISTS mode text NOT NULL DEFAULT 'deathmatch';`

## 9. Sauvegardes

```bash
# Dump quotidien de la base (cron)
docker compose exec -T db pg_dump -U postgres postgres | gzip > /opt/backups/streetact-$(date +%F).sql.gz
```

À automatiser via une tâche cron sur l'hôte une fois la stack stable.

---

**Rappel de méthode** : quand on exécutera ce runbook ensemble, on avance étape par étape avec
vérification à chaque point de contrôle — en particulier avant `ufw enable` et avant toute
modification touchant Postgres en production.
