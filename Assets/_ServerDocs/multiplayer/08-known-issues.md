# 08 — Limites connues (état au 2026-10-03)

Seulement ce qui est vrai aujourd'hui. L'ancien journal de bord (1900 lignes, 2026-08 à 2026-09) a
été retiré le 2026-10-03 : il reste consultable dans l'historique git.

## Jeu en ligne

- **Pas de retour dans une bataille en cours** : un joueur qui quitte l'application ou perd la
  connexion ne peut pas la rejoindre ; ses unités tiennent leur position (sans IA) jusqu'à la fin.
- **Les deux joueurs d'un siège doivent être en ligne en même temps** pour jouer la bataille ;
  sinon, résolution automatique à l'échéance (6 h) avec les troupes des casernes.
- **Un seul combat à la fois par instance** (scène et NavMesh partagés) : avec 3 instances, au plus
  3 batailles simultanées. Une bataille de siège dont l'instance est occupée attend qu'elle se libère.
- **Choix d'instance d'un siège** : `siege_id % nombre d'instances vivantes`. Si une instance meurt
  ou revient entre les connexions des deux joueurs, ils peuvent se retrouver sur deux instances
  différentes et s'attendre en vain (rare ; annuler puis rejoindre règle le cas).
- **Revenu des quartiers** : lecture puis écriture du solde de PA (pas d'incrément atomique) — un
  achat exactement simultané peut être écrasé. Versé uniquement par `game-server-1` : si cette
  instance est arrêtée, aucun revenu n'est versé.
- **Caserne** : les troupes recrutées ne limitent pas le déploiement d'une bataille jouée (seuls les
  plafonds de comptage s'appliquent) ; elles servent à la résolution automatique d'un siège.
- **Pas de notification push** : les rapports se lisent à l'ouverture de l'application (écran
  Conquête rafraîchi toutes les 15 s).

## Technique

- `TacticalCore` (pathfinding/grille) est testé hors Éditeur ; le moteur de combat réel (UnitAI,
  NavMesh, PhysX) ne l'est qu'en jeu.
- Sauvegardes de la base : non automatisées (voir `01-deployment-vps.md` §9).
- Unity peut réutiliser une ancienne build en cache : avant un déploiement, vérifier la date de
  `build/LinuxServer/NovgovServer_Data/Managed/Assembly-CSharp.dll`.
- Les comptes de test ont reçu beaucoup trop de PA (environ 140 000) à cause du revenu versé trois
  fois (corrigé le 2026-10-03) ; les soldes existants n'ont pas été corrigés.
