# CLAUDE.md

@AGENTS.md

The guidance for this repository lives in `AGENTS.md` so that every assistant
reads the same file. If the import above did not resolve, read `AGENTS.md`
directly before making a change.

Claude-specific notes:

- Skills for the common tasks are in `.claude/skills/`. Each one is a thin
  wrapper over the shared procedure in `docs/agents/recipes/`, which is where the
  content lives and where any correction belongs.
- Do not add guidance to this file. A second source of truth drifts; corrections
  belong in `AGENTS.md` or the recipe they concern.
