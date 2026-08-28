---
name: add-a-tool
description: Use when adding or changing a tool an agent can call. Covers the tool definition and its release, the IToolAdapter implementation and registration, the result envelope, and the agent allowlists that grant it.
---

A tool is half artifact and half code: a definition that can be released without a
deploy, and an adapter that cannot.

V1 ships read-only tools only, and no `IWriteToolRegistry` implementation is
registered, so a component asking for write capability cannot be constructed. A
write tool is a V2 change that also requires the approval flow.

Never call an adapter from anywhere but the gateway — that skips authorization,
the risk ceiling, rate limiting, the result cap, redaction and the audit record.

**Full procedure: `docs/agents/recipes/add-a-tool.md`.**
