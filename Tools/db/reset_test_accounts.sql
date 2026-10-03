-- Remet les deux comptes de test (Joueur 1 = TestLille1, Joueur 2 = TestLille2 — voir
-- Assets/Scripts/Network/EditorTestPlayers.cs) dans leur état de départ, pour rejouer un siège :
--   - supprime les parties jouées UNIQUEMENT entre eux (participants, ordres, journal : en cascade),
--   - supprime leurs sièges l'un contre l'autre et tous leurs rapports (notifications),
--   - rend à chacun son quartier de départ, sans protection de 6 h,
--   - remet leur classement à 1000.
-- Ne touche à aucun autre joueur. Réutilisable après chaque essai.
--
-- Sur le VPS :
--   cd /opt/novgov && sudo docker compose exec -T db psql -U postgres -d postgres < reset_test_accounts.sql
\set ON_ERROR_STOP on
begin;

create temp table test_players(id uuid primary key, tile_x int, tile_y int) on commit drop;
insert into test_players values
  ('5a8c12a9-b9f0-42b7-be3a-8f06c72382ec', 66648, 44111),  -- Joueur 1 (TestLille1)
  ('3664923c-374e-40a2-b18e-42b7aa45f2bf', 66648, 44110);  -- Joueur 2 (TestLille2)

delete from public.matches m
 where exists (select 1 from public.match_participants p where p.match_id = m.id)
   and not exists (select 1 from public.match_participants p
                    where p.match_id = m.id and p.user_id not in (select id from test_players));

delete from public.zone_sieges
 where attacker_user_id in (select id from test_players)
   and defender_user_id in (select id from test_players);

delete from public.notifications where user_id in (select id from test_players);

update public.zones z set owner_user_id = t.id, shield_until = null, captured_at = now()
  from test_players t
 where z.tile_x = t.tile_x and z.tile_y = t.tile_y and z.zoom = 17;

update public.profiles set rating = 1000 where id in (select id from test_players);

-- Contrôle
select z.tile_x, z.tile_y, p.username, z.building_level, z.shield_until
  from public.zones z join public.profiles p on p.id = z.owner_user_id
 where (z.tile_x, z.tile_y) in (select tile_x, tile_y from test_players) and z.zoom = 17
 order by z.tile_y;

commit;
