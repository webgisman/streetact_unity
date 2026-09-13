# Déploiement en production (novgov.com) — 2026-09-13

**Fait, sur demande explicite de l'utilisateur** ("appliquer directement sur novgov.com, production"),
après l'avoir prévenu que ce code n'a jamais été testé avec de vrais clients. Ce document ne remplace
pas [09](09-real-unity-combat-investigation-2026-09-13.md)/[10](10-deathmatch-zonecontrol-switched-to-live-2026-09-13.md)/[11](11-real-engine-async-pace-notifications-2026-09-13.md) —
il consigne ce qui a RÉELLEMENT été appliqué sur le VPS et vérifié, pas seulement écrit.

## Ce qui a été fait

1. **Migration base de données appliquée** (via `psql` en SSH, connecté en `supabase_admin`) :
   - `alter table public.matches add column if not exists paused_roster_json jsonb;`
   - Table `public.notifications` (RLS activée, policies select/update, grants resserrés).
   - **Vérifié après coup** (pas supposé) : `\d public.matches`/`\d public.notifications` montrent
     bien la colonne/la table ; `information_schema.role_column_grants` confirme que le rôle
     `authenticated` n'a que SELECT sur toutes les colonnes de `notifications` + UPDATE sur `read_at`
     seule — exactement le schéma voulu, pas un grant plus large laissé par erreur.
2. **Nouveau binaire serveur déployé** (`NovgovServer.x86_64`/`NovgovServer_Data`/`UnityPlayer.so`,
   build local vérifié par MD5 identique après transfert) sur les 3 instances.
3. **Pool de 3 instances rétabli** (`docker-compose.yml` copié sur le VPS, `docker compose build` +
   `up -d` fait un service à la fois — `game-server-1/2/3`, ports 7777/7778/7779).
4. **Pare-feu UFW** : ports 7778/7779 rouverts (`ufw allow`).
5. **Vérifié après déploiement** : les 7 conteneurs `Up`/`healthy`, aucune exception dans les logs des
   3 instances de jeu, `nc -zv localhost 777{7,8,9}` réussi sur le VPS lui-même, et la table
   `server_instances` montre bien 3 lignes fraîches (`game-server-1/2/3`, statut `free`, horodatage à
   l'instant du déploiement).

## Ce qui N'A PAS été vérifié — à ne pas confondre avec "ça marche"

**Déployer proprement (ça démarre, ça écoute, la base est cohérente) n'est pas la même chose que
"le jeu fonctionne correctement".** Rien de tout ce qui suit n'a été testé dans cette session, faute
de deuxième appareil/client réel :
- Un vrai match Deathmatch/Zone de Contrôle/Conquête avec le nouveau moteur temps réel (mouvement
  NavMesh, tir `Physics.RaycastAll`, dégâts) — jamais joué de bout en bout.
- Le cycle pause/reprise du rythme "async" (sérialisation → destruction → attente → respawn) —
  jamais exécuté une seule fois en conditions réelles, y compris la restauration de
  garnison/fenêtre/intérieur/zone de capture.
- L'écriture réelle d'une notification et sa lecture (aucun code client ne la lit encore).
- Le comportement à 2/3 matchs réellement simultanés sur le pool de 3 instances.

**Donc** : le déploiement lui-même est propre et vérifié, mais la correction du GAMEPLAY reste
entièrement à valider — ce n'est qu'après un vrai test à 2 joueurs que "ça marche" pourra être
affirmé, pas avant.

## Rollback si un problème réel est découvert en jouant

- Revenir à l'ancien binaire : les fichiers précédents n'ont pas été sauvegardés séparément sur le
  VPS avant l'écrasement (leçon pour la prochaine fois : `cp -a` un backup daté du dossier
  `game-server/` avant d'écraser) — il faudrait rebuild depuis un commit antérieur de cette branche,
  ou depuis `master`, et redéployer par-dessus avec la même procédure.
- La colonne `paused_roster_json`/la table `notifications` peuvent rester en base sans risque même si
  on revient à l'ancien binaire (colonnes/tables inertes si le code qui les utilise n'est plus actif).
- Le pool à 3 instances peut être ramené à 1 en supprimant `game-server-2`/`game-server-3` du
  `docker-compose.yml` et `docker compose up -d --remove-orphans`.
