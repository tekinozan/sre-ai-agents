# Execution plans

`docs/plans/` holds execution plans for multi-session work. A plan stays in the repository from the moment it is written until every step is done or explicitly dropped. Decisions that outlive the plan graduate to `docs/DECISIONS.md`; the plan is then marked complete and kept because its reasoning and partial-work record are the durable resume point.

| Plan | Subject | Status |
|---|---|---|
| [`llm-providers.md`](llm-providers.md) | Tenant model providers and evaluation harness | Phases 1–3 complete; Phase 5 partially landed; Phase 4 and remaining Phase 5 planned |
| [`adp-003-evidence-closure.md`](adp-003-evidence-closure.md) | HTTP boundary proof for alert ingress and model-provider management, closing the ADP-003 evidence rows | Planned; not started |
| [`adp-phased-execution-plan.md`](adp-phased-execution-plan.md) | Ordered phase-by-phase execution chain for ADP-001–ADP-605, including the ADP-003 evidence closure work | Planned; phase F0 not started |
| [`llm-providers-resume.md`](llm-providers-resume.md) | Verified resume plan for the interrupted implementation | Complete |
| [`production-readiness.md`](production-readiness.md) | Production readiness remediation, including completed local Compose PostgreSQL consolidation | Phases 1–4 and deployment follow-up complete; Phase 5 pending |
