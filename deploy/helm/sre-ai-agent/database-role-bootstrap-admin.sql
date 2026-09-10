-- Phase 1 of 2: Database administrator / owner connection.
--
-- Performs database-level connection grants to the runtime role.
--
-- Prerequisites:
-- - Connected as database owner (or superuser).
-- - Target database and runtime_role exist.
--
-- Invocation:
--   psql "$ADMIN_DATABASE_URL" \
--     --set=database_name=sre \
--     --set=runtime_role=sre_runtime \
--     --file=deploy/helm/sre-ai-agent/database-role-bootstrap-admin.sql
--
-- Authority & invariants:
-- - Operates strictly with database-owner authority.
-- - Performs NO role-membership changes, NO ALTER ROLE, and NO SET ROLE.
-- - Existing role memberships and ADMIN OPTION grants remain completely untouched.

\set ON_ERROR_STOP on

BEGIN;

-- Database connection permission is granted with database-owner authority.
-- No role memberships are touched, and schema/object grants are deferred to Phase 2.
GRANT CONNECT ON DATABASE :"database_name" TO :"runtime_role";

COMMIT;
