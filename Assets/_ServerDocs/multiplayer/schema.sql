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
-- Note sur les écritures : le serveur de jeu Unity headless se connecte à Postgres
-- avec sa propre chaîne de connexion (rôle "postgres", réseau Docker interne, jamais
-- exposé publiquement) et contourne volontairement RLS/PostgREST pour ces écritures
-- de combat — voir 04-unity-headless-server.md. Le client, lui, ne parle jamais
-- directement à Postgres.
-- =========================================================================
