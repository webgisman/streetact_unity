-- Schéma applicatif Novgov.
-- Prérequis : image "supabase/postgres" (déjà fournie dans docker-compose.yml), qui crée
-- automatiquement le schéma "auth" (table auth.users) et les rôles anon/authenticated/
-- service_role/supabase_auth_admin/authenticator. Ce fichier n'ajoute que le schéma "public".

-- =========================================================================
-- 1. Profils joueur (1:1 avec auth.users)
-- =========================================================================
create table public.profiles (
    id uuid primary key references auth.users(id) on delete cascade,
    username text unique not null,
    rating integer not null default 1000,
    created_at timestamptz not null default now()
);

alter table public.profiles enable row level security;

create policy "Un profil est visible par tous les joueurs authentifiés"
    on public.profiles for select
    to authenticated
    using (true);

create policy "Un joueur ne modifie que son propre profil"
    on public.profiles for update
    to authenticated
    using (auth.uid() = id)
    with check (auth.uid() = id);

-- RLS protège la LIGNE (quel profil), pas les COLONNES à l'intérieur de cette ligne. Le rôle
-- "authenticated" reçoit "update" sur TOUTES les colonnes de TOUTES les tables via
-- bootstrap-db.sh ("grant select, insert, update, delete on all tables in schema public to
-- authenticated"), donc sans la restriction ci-dessous, la policy au-dessus n'empêchait pas un
-- joueur connecté d'écrire son propre "rating" directement via PostgREST
-- (PATCH /profiles?id=eq.<son-id> {"rating": 99999}), contournant entièrement le calcul ELO
-- serveur (MatchSessionManager.UpdateRatings). Seul "username" reste modifiable par le joueur ;
-- "rating" ne peut plus être écrit que par service_role (le serveur de jeu).
revoke update on public.profiles from authenticated;
grant update (username) on public.profiles to authenticated;

-- Création automatique du profil à l'inscription
create function public.handle_new_user()
returns trigger as $$
begin
    insert into public.profiles (id, username)
    values (new.id, coalesce(new.raw_user_meta_data->>'username', split_part(new.email, '@', 1)));
    return new;
end;
$$ language plpgsql security definer;

create trigger on_auth_user_created
    after insert on auth.users
    for each row execute procedure public.handle_new_user();

-- =========================================================================
-- 2. Parties (matches)
-- =========================================================================
create type public.match_status as enum ('waiting', 'active', 'finished', 'aborted');

create table public.matches (
    id uuid primary key default gen_random_uuid(),
    status public.match_status not null default 'waiting',
    mode text not null default 'deathmatch',  -- 'deathmatch' ou 'zone_control'
    map_seed text,
    gps_latitude double precision,
    gps_longitude double precision,
    winner_team smallint,               -- 1 ou 2, null tant que non terminé
    created_at timestamptz not null default now(),
    started_at timestamptz,
    ended_at timestamptz
);

alter table public.matches enable row level security;
-- Aucune policy insert/update pour "authenticated" : seul le serveur de jeu
-- (connexion directe Postgres avec le rôle "postgres", hors PostgREST) écrit ici.
-- La policy SELECT est créée plus bas, après la table match_participants dont elle dépend.

-- =========================================================================
-- 3. Participants (un joueur, une équipe, dans une partie)
-- =========================================================================
create table public.match_participants (
    id uuid primary key default gen_random_uuid(),
    match_id uuid not null references public.matches(id) on delete cascade,
    user_id uuid not null references auth.users(id) on delete cascade,
    team_id smallint not null check (team_id in (1, 2)),
    is_ghosted boolean not null default false,   -- true = ses unités sont pilotées par TacticalAIPlanner
    last_seen_at timestamptz not null default now(),
    unique (match_id, user_id)
);

alter table public.match_participants enable row level security;

create policy "Un participant voit sa propre partie"
    on public.matches for select
    to authenticated
    using (
        exists (
            select 1 from public.match_participants mp
            where mp.match_id = matches.id and mp.user_id = auth.uid()
        )
    );

create policy "Un participant voit les participants de sa propre partie"
    on public.match_participants for select
    to authenticated
    using (
        exists (
            select 1 from public.match_participants self
            where self.match_id = match_participants.match_id and self.user_id = auth.uid()
        )
    );

-- =========================================================================
-- 4. Intentions reçues par tour (ce que le client envoie)
-- Reflète directement TacticalPathManager.TacticalNode + NodeAction côté Unity.
-- =========================================================================
create table public.match_turn_orders (
    id bigint generated always as identity primary key,
    match_id uuid not null references public.matches(id) on delete cascade,
    turn_number integer not null,
    team_id smallint not null check (team_id in (1, 2)),
    unit_id text not null,               -- identifiant d'unité côté client (nom GameObject ou GUID)
    node_index integer not null,         -- position dans le tacticalPath de l'unité
    position_x real not null,
    position_y real not null,
    position_z real not null,
    node_action smallint not null,       -- valeur brute de l'enum TacticalPathManager.NodeAction
    received_at timestamptz not null default now()
);

create index on public.match_turn_orders (match_id, turn_number);

alter table public.match_turn_orders enable row level security;

-- CORRECTION (audit sécurité 2026-08-30) : le commentaire d'origine ci-dessous ("pas de RLS,
-- les intentions transitent par TCP pas par PostgREST") était une justification erronée — PostgREST
-- expose AUTOMATIQUEMENT toute table du schéma "public" (PGRST_DB_SCHEMAS: public,
-- docker-compose.yml), quel que soit le chemin d'écriture VOULU. Sans RLS ici, combiné au grant
-- large "insert, update, delete ... to authenticated" de bootstrap-db.sh, n'importe quel joueur
-- authentifié pouvait écrire/modifier/supprimer des lignes pour N'IMPORTE QUELLE partie via un
-- simple appel REST — trouvé et corrigé en production le 2026-08-30 (RLS activée, aucune policy
-- nécessaire puisque personne ne doit écrire ici via PostgREST -> default-deny total). Revoke
-- explicite ci-dessous en défense en profondeur, même pattern que "zones"/"server_instances".
revoke insert, update, delete on public.match_turn_orders from authenticated;

-- Aucune policy select/insert pour "authenticated" : les intentions transitent par le protocole
-- TCP du serveur de jeu (voir 03-network-protocol.md), pas par PostgREST — mais voir la correction
-- ci-dessus : RLS default-deny reste nécessaire pour que cette intention soit réellement appliquée.

-- =========================================================================
-- 5. Log d'événements résolus (ce que le serveur renvoie pour rejouer l'animation)
-- =========================================================================
create table public.match_event_log (
    id bigint generated always as identity primary key,
    match_id uuid not null references public.matches(id) on delete cascade,
    turn_number integer not null,
    timestamp_ms integer not null,       -- offset en ms depuis le début de la phase d'exécution
    event_type text not null,            -- 'move' | 'shot' | 'death' | 'spot' | 'ghost_activated'
    unit_id text,
    target_unit_id text,
    payload jsonb not null default '{}',
    created_at timestamptz not null default now()
);

