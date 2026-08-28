---
name: add-an-expert-agent
description: Use when adding, removing or changing an expert agent in config/agents. Covers the six artifacts one agent spans, the feature flag whose absence disables the agent silently, and the scripted model provider that must know about it.
---

Adding an expert is a configuration release, not a deploy. One conceptual change
spans six files, and two of the ways to get it wrong produce no error:

- no entry in `config/flags/agent-flags.v1.yaml` — the agent loads, passes startup
  validation and never runs, because the file's defaults are `state: disabled`
- no branch in `ScriptedChatClient` — the agent runs, spends its budget and
  returns nothing

**Full procedure: `docs/agents/recipes/add-an-expert-agent.md`.** Follow it rather
than this summary; it has the file order, the naming trap in the prompt reference,
and the verification steps.
