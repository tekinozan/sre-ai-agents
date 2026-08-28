# Kubernetes manifests

The resources of ARCHITECTURE.md 19.2, as plain YAML.

These are the reference form. The Helm chart under `deploy/helm` renders the same
shapes with values; this directory exists so the resources can be read and
reviewed without rendering a template, which is what most people want when they
are trying to understand a deployment rather than change one.

| File | Contents |
|---|---|
| `namespace.yaml` | Namespace, ServiceAccount, Role and RoleBinding |
| `config.yaml` | ConfigMap and the Secret references the pods read |
| `api.yaml` | API Deployment, Service, HPA and PodDisruptionBudget |
| `workers.yaml` | Alert and investigation worker Deployments and HPAs |
| `networkpolicy.yaml` | Default-deny plus the egress each host actually needs |

## What is deliberately not here

**Secret values.** The manifests reference a Secret named `sre-ai-agent`; they do
not create it. A secret committed to a repository is a secret that has leaked,
and section 13 asks for them to arrive from a managed store. Create it out of
band, or point an External Secrets operator at it.

**Production HPA thresholds.** The investigation worker scales on Kafka consumer
lag rather than CPU, because an agent waiting on a model call is idle by every
CPU measure while the backlog grows. The lag metric needs an adapter this
directory cannot assume, so the threshold is a placeholder and the CPU metric is
kept as a floor.

**A migration Job.** `Database:MigrateOnStartup` is off in production on purpose.
Several replicas racing for the migration lock during a rolling update is a
deployment failure that looks like a flake, so the schema change is a separate,
reviewable step in the pipeline.
