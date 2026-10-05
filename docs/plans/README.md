# Execution plans

`docs/plans/` holds execution plans for multi-session work. A plan stays in the repository from the moment it is written until every step is done or explicitly dropped. Decisions that outlive the plan graduate to `docs/DECISIONS.md`; the plan is then marked complete and kept because its reasoning and partial-work record are the durable resume point.

| Plan | Subject | Status |
|---|---|---|
| [`llm-providers.md`](llm-providers.md) | Tenant model providers and evaluation harness | Phases 1–3 complete; Phase 5 partially landed; Phase 4 and remaining Phase 5 planned |
| [`product-adaptability-implementation-and-testing.md`](product-adaptability-implementation-and-testing.md) | ADP-001–ADP-605 product adaptability implementation and test guide | Main guide; Phase 0 partially landed (ADP-001/ADP-002 applied, ADP-003 verified, ADP-004 blocked) |
| [`adp-003-integration-support-and-testing.md`](adp-003-integration-support-and-testing.md) | ADP-003 integration support and test evidence | Focused supplement; alert ingress HTTP/persistence boundaries verified |
| [`adp-003-evidence-closure.md`](adp-003-evidence-closure.md) | HTTP boundary proof for alert ingress and model-provider management, closing the ADP-003 evidence rows | Approaches 1–5 executed and verified; approach 6 partly done; file ends with a Release evidence section |
| [`adp-phased-execution-plan.md`](adp-phased-execution-plan.md) | Ordered phase-by-phase execution chain for ADP-001–ADP-605, including the ADP-003 evidence closure work | Phase F0: steps F0-1–F0-7 verified, F0-8/F0-9 applied; F0-10 blocked, awaiting real pilot product/environment targets. Phase F1: F1-1–F1-5 applied, F1-6–F1-8 planned |
| [`llm-providers-resume.md`](llm-providers-resume.md) | Verified resume plan for the interrupted implementation | Complete |
| [`production-readiness.md`](production-readiness.md) | Production readiness remediation, including completed local Compose PostgreSQL consolidation | Phases 1–4 and deployment follow-up complete; Phase 5 applied, full soak outstanding |
| [`project-assessment-2026-10.md`](project-assessment-2026-10.md) | 2026-10 assessment of phase, provider flexibility, production readiness and business flow, with the prioritised remediation order | P0–P4-3 applied or verified; P4-4 onward planned; progress table inside |
| [`gemini-budgets-notifications.md`](gemini-budgets-notifications.md) | Native Gemini adapter, tenant cost budgets, Slack and Teams notifications (assessment P4-4, P5-1, P5-2) | Done; Gemini live proof awaits a credential |
| [`incidents-feedback-evals.md`](incidents-feedback-evals.md) | Incident listing, RCA feedback, eval separation of duties and the promotion gate (assessment P5-3, P6-1…P6-3) | Planned |
