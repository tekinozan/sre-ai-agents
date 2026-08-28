---
name: add-a-package
description: Use when adding, updating or removing a NuGet package. Covers central package management, regenerating the committed lock files, and extending the layer guard when the dependency is a new infrastructure family.
---

Versions live only in `Directory.Packages.props`; a `PackageReference` carries no
version. Lock files are committed and the gate restores with `--locked-mode`, so
after any change run:

    dotnet restore SreAiAgent.slnx --force-evaluate

A plain restore reuses the existing lock and reports success while the new package
is missing from it. Never hand-edit a lock file.

The layer guard only knows the package families named in
`Directory.Build.targets`. A new infrastructure dependency outside that list slips
past — extend the pattern in the same commit.

**Full procedure: `docs/agents/recipes/add-a-package.md`.**
