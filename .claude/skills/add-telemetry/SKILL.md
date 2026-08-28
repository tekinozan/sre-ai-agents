---
name: add-telemetry
description: Use when adding a span, a span attribute or a metric. Covers the attribute allowlist that drops unknown keys silently, TagList and cardinality, and the sampler rule that keeps investigations visible.
---

Everything lives in `src/SreAiAgent.Application/Diagnostics/`. `Activity.SetTag` is
banned; `SetSafeTag` is the only route to a tag, and a key that is not in the
`TraceAttributes` allowlist is **dropped silently** — the only signal is
`sre_trace_attribute_rejected_total` leaving zero. Add the constant *and* the
allowlist entry.

Prompts, tokens, credentials and raw tool results may never appear on a span.
Counts are fine; contents are not.

Do not wrap `InvestigationAwareSampler` in a `ParentBasedSampler`. It is the
top-level sampler on purpose: an investigation span is never a root, so wrapping
it means it is never consulted.

**Full procedure: `docs/agents/recipes/add-telemetry.md`.**
