-- Phase 2 of 2: Migration role connection.
--
-- Configures schema usage, table/sequence permissions, and default privileges
-- for the runtime role on objects owned by the migration role.
--
-- Prerequisites:
-- - Phase 1 (database-role-bootstrap-admin.sql) has completed successfully.
-- - Connected directly authenticated AS the migration role.
-- - migration_role owns the schema and all existing migration objects.
--
-- Invocation:
--   psql "$MIGRATION_DATABASE_URL" \
--     --set=schema_name=public \
--     --set=runtime_role=sre_runtime \
--     --file=deploy/helm/sre-ai-agent/database-role-bootstrap.sql
--
-- Authority & invariants:
-- - Operates strictly as the migration role (CURRENT_USER).
-- - Requires NO admin or superuser privileges; needs NO role membership grants.
-- - Grants SELECT, INSERT, UPDATE, DELETE on all existing tables in schema.
-- - Grants USAGE, SELECT, UPDATE on all existing sequences in schema.
-- - Configures ALTER DEFAULT PRIVILEGES so future tables and sequences created
--   by the migration role automatically grant the same permissions to runtime_role.
-- - Performs NO role-membership changes and NO SET ROLE.

\set ON_ERROR_STOP on

BEGIN;

-- Schema usage permits relation lookup without granting DDL or schema mutation.
GRANT USAGE ON SCHEMA :"schema_name" TO :"runtime_role";

-- Existing tables and sequences receive only DML and value generation grants;
-- runtime never receives DDL, TRUNCATE, or ownership rights.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA :"schema_name" TO :"runtime_role";
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA :"schema_name" TO :"runtime_role";

-- Default privileges ensure future tables and identity sequences created during
-- migrations automatically inherit runtime access, preventing post-rollout failures.
ALTER DEFAULT PRIVILEGES IN SCHEMA :"schema_name"
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";

ALTER DEFAULT PRIVILEGES IN SCHEMA :"schema_name"
  GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";

COMMIT;
