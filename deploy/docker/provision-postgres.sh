#!/bin/sh
set -eu

# Init scripts run only for a new volume. This job also supports retained
# volumes, so it adds missing objects and never changes existing credentials,
# role attributes, or database ownership.
: "${POSTGRES_BOOTSTRAP_USER:=sre_admin}"
: "${POSTGRES_BOOTSTRAP_PASSWORD:=sre_admin}"
: "${POSTGRES_LEGACY_ADMIN_USER:=sre}"
: "${POSTGRES_LEGACY_ADMIN_PASSWORD:=sre}"
: "${POSTGRES_BUSINESS_USER:=businessowner}"
: "${POSTGRES_BUSINESS_PASSWORD:=businessowner}"
: "${POSTGRES_TEMPORAL_USER:=temporal}"
: "${POSTGRES_TEMPORAL_PASSWORD:=temporal}"

validate_identifier() {
    case "$1" in
        "" | [!A-Za-z_]* | *[!A-Za-z0-9_]*) return 1 ;;
    esac
    [ "${#1}" -le 63 ]
}

for identifier in "$POSTGRES_BOOTSTRAP_USER" "$POSTGRES_LEGACY_ADMIN_USER" "$POSTGRES_BUSINESS_USER" "$POSTGRES_TEMPORAL_USER"; do
    validate_identifier "$identifier" || { printf '%s\n' "Invalid PostgreSQL role name: $identifier" >&2; exit 1; }
done

psql_as() {
    user=$1
    password=$2
    shift 2
    PGPASSWORD="$password" psql -X -v ON_ERROR_STOP=1 -h postgres -p 5432 -U "$user" -d postgres "$@"
}

if psql_as "$POSTGRES_BOOTSTRAP_USER" "$POSTGRES_BOOTSTRAP_PASSWORD" -Atqc 'SELECT 1' >/dev/null 2>&1; then
    admin_user=$POSTGRES_BOOTSTRAP_USER
    admin_password=$POSTGRES_BOOTSTRAP_PASSWORD
    printf '%s\n' "Provisioning through bootstrap administrator '$admin_user'."
elif psql_as "$POSTGRES_LEGACY_ADMIN_USER" "$POSTGRES_LEGACY_ADMIN_PASSWORD" -Atqc 'SELECT 1' >/dev/null 2>&1; then
    admin_user=$POSTGRES_LEGACY_ADMIN_USER
    admin_password=$POSTGRES_LEGACY_ADMIN_PASSWORD
    printf '%s\n' "Provisioning through legacy administrator '$admin_user'."
else
    printf '%s\n' 'Neither configured PostgreSQL administrator can connect.' >&2
    exit 1
fi

role_exists() {
    printf '%s\n' "SELECT 1 FROM pg_roles WHERE rolname = :'role';" | \
        psql_as "$admin_user" "$admin_password" -v role="$1" -Atq | grep -qx 1
}

database_exists() {
    printf '%s\n' "SELECT 1 FROM pg_database WHERE datname = :'database';" | \
        psql_as "$admin_user" "$admin_password" -v database="$1" -Atq | grep -qx 1
}

ensure_role() {
    if role_exists "$1"; then
        printf '%s\n' "Role '$1' already exists; leaving it unchanged."
    else
        printf '%s\n' "CREATE ROLE :\"role\" LOGIN PASSWORD :'password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;" | \
            psql_as "$admin_user" "$admin_password" -v role="$1" -v password="$2" -q
        printf '%s\n' "Created non-superuser role '$1'."
    fi
}

ensure_database() {
    if database_exists "$1"; then
        owner=$(printf '%s\n' "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = :'database';" | \
            psql_as "$admin_user" "$admin_password" -v database="$1" -Atq)
        printf '%s\n' "Database '$1' already exists with owner '$owner'; leaving ownership unchanged."
    else
        printf '%s\n' 'CREATE DATABASE :"database" OWNER :"owner";' | \
            psql_as "$admin_user" "$admin_password" -v database="$1" -v owner="$2" -q
        printf '%s\n' "Created database '$1' owned by '$2'."
    fi
}

ensure_role "$POSTGRES_BUSINESS_USER" "$POSTGRES_BUSINESS_PASSWORD"
ensure_role "$POSTGRES_TEMPORAL_USER" "$POSTGRES_TEMPORAL_PASSWORD"
ensure_database sre "$POSTGRES_BUSINESS_USER"
ensure_database temporal "$POSTGRES_TEMPORAL_USER"
ensure_database temporal_visibility "$POSTGRES_TEMPORAL_USER"
printf '%s\n' 'PostgreSQL logical database provisioning completed safely.'
