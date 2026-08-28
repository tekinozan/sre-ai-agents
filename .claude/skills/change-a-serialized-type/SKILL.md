---
name: change-a-serialized-type
description: Use when adding a property to or changing any type that crosses HTTP, Kafka, a Temporal activity, a jsonb column or the model boundary. Covers which of the five generated JSON contexts to update and the four failures this has already caused.
---

Reflection-based serialization is banned and enforced, so every serialized type
needs compile-time metadata in a generated context. The generator only knows what
it was told, and a type can need more than one context.

The four failures already paid for:

1. the generator does not walk into types behind a custom converter
2. a `Task`-returning `[WorkflowRun]` yields `VoidTaskResult`, which cannot be named
3. `required` members stop property initializers running — optional members must
   be nullable
4. wire contracts are snake_case

**Full procedure: `docs/agents/recipes/change-a-serialized-type.md`.**
