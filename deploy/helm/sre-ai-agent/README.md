# SRE AI Agent Helm chart

The chart is the deployment source of truth. `deploy/k8s/generated-development.yaml`
is a checked-in render for review only; regenerate it with `eng/render-k8s.sh`
and fail CI drift checks with `eng/check-k8s-render.sh`.

Every Secret is external. `existingSecret` supplies application credentials;
`migration.secretName` is a separate Secret whose only required key is
`Database__ConnectionString`. Neither the chart nor generated raw manifest
creates a Secret object or contains a placeholder value.

A schema migration is a two-phase release gate, not a startup action or Helm
hook. The migration Job depends only on its dedicated database secret
(`migration.secretName`), runs with `automountServiceAccountToken: false`, and
requires no application ConfigMap:

```bash
set -euo pipefail

# Phase 1: Deploy migration Job and block on completion.
# The explicit '&&' chain and 'set -euo pipefail' enforce fail-fast semantics:
# a non-zero exit from either Helm --wait-for-jobs or kubectl wait immediately
# halts execution, preventing Phase 2 workload rollout on migration failure.
helm upgrade --install sre-ai-agent deploy/helm/sre-ai-agent \
  --namespace sre-ai-agent --create-namespace \
  -f deploy/helm/sre-ai-agent/values.production.yaml \
  -f values.production.local.yaml \
  --set app.enabled=false --set migration.enabled=true \
  --wait --wait-for-jobs --timeout 10m \
&& kubectl wait --namespace sre-ai-agent --for=condition=complete \
  job/sre-ai-agent-database-migration --timeout=10m \
&& helm upgrade sre-ai-agent deploy/helm/sre-ai-agent \
  --namespace sre-ai-agent \
  -f deploy/helm/sre-ai-agent/values.production.yaml \
  -f values.production.local.yaml \
  --set app.enabled=true --set migration.enabled=false \
  --wait --timeout 10m
```

The final helm upgrade is the promotion boundary: it is reachable only through
the successful '&&' chain after the migration Job completes successfully,
guaranteeing that application workloads are never rendered or rolled on schema failure.
The migration Job uses the namespace's default identity with
`automountServiceAccountToken: false`, so it mounts no token; its NetworkPolicy
permits only DNS (UDP/TCP 53) and the configured PostgreSQL CIDRs.
Note that live CNI behavior must be validated against the target cluster's CNI provider.
For a shared development deployment, use `values.development.yaml`. It sets
`ASPNETCORE_ENVIRONMENT=Development` for fake local providers but leaves
`Security__AllowDevelopmentAuthentication=false`; supply a real issuer and
audience through the external development Secret.

Before the first migration, configure role permissions using two explicit,
single-authority phases. Neither script alters role memberships or grants/revokes
admin options, preserving standing administrator permissions completely:

1. **Database owner / administrator connection** grants database-level connection rights:
   ```bash
   psql "$ADMIN_DATABASE_URL" \
     --set=database_name=sre \
     --set=runtime_role=sre_runtime \
     --file=deploy/helm/sre-ai-agent/database-role-bootstrap-admin.sql
   ```

2. **Migration role connection** configures schema usage, table/sequence access, and default privileges for future objects:
   ```bash
   psql "$MIGRATION_DATABASE_URL" \
     --set=schema_name=public \
     --set=runtime_role=sre_runtime \
     --file=deploy/helm/sre-ai-agent/database-role-bootstrap.sql
   ```
Both scripts execute atomically inside transactions with `ON_ERROR_STOP` enabled.