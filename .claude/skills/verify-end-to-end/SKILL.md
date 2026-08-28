---
name: verify-end-to-end
description: Use before claiming a change works when it touches persistence, messaging, the workflow engine, serialization, telemetry or configuration. Runs the compose stack and checks what a healthy investigation looks like.
---

The gate proves a change compiles and that the unit tests pass. It does not prove
the platform works: about twenty defects here passed the gate and were found only
by running the process against real infrastructure.

    docker compose -f deploy/docker/compose.yaml up -d --build

Then drive an alert through and confirm the report, the single Jaeger trace across
all three services, the drained outbox, and a Temporal workflow that reaches
`Completed` rather than staying `Running`.

**Full procedure: `docs/agents/recipes/verify-end-to-end.md`.**
Diagnosis when it fails: `docs/RUNBOOK.md`.
