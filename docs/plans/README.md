# Execution plans

`docs/plans/` holds execution plans for multi-session work. A plan stays in the repository from the moment it is written until every step is done or explicitly dropped. Decisions that outlive the plan graduate to `docs/DECISIONS.md`; the plan is then marked complete and kept because its reasoning and partial-work record are the durable resume point.

| Plan | Subject | Status |
|---|---|---|
| [`llm-providers.md`](llm-providers.md) | Tenant model providers and evaluation harness | Phases 1–3 complete; Phase 5 partially landed; Phase 4 and remaining Phase 5 planned |
| [`llm-providers-resume.md`](llm-providers-resume.md) | Verified resume plan for the interrupted implementation | Complete |
| [`production-readiness.md`](production-readiness.md) | Production readiness remediation across tenant isolation, messaging, operational surface, resilience, and proof | Phases 1–4 complete; Phase 5 pending |
