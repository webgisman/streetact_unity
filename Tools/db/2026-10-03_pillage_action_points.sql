-- Migration du 2026-10-03 : la fonction de pillage des Points d'Action (schema.sql §10) n'a jamais été
-- créée sur la base de production (schema.sql ne s'exécute qu'à la première installation). Sans elle,
-- chaque siège gagné échoue à piller : "PGRST202 ... public.pillage_action_points" dans les journaux
-- du serveur de jeu. Réservée au serveur de jeu (service_role).
--
-- Sur le VPS :
--   cd /opt/novgov && sudo docker compose exec -T db psql -U postgres -d postgres < 2026-10-03_pillage_action_points.sql
\set ON_ERROR_STOP on
begin;

create or replace function public.pillage_action_points(attacker_id uuid, defender_id uuid)
returns table(stolen_ap integer, attacker_new_ap integer)
language plpgsql
as $$
declare
    v_def_ap integer;
    v_att_ap integer;
begin
    -- Verrouiller les deux profils dans un ordre déterministe pour éviter les deadlocks
    if attacker_id < defender_id then
        select action_points into v_att_ap from public.profiles where id = attacker_id for update;
        select action_points into v_def_ap from public.profiles where id = defender_id for update;
    else
        select action_points into v_def_ap from public.profiles where id = defender_id for update;
        select action_points into v_att_ap from public.profiles where id = attacker_id for update;
    end if;

    if v_def_ap is null then v_def_ap := 0; end if;
    if v_att_ap is null then v_att_ap := 0; end if;

    if v_def_ap > 0 then
        update public.profiles set action_points = 0 where id = defender_id;
        update public.profiles set action_points = action_points + v_def_ap where id = attacker_id;
    end if;

    return query select v_def_ap, v_att_ap + v_def_ap;
end;
$$;
revoke execute on function public.pillage_action_points(uuid, uuid) from public, anon, authenticated;
grant execute on function public.pillage_action_points(uuid, uuid) to service_role;

commit;
notify pgrst, 'reload schema';