create index on public.match_event_log (match_id, turn_number);

alter table public.match_event_log enable row level security;

create policy "Un participant voit le log de sa propre partie"
    on public.match_event_log for select
    to authenticated
    using (
        exists (
            select 1 from public.match_participants mp
            where mp.match_id = match_event_log.match_id and mp.user_id = auth.uid()
        )
    );

-- =========================================================================
-- 6. Zones de Conquête (grille Slippy Map fixe, Zoom 17 — voir CityGenerator.ZONE_ZOOM)
-- Remplace l'ancien système de génération GPS à rayon dynamique : chaque Zone est une tuile
-- Slippy Map identifiée par (tile_x, tile_y, zoom), possédée par au plus un joueur à la fois.
-- =========================================================================
create table public.zones (
    tile_x integer not null,
    tile_y integer not null,
    zoom smallint not null,
    owner_user_id uuid references auth.users(id) on delete set null,
    captured_at timestamptz,
    primary key (tile_x, tile_y, zoom)
);

alter table public.zones enable row level security;

create policy "Une Zone est visible par tous les joueurs authentifiés"
    on public.zones for select
    to authenticated
    using (true);

-- Pas de policy insert/update pour "authenticated" : seul le serveur de jeu (connexion directe
-- Postgres avec le rôle "postgres", ou service_role via PostgREST) capture/transfère une Zone,
-- jamais le client directement — voir MatchSessionManager.CaptureZoneInDb.
--
-- IMPORTANT pour le déploiement VPS (vérifié en migrant novgov.com le 2026-08-30, une table créée
-- APRÈS le bootstrap initial) : `ALTER DEFAULT PRIVILEGES FOR ROLE supabase_admin IN SCHEMA public`
-- (mis en place par bootstrap-db.sh) accorde AUTOMATIQUEMENT select/insert/update/delete à
-- "authenticated" sur toute nouvelle table créée par supabase_admin dans public — donc une table
-- migrée après coup avec ce rôle N'A PAS besoin d'un `grant select` manuel, contrairement à ce
-- qu'on pourrait croire. En revanche RLS seul (la policy SELECT ci-dessus) ne suffit pas à
-- restreindre insert/update/delete comme prévu pour les autres tables (RLS bloque bien ces
-- commandes sans policy dédiée, mais par défense en profondeur, resserrer aussi le GRANT lui-même
-- comme pour "profiles" plus haut) :
revoke insert, update, delete on public.zones from authenticated;

