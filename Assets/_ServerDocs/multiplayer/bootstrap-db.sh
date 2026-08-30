#!/usr/bin/env bash
# Bootstrap complet de la base Postgres pour GoTrue + PostgREST, à exécuter UNE FOIS
# juste après "docker compose up -d db" et AVANT "docker compose up -d auth".
#
# Pourquoi ce script existe : l'image "supabase/postgres" utilisée ici ne pré-crée PAS les
# rôles Supabase standards (anon/authenticated/service_role/authenticator/supabase_auth_admin)
# ni le rôle "postgres" — seul "supabase_admin" existe au premier démarrage. De plus, le
# migrateur interne de GoTrue (v2.170.0 ET v2.196.0, testé le 2026-08-22) a un bug reproductible :
# sa migration "20221003041349_add_mfa_schema.up.sql" ne persiste pas les types/tables qu'elle
# est censée créer (auth.factor_type, auth.mfa_factors, etc.), ce qui fait échouer toutes les
# migrations suivantes qui en dépendent, en boucle infinie de redémarrage. Contournement :
# on applique nous-mêmes TOUTES les migrations officielles de GoTrue (récupérées depuis le
# dépôt GitHub supabase/auth) via psql, puis on marque leurs versions comme "déjà appliquées"
# dans auth.schema_migrations pour que GoTrue démarre directement sans repasser par son
# migrateur cassé.
#
# Usage : ./bootstrap-db.sh   (à lancer sur le VPS, dans /opt/novgov, après "docker compose up -d db")

set -euo pipefail
cd "$(dirname "$0")"

PGPASS=$(grep ^POSTGRES_PASSWORD .env | cut -d= -f2-)
PSQL="docker compose exec -T -e PGPASSWORD=$PGPASS db psql -h 127.0.0.1 -U supabase_admin -d postgres"

echo "==> 1. Rôles Supabase + schéma auth + extension pgcrypto"
$PSQL -v ON_ERROR_STOP=1 <<'SQL'
create extension if not exists pgcrypto;

do $$ begin
  if not exists (select 1 from pg_roles where rolname = 'postgres') then
    create role postgres superuser login;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'anon') then
    create role anon nologin noinherit;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then
    create role authenticated nologin noinherit;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'service_role') then
    create role service_role nologin noinherit bypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticator') then
    create role authenticator noinherit login;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'supabase_auth_admin') then
    create role supabase_auth_admin noinherit createrole login;
  end if;
end $$;

grant anon to authenticator;
grant authenticated to authenticator;
grant service_role to authenticator;
grant create on database postgres to supabase_auth_admin;
grant create on schema public to supabase_auth_admin;
alter role supabase_auth_admin set search_path to auth, public;

create schema if not exists auth authorization supabase_auth_admin;
grant usage on schema auth to anon, authenticated, service_role;
grant usage on schema public to anon, authenticated, service_role;
grant all on all tables in schema public to service_role;
grant select, insert, update, delete on all tables in schema public to authenticated;
alter default privileges in schema public grant select, insert, update, delete on tables to authenticated;
alter default privileges in schema public grant all on tables to service_role;
SQL

echo "==> 2. Mots de passe des rôles applicatifs (alignés sur POSTGRES_PASSWORD, voir .env)"
$PSQL -v ON_ERROR_STOP=1 -v pw="$PGPASS" <<'SQL'
alter user authenticator with password :'pw';
alter user supabase_auth_admin with password :'pw';
SQL

echo "==> 3. Téléchargement + application des migrations officielles GoTrue (contournement du bug)"
WORKDIR=$(mktemp -d)
cd "$WORKDIR"
curl -s 'https://api.github.com/repos/supabase/auth/contents/migrations' \
  | python3 -c "
import json,sys
d=json.load(sys.stdin)
for x in sorted(d, key=lambda r: r['name']):
    if x['name'].endswith('.up.sql'): print(x['name'])
" > filelist.txt

while read -r f; do
  curl -s "https://raw.githubusercontent.com/supabase/auth/master/migrations/$f" -o "$f"
done < filelist.txt

: > combined.sql
while read -r f; do
  sed -e 's/{{ index .Options "Namespace" }}/auth/g' -e 's/{{index .Options "Namespace" }}/auth/g' "$f" >> combined.sql
  echo ';' >> combined.sql
done < filelist.txt

python3 -c "
import re
versions=[]
with open('filelist.txt') as f:
    for line in f:
        m=re.match(r'^([0-9]+)_', line.strip())
        if m: versions.append(m.group(1))
with open('insert_versions.sql','w') as out:
    out.write('insert into auth.schema_migrations (version) values\n')
    out.write(',\n'.join(\"('%s')\" % v for v in versions))
    out.write(' on conflict do nothing;\n')
"

cd - > /dev/null
docker cp "$WORKDIR/combined.sql" novgov-db-1:/tmp/combined.sql
docker cp "$WORKDIR/insert_versions.sql" novgov-db-1:/tmp/insert_versions.sql

$PSQL -v ON_ERROR_STOP=1 -f /tmp/combined.sql
$PSQL -v ON_ERROR_STOP=1 -f /tmp/insert_versions.sql

rm -rf "$WORKDIR"

echo "==> Terminé. Tu peux maintenant lancer : docker compose up -d auth rest"
