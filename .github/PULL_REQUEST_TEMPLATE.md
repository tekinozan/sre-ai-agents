## What changed, and why

<!-- The behaviour that changed, not the files touched. If this fixes something,
     say what the symptom was — that is what the next person searches for. -->

## Checks

- [ ] `bash eng/ci.sh` passes (or `eng/ci.ps1` on Windows)
- [ ] Ran the stack — **required** if this touches persistence, messaging, the
      workflow engine, serialization, telemetry or configuration.
      See `docs/agents/recipes/verify-end-to-end.md`
- [ ] `bash eng/ci.sh --with-integration`, if this touches the database or the outbox

## Decisions and guidance

- [ ] A call a future reader would question is recorded in `docs/DECISIONS.md`,
      with its reason **and its cost**
- [ ] A failure mode that produced no error is recorded in `docs/agents/PITFALLS.md`
- [ ] `AGENTS.md` or a recipe updated, if this changes how a task is done

## Deliberately not done

<!-- Scope left out, and why. An empty section is a claim that nothing was. -->
