# Add or update a package

Central Package Management with transitive pinning and committed lock files, so
a dependency cannot float between the build that was tested and the build that
ships (D02). The rules are mechanical; the failure modes are not obvious.

## Procedure

**1. Pin the version in `Directory.Packages.props`.**

```xml
<PackageVersion Include="Some.Package" Version="1.2.3" />
```

Never put a `Version` attribute on a `PackageReference` in a `.csproj`. CPM
rejects it, and the point is that a version appears exactly once in the
repository. Where several packages ship together, use the existing property
pattern (`$(TestcontainersVersion)`) so they cannot drift apart.

**2. Reference it, without a version.**

```xml
<PackageReference Include="Some.Package" />
```

Test-only packages that every suite needs belong in `tests/Directory.Build.props`,
not in each project.

**3. Regenerate the lock files.**

```bash
dotnet restore SreAiAgent.slnx --force-evaluate
```

Every project has its own `packages.lock.json` and all of them are committed.
`--force-evaluate` is required: a plain restore will reuse the existing lock and
report success while the new package is missing from it.

**Never hand-edit a lock file.** It records transitive resolution and content
hashes; an edited one either fails `--locked-mode` or, worse, passes while
describing a graph NuGet did not produce.

**4. Run the gate.**

```bash
bash eng/ci.sh
```

`--locked-mode` in the restore step is what proves the lock files are current, and
the audit step fails the build on a known vulnerability.

## Things that catch people

**The layer guard only knows the families it was told about.**
`Directory.Build.targets` fails the build (`SREARCH001`) when Domain, Application
or Contracts declare a package matching `BannedLayerPackagePattern` —
`Microsoft.AspNetCore`, `EntityFrameworkCore`, `Npgsql`, `Confluent.Kafka`,
`Temporalio`, `OpenTelemetry`, `Serilog`, `KubernetesClient`, `YamlDotNet`,
`JsonSchema`, `OpenAI`, `Azure.AI`, `Anthropic`.

A new infrastructure dependency outside that list slips past. If you are adding
one, **extend the pattern in the same commit**. `ArchitectureTests` is the second
net, but it only sees what ends up in a compiled assembly reference.

**Sources are cleared deliberately.** The repository-local `NuGet.config` clears
inherited sources and leaves only nuget.org (D03). The machine this was built on
had a local feed and a Visual Studio offline feed registered globally, and either
can satisfy a restore with a package nobody chose. If a restore cannot find your
package, the fix is the version, not the config.

**One prerelease, on purpose.** `OpenTelemetry.Exporter.Prometheus.AspNetCore` is
pinned to a beta because ARCHITECTURE.md 12.1 requires `GET /metrics` and there is
no stable equivalent (D17). It is the only prerelease dependency; adding a second
needs a decision entry saying why.

**A new project needs its own lock file** and an entry in `SreAiAgent.slnx`. The
`--force-evaluate` restore generates the lock; the solution entry is manual.

**A package used only by an analyzer or a generator** takes
`PrivateAssets="all"`. The layer guard ignores those, correctly — they do not
become a runtime dependency.

## Removing a package

Delete the `PackageVersion`, delete the references, and run the same
`--force-evaluate` restore. A `PackageVersion` left behind for a package nobody
references is harmless but misleading, and the next person will assume it is used.

## Related

- `docs/DECISIONS.md` D02, D03, D17, D18
- `ARCHITECTURE.md` 22
