-- Schéma applicatif StreetAct.
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
    using (auth.uid() = id);

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

-- Pas de RLS select/insert pour "authenticated" : les intentions transitent par le protocole
-- TCP du serveur de jeu (voir 03-network-protocol.md), pas par PostgREST.

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
-- Note sur les écritures : le serveur de jeu Unity headless se connecte à Postgres
-- avec sa propre chaîne de connexion (rôle "postgres", réseau Docker interne, jamais
-- exposé publiquement) et contourne volontairement RLS/PostgREST pour ces écritures
-- de combat — voir 04-unity-headless-server.md. Le client, lui, ne parle jamais
-- directement à Postgres.
-- =========================================================================