-- =========================================================================
-- 7. Registre d'instances du serveur de jeu (pool multi-serveurs, 2026-08-30)
-- Remplace le modèle "un seul serveur pour tout le monde" (portée V1 initiale, un match à la
-- fois) : plusieurs instances du même conteneur game-server tournent en parallèle (voir
-- docker-compose.yml, services game-server-1/2/3), chacune ne gérant toujours qu'UN match à la
-- fois EN INTERNE (le code de simulation n'a pas changé). Chaque instance publie ici son état
-- toutes les 2s (MatchSessionManager.ReportInstanceStatus) ; le client lit cette table AVANT de
-- se connecter pour choisir une instance libre — voir MultiplayerMatchController.
-- PickServerInstance. C'est la même logique qu'une flotte de serveurs de partie (Fortnite,
-- Valorant, etc.) : un matchmaking léger devant un pool de processus de jeu, sans toucher au
-- moteur de simulation lui-même.
-- =========================================================================
create table public.server_instances (
    id text primary key,                       -- ex: 'game-server-1', doit matcher INSTANCE_ID
    public_port integer not null,              -- port réellement utilisé par le client pour se connecter
    status text not null default 'free',       -- 'free' ou 'busy' (un match tourne déjà dessus)
    waiting_deathmatch integer not null default 0,   -- joueurs déjà en file d'attente Deathmatch SUR CETTE instance
    waiting_zone_control integer not null default 0, -- idem pour Zone de Contrôle
    updated_at timestamptz not null default now()    -- passé un certain âge (voir requête client), l'instance
                                                       -- est considérée morte/plantée et ignorée
);

alter table public.server_instances enable row level security;

create policy "Le registre d'instances est visible par tous les joueurs authentifiés"
    on public.server_instances for select
    to authenticated
    using (true);

-- Pas de policy insert/update pour "authenticated" : seule chaque instance de jeu écrit sa PROPRE
-- ligne (service_role via PostgREST) — jamais le client, qui ne fait que lire pour choisir où se
-- connecter.
revoke insert, update, delete on public.server_instances from authenticated;

-- =========================================================================
-- 8. Rythme "async" (5 min / 6h, 2026-09-13) — colonne de pause du match vivant
-- Un match "async" (voir NetMessage.turn_pace) libère la scène/matchInProgress entre deux tours
-- (attente jusqu'à 6h possible) au lieu de la garder occupée comme un match "fast" — sinon un seul
-- match async bloquerait TOUS les autres matchs (tous rythmes/modes confondus) pendant potentiellement
-- des heures, matchInProgress étant un verrou global unique depuis la bascule vers le vrai moteur
-- (voir MatchSessionManager_MatchLive.cs). L'état des vraies UnitAI (position/rotation/PV/type/équipe)
-- est donc sérialisé ici avant destruction des GameObjects, puis relu pour les respawn au tour suivant.
-- =========================================================================
alter table public.matches add column if not exists paused_roster_json jsonb;

-- =========================================================================
-- 9. Notifications (2026-09-13) — "c'est ton tour" pour le rythme async, lu par le client à
-- l'ouverture de l'application (pas de push mobile, voir 11-real-engine-switch-2026-09-13.md) : une
-- notification normale au sens Supabase, une simple ligne dans une table protégée par RLS, jamais un
-- service tiers.
-- =========================================================================
create table public.notifications (
    id bigint generated always as identity primary key,
    user_id uuid not null references auth.users(id) on delete cascade,
    match_id uuid references public.matches(id) on delete cascade,
    type text not null,          -- 'your_turn' | 'match_over' | 'opponent_ghosted'
    message text not null,
    read_at timestamptz,
    created_at timestamptz not null default now()
);

create index on public.notifications (user_id, read_at);

alter table public.notifications enable row level security;

create policy "Un joueur voit ses propres notifications"
    on public.notifications for select
    to authenticated
    using (auth.uid() = user_id);

create policy "Un joueur peut marquer ses propres notifications comme lues"
    on public.notifications for update
    to authenticated
    using (auth.uid() = user_id)
    with check (auth.uid() = user_id);

-- Seul le serveur de jeu (connexion Postgres directe, rôle "postgres") écrit une notification —
-- jamais le client, qui ne fait que lire/marquer comme lue les siennes. Même schéma que
-- "profiles" (§1) : revoke le grant large de bootstrap-db.sh, puis regrant colonne par colonne.
revoke insert, delete on public.notifications from authenticated;
revoke update on public.notifications from authenticated;
grant update (read_at) on public.notifications to authenticated;

-- =========================================================================
-- 10. Économie de Conquête (2026-09-13, demande explicite) — un bâtiment HQ par carte (Zone),
-- des Points d'Action (AP) qui financent l'achat d'unités. Remplace l'ancienne "SupabaseDatabaseClient"
-- qui, malgré son nom, ne lisait/écrivait QUE du PlayerPrefs local à chaque appareil — d'où le bug
-- signalé ("bâtiment jaune chez un joueur, pas chez l'autre") : chaque appareil avait sa propre
-- vérité, jamais partagée. Tout ce qui suit est réellement lu/écrit en base, partagé entre les deux
-- joueurs d'un même match.
-- =========================================================================

-- Un seul bâtiment "HQ" par Zone (tuile), désigné UNE FOIS par le serveur (le plus grand bâtiment
-- de la tuile, voir MatchSessionManager_Conquest.EnsureHqBuildingIndex) au lieu d'un index choisi au
-- hasard côté client (UnityEngine.Random.Range) et jamais partagé. Même valeur lue par les deux
-- clients d'un même match — c'est ce qui corrige l'incohérence visuelle.
alter table public.zones add column if not exists hq_building_index integer;

-- Points d'Action (AP) — la monnaie qui finance l'achat d'unités. Remplace
-- SupabaseDatabaseClient.GetActionPoints/SetActionPoints (PlayerPrefs local). Hérite AUTOMATIQUEMENT
-- du verrou déjà en place sur public.profiles (revoke update + grant(username) seul, voir §1) :
-- une colonne ajoutée après coup sur une table existante ne reçoit PAS de nouveau grant, donc
-- "authenticated" ne peut PAS l'écrire directement par PostgREST — seule la fonction buy_unit()
-- plus bas (SECURITY DEFINER) ou service_role (serveur) peuvent la modifier.
alter table public.profiles add column if not exists action_points integer not null default 100;

-- Caserne réelle (remplace SupabaseDatabaseClient.GetRoster/UpsertRosterItem, PlayerPrefs local).
create table if not exists public.player_roster (
    user_id uuid not null references auth.users(id) on delete cascade,
    unit_type text not null,
    quantity integer not null default 0,
    primary key (user_id, unit_type)
);

alter table public.player_roster enable row level security;

create policy "Un joueur voit son propre roster"
    on public.player_roster for select
    to authenticated
    using (auth.uid() = user_id);

-- Grant SELECT explicite (ne pas se fier uniquement à l'ALTER DEFAULT PRIVILEGES de §"zones" :
-- constaté en migrant novgov.com le 2026-09-13, `player_roster` existait déjà d'un essai précédent,
-- créée sans passer par supabase_admin/hors de ce script — le `create table if not exists`
-- ci-dessus était donc un no-op qui n'a PAS déclenché l'auto-grant, et la table n'avait alors AUCUN
-- privilège pour "authenticated", RLS ou pas — la lecture du roster aurait échoué côté client. D'où
-- ce grant explicite, sûr dans tous les cas — qu'il s'agisse d'une création fraîche ou d'une table
-- déjà là).
grant select on public.player_roster to authenticated;

-- Pas de policy insert/update/delete pour "authenticated" : RLS default-deny + revoke explicite en
-- défense en profondeur (même schéma que "profiles"/"zones"/"notifications" plus haut) — seule la
-- fonction buy_unit() (SECURITY DEFINER, ci-dessous) peut y écrire, jamais un PATCH/POST direct du
-- client.
revoke insert, update, delete on public.player_roster from authenticated;

-- Achat d'unité — transaction ATOMIQUE (verrou de ligne "for update" + une seule transaction SQL) :
-- vérifie le coût (fixé ICI, jamais envoyé par le client — un client modifié ne peut donc jamais
-- payer moins cher), déduit les AP, incrémente la caserne. SECURITY DEFINER : s'exécute avec les
-- droits du propriétaire de la fonction (peut écrire profiles.action_points/player_roster même si
-- "authenticated" n'a lui-même aucun grant d'écriture dessus), mais ne fait jamais que CE calcul
-- précis — pas une porte dérobée générale.
create or replace function public.buy_unit(p_unit_type text)
returns table(new_action_points integer, new_quantity integer)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_user_id uuid := auth.uid();
    v_current_ap integer;
    v_cost integer;
begin
    if v_user_id is null then
        raise exception 'Non authentifié';
    end if;

    v_cost := case p_unit_type
        when 'Fantassin' then 50
        when 'VehiculeCanon' then 150
        when 'CharLeopard' then 300
        when 'Mortier' then 400
        when 'Drone' then 200
        else null
    end;
    if v_cost is null then
        raise exception 'Type d''unité inconnu : %', p_unit_type;
    end if;

    select action_points into v_current_ap from public.profiles where id = v_user_id for update;
    if v_current_ap is null or v_current_ap < v_cost then
        raise exception 'Points d''action insuffisants (% disponibles, % requis)', coalesce(v_current_ap, 0), v_cost;
    end if;

    update public.profiles set action_points = action_points - v_cost where id = v_user_id;

    insert into public.player_roster (user_id, unit_type, quantity)
    values (v_user_id, p_unit_type, 1)
    on conflict (user_id, unit_type) do update set quantity = public.player_roster.quantity + 1;

    return query
        select p.action_points, r.quantity
        from public.profiles p
        join public.player_roster r on r.user_id = p.id and r.unit_type = p_unit_type
        where p.id = v_user_id;
end;
$$;

grant execute on function public.buy_unit(text) to authenticated;

-- Bonus quotidien de connexion (déjà existant côté client, MultiplayerMatchController.
-- GrantDailyActionPoints — cassé par ce chantier puisqu'il écrivait auparavant action_points en
-- PlayerPrefs local ; ne peut plus écrire cette colonne directement une fois verrouillée ci-dessus).
-- Un vrai horodatage serveur (pas un PlayerPrefs local, qu'un joueur pourrait remettre à zéro en
-- réinstallant l'app) empêche un abus trivial de "réclamer plusieurs fois par jour".
alter table public.profiles add column if not exists last_daily_bonus_at timestamptz;

create or replace function public.claim_daily_bonus()
returns table(new_action_points integer, already_claimed boolean)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_user_id uuid := auth.uid();
    v_last timestamptz;
begin
    if v_user_id is null then
        raise exception 'Non authentifié';
    end if;

    select last_daily_bonus_at into v_last from public.profiles where id = v_user_id for update;

    if v_last is not null and v_last::date = now()::date then
        return query select action_points, true from public.profiles where id = v_user_id;
        return;
    end if;

    update public.profiles
    set action_points = action_points + 50, last_daily_bonus_at = now()
    where id = v_user_id;

    return query select action_points, false from public.profiles where id = v_user_id;
end;
$$;

grant execute on function public.claim_daily_bonus() to authenticated;

-- Transfert transactionnel des AP (Conquête/Siège) pour éviter une condition de course
-- (un joueur dépense des AP pendant que le serveur les lit/modifie asynchronement).
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
-- Réservé au serveur de jeu (service_role) : PostgreSQL accorde EXECUTE à PUBLIC par défaut.
revoke execute on function public.pillage_action_points(uuid, uuid) from public, anon, authenticated;
grant execute on function public.pillage_action_points(uuid, uuid) to service_role;

-- =========================================================================
-- 11. Sièges de Zone — PvP asynchrone (2026-09-13, demande explicite : "les joueurs vont vouloir
-- attaquer une map déjà conquise... le joueur doit être notifié et organiser tout cela").
--
-- Principe : attaquer une Zone NEUTRE reste inchangé (capture instantanée, voir §6/Conquête plus
-- haut). Attaquer une Zone déjà possédée par un AUTRE joueur passe maintenant par un vrai siège en
-- 2 temps au lieu d'un combat instantané contre une garnison IA :
--   1. start_siege() (ci-dessous) : déclare le siège, notifie le défenseur, ouvre une fenêtre de
--      6h. L'attaquant se connecte ensuite au serveur de jeu pour DÉPLOYER ses unités (comme un
--      déploiement de Conquête classique) — ce déploiement est capturé (position/type de chaque
--      unité) et stocké ici, PAS résolu immédiatement.
--   2. Si le défenseur se connecte dans les 6h et choisit de défendre, il déploie À SON TOUR sa
--      garnison (son VRAI roster, avec la liberté de composition) — dès que les deux déploiements
--      sont présents, le serveur résout IMMÉDIATEMENT le combat (vrai moteur Unity, voir
--      MatchSessionManager_Siege.cs). Sinon, à l'expiration des 6h, le serveur génère
--      automatiquement une défense à partir du roster ACTUEL du défenseur (même mécanisme que
--      l'ancienne Conquête instantanée) et résout le combat tout seul, sans qu'aucun des deux
--      joueurs n'ait besoin d'être connecté à cet instant précis.
-- Dans tous les cas, le résultat est notifié aux DEUX joueurs (table "notifications" ci-dessus).
-- =========================================================================

-- Niveau de bâtiment (1 à 3) : investissement du propriétaire dans SA Zone (upgrade_building()
-- ci-dessous) — augmente à la fois le revenu passif (voir le calcul d'intérêt de ZoneIncomeLoop,
-- désormais pondéré par ce niveau) ET la force de la garnison auto-générée en cas de siège non
-- défendu en direct (voir MatchSessionManager_Siege.ResolveSiegeNow). C'est le levier
-- "organiser/renforcer son économie" demandé : un joueur qui investit ses AP dans SES bâtiments
-- plutôt que dans l'expansion devient mécaniquement plus dur à déloger.
alter table public.zones add column if not exists building_level integer not null default 1;

-- Bouclier de grâce : une Zone qui vient d'être attaquée (gagnée OU perdue) ne peut plus être
-- assiégée à nouveau avant cette date — sans ça, une Zone fraîchement conquise (ou fraîchement
-- défendue avec succès, roster probablement affaibli) pouvait être ré-attaquée en boucle par
-- n'importe qui, sans la moindre chance de se réorganiser entre deux sièges.
alter table public.zones add column if not exists shield_until timestamptz;

create table public.zone_sieges (
    id bigint generated always as identity primary key,
    tile_x integer not null,
    tile_y integer not null,
    zoom smallint not null,
    attacker_user_id uuid not null references auth.users(id) on delete cascade,
    defender_user_id uuid not null references auth.users(id) on delete cascade,
    -- 'resolving' (2026-09-19, rapport d'audit §1) : réservation atomique le temps de la résolution
    -- (voir MatchSessionManager_Siege.ResolveSiegeNow) — sans cet état intermédiaire, deux instances
    -- du pool recevant chacune un déploiement au même instant pouvaient toutes les deux lire encore
    -- 'pending' et déclencher une résolution en double.
    status text not null default 'pending', -- 'pending' | 'resolving' | 'resolved'
    -- Chacun un objet {"units":[{unit_id,unit_type,team_id,x,y,z}, ...]} — même structure que
    -- Network.DeployedUnit côté client/serveur — jamais un tableau JSON nu au premier niveau
    -- (limitation de JsonUtility côté Unity, voir MatchSessionManager_AsyncPause.paused_roster_json
    -- pour le même choix déjà fait ailleurs dans ce schéma).
    attacker_deployment_json jsonb,
    defender_deployment_json jsonb,
    deadline timestamptz not null,
    winner_user_id uuid references auth.users(id),
    created_at timestamptz not null default now(),
    resolved_at timestamptz
);

create index on public.zone_sieges (status, deadline);
create index on public.zone_sieges (defender_user_id, status);
create index on public.zone_sieges (attacker_user_id, status);

alter table public.zone_sieges enable row level security;

create policy "Un joueur voit les sièges où il est attaquant ou défenseur"
    on public.zone_sieges for select
    to authenticated
    using (auth.uid() = attacker_user_id or auth.uid() = defender_user_id);

-- Seul le serveur de jeu (service_role, écrit les déploiements/la résolution) ou la fonction
-- start_siege() ci-dessous (SECURITY DEFINER) peuvent écrire ici — jamais un PATCH/POST direct du
-- client, même pour ses propres sièges (un client modifié pourrait sinon s'auto-déclarer vainqueur).
revoke insert, update, delete on public.zone_sieges from authenticated;

-- Déclare un siège sur une Zone déjà possédée par un autre joueur. Vérifie l'absence de bouclier
-- actif et l'absence d'un siège déjà en cours sur cette même Zone (un seul siège actif à la fois
-- par Zone, dans l'ordre d'arrivée) avant d'insérer la ligne et de notifier le défenseur.
create or replace function public.start_siege(p_tile_x integer, p_tile_y integer, p_zoom smallint)
returns table(new_siege_id bigint, siege_deadline timestamptz)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_attacker uuid := auth.uid();
    v_owner uuid;
    v_shield timestamptz;
    v_existing bigint;
    v_deadline timestamptz;
begin
    if v_attacker is null then
        raise exception 'Non authentifié';
    end if;

    select owner_user_id, shield_until into v_owner, v_shield
        from public.zones where tile_x = p_tile_x and tile_y = p_tile_y and zoom = p_zoom
        for update;

    if v_owner is null then
        raise exception 'Zone neutre : capturez-la directement, pas besoin de siège';
    end if;
    if v_owner = v_attacker then
        raise exception 'Vous possédez déjà cette zone';
    end if;
    if v_shield is not null and v_shield > now() then
        raise exception 'Zone protégée jusqu''à %', v_shield;
    end if;

    -- 'resolving' inclus (2026-09-19) : une résolution en cours (voir ResolveSiegeNow) ne doit pas
    -- laisser cette fenêtre, même brève, ouvrir un second siège sur la même Zone.
    select id into v_existing from public.zone_sieges
        where tile_x = p_tile_x and tile_y = p_tile_y and zoom = p_zoom and status in ('pending', 'resolving');
    if v_existing is not null then
        raise exception 'Un siège est déjà en cours sur cette zone';
    end if;

    v_deadline := now() + interval '6 hours';
    insert into public.zone_sieges (tile_x, tile_y, zoom, attacker_user_id, defender_user_id, deadline)
        values (p_tile_x, p_tile_y, p_zoom, v_attacker, v_owner, v_deadline)
        returning id into new_siege_id;
    siege_deadline := v_deadline;

    insert into public.notifications (user_id, type, message)
        values (v_owner, 'under_attack',
            'Votre territoire est assiégé ! Défendez-le dans les 6 prochaines heures ou votre garnison actuelle la défendra automatiquement.');

    return next;
end;
$$;

grant execute on function public.start_siege(integer, integer, smallint) to authenticated;

-- Améliore le bâtiment de la Zone (niveau 1 -> 2 -> 3), coût croissant en AP. SECURITY DEFINER :
-- action_points/zones.building_level ne sont pas directement modifiables par le client (voir les
-- revoke plus haut/plus bas), seule cette fonction peut les changer ensemble, atomiquement.
create or replace function public.upgrade_building(p_tile_x integer, p_tile_y integer, p_zoom smallint)
returns table(new_action_points integer, new_level integer)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_user uuid := auth.uid();
    v_owner uuid;
    v_level integer;
    v_cost integer;
    v_ap integer;
begin
    if v_user is null then
        raise exception 'Non authentifié';
    end if;

    select owner_user_id, building_level into v_owner, v_level
        from public.zones where tile_x = p_tile_x and tile_y = p_tile_y and zoom = p_zoom
        for update;

    if v_owner is null or v_owner != v_user then
        raise exception 'Vous ne possédez pas cette zone';
    end if;
    if v_level >= 3 then
        raise exception 'Niveau de bâtiment maximum déjà atteint';
    end if;

    v_cost := case v_level when 1 then 200 when 2 then 500 else 999999 end;

    select action_points into v_ap from public.profiles where id = v_user for update;
    if v_ap is null or v_ap < v_cost then
        raise exception 'Points d''action insuffisants (% disponibles, % requis)', coalesce(v_ap, 0), v_cost;
    end if;

    update public.profiles set action_points = action_points - v_cost where id = v_user;
    update public.zones set building_level = v_level + 1
        where tile_x = p_tile_x and tile_y = p_tile_y and zoom = p_zoom;

    new_action_points := v_ap - v_cost;
    new_level := v_level + 1;
    return next;
end;
$$;

grant execute on function public.upgrade_building(integer, integer, smallint) to authenticated;

-- =========================================================================
-- Note sur les écritures : le serveur de jeu Unity headless se connecte à Postgres
-- avec sa propre chaîne de connexion (rôle "postgres", réseau Docker interne, jamais
-- exposé publiquement) et contourne volontairement RLS/PostgREST pour ces écritures
-- de combat — voir 04-unity-headless-server.md. Le client, lui, ne parle jamais
-- directement à Postgres.
-- =========================================================================
