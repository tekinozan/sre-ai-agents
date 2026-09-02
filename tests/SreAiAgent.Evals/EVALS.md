# Eval harness

The eval project exercises the production registries, controlled prompt renderer,
model gateway, agent executor, deterministic classification/routing, correlation,
and citation binding with an eval-only fixed clock, recording audit sink, and
fixture-backed read-only tool gateway. It does not register PostgreSQL, Kafka, or
Temporal adapters.

## Scripted PR gate

The default CI gate excludes live evals and always runs the deterministic scripted
smoke after the ordinary test projects:

```bash
bash eng/ci.sh
```

```powershell
pwsh -File eng/ci.ps1
```

Run only the scripted smoke with:

```bash
dotnet test tests/SreAiAgent.Evals/SreAiAgent.Evals.csproj -- --filter-trait "Category=ScriptedEval"
```

The candidate, dataset, scorer configuration, repeat count, fixed clock, seed,
release hashes, and baseline identity are frozen into one candidate hash before
the first attempt. Mutable names such as `latest` are rejected.

## Explicit live matrix

Live evals never run as part of a pull-request gate. A manual or scheduled job must
provide an exact candidate manifest, a compatible immutable baseline, and the
credential environment variable named by the candidate manifest. Then select the
live matrix explicitly:

```bash
export SRE_EVAL_CANDIDATE=/secure/job-input/candidate-2026.08.31.1.json
export SRE_EVAL_BASELINE=/secure/job-input/baseline-2026.08.31.1.json
export SRE_EVAL_OPENAI_KEY=...
bash eng/ci.sh --live-eval
```

```powershell
$env:SRE_EVAL_CANDIDATE = 'C:\secure\job-input\candidate-2026.08.31.1.json'
$env:SRE_EVAL_BASELINE = 'C:\secure\job-input\baseline-2026.08.31.1.json'
$env:SRE_EVAL_OPENAI_KEY = '...'
pwsh -File eng/ci.ps1 -LiveEval
```

A requested live run fails when the candidate, baseline, capability, endpoint, or
credential is absent or incompatible; it is not converted to a skipped test. The
runner goes through the production model gateway/provider resolver and never calls
a provider SDK directly.

## Versioned inputs and outputs

- `Datasets/incidents/v1/` contains redacted schema-versioned cases and canonical
  read-only tool fixtures.
- `Candidates/` pins tenant, provider revision, deployment, assignment, releases,
  and generation settings.
- `Baselines/` contains immutable aggregate baselines compatible by dataset,
  scorer, and repeat hashes.
- `eval-config.v1.json` pins hard/quality thresholds, regression limits, repeats,
  and the disabled-by-default judge policy.

Each run writes `manifest.json`, `attempts.jsonl`, `summary.json`, and
`results.junit.xml` below `artifacts/evals/<run-id>/`. Existing files are never
overwritten with different bytes. Artifacts contain hashes, safe errors, scores,
usage, and tool names only: rendered prompts, raw tool payloads, raw provider
responses, credential references, and credentials are structurally excluded and
rejected by the writer.

The hard gate covers completion/schema, evidence citations, unsupported or
forbidden claims, injection/secret leakage, read-only allowlisted tool use,
observed routing/root-cause family, and budgets. Diagnosis correctness, grounding,
uncertainty calibration, and actionability remain separate quality dimensions;
raw response equality is never scored. Promotion eligibility requires every hard
gate plus absolute and baseline-regression thresholds. The optional judge uses a
separate pinned identity and candidate-blind structured rubric, and cannot be
enabled without a pinned calibration version and minimum agreement.
