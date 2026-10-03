# Novgov — documentation technique

Novgov est un jeu tactique au tour par tour sur la carte de vraies villes (OpenStreetMap) :
- **Solo** : une partie hors ligne contre l'ordinateur ;
- **En ligne** : la Conquête des quartiers de sa ville — prendre les quartiers libres, gagner des
  Points d'Action, recruter, et prendre ceux des autres joueurs par des **sièges joués au tour par
  tour** entre les deux joueurs. Pas d'IA en multijoueur.

| Document | Contenu |
|---|---|
| [00-architecture.md](00-architecture.md) | **À lire en premier** — le jeu, les règles en ligne, le serveur, le client, les données |
| [01-deployment-vps.md](01-deployment-vps.md) | VPS `novgov.com` : installation complète, mise à jour du serveur de jeu, retour arrière, sauvegardes |
| [02-auth-supabase-unity.md](02-auth-supabase-unity.md) | Authentification (Supabase GoTrue) côté Unity |
| [03-network-protocol.md](03-network-protocol.md) | Protocole TCP client ↔ serveur (capture, bataille de siège) |
| [06-security-checklist.md](06-security-checklist.md) | Sécurité : audits et points ouverts |
| [07-tests.md](07-tests.md) | Tests automatiques, test à 2 joueurs dans l'Éditeur, test de bout en bout, captures d'écran |
| [08-known-issues.md](08-known-issues.md) | Limites connues aujourd'hui |
| `schema.sql`, `bootstrap-db.sh`, `docker-compose.yml`, `nginx/`, `game-server/` | Base de données et infrastructure (copies de ce qui tourne sur le VPS) |

Règles techniques du moteur (génération de ville, NavMesh, import des animations) : `Assets/Docs/`.
Identifiants d'accès : `CREDENTIALS.md` à la racine du projet (local, jamais versionné).

**État au 2026-10-03** : serveur de jeu en production sur 3 instances avec la bataille de siège au
tour par tour ; Match à mort, Contrôle de zone, Entraînement en ligne et rythme asynchrone ont été
retirés du jeu et du code. L'historique complet des sessions de développement est dans git.
