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
provide an exact candidate manifest, an optional bootstrap switch or compatible immutable baseline,
and the credential environment variable named by the candidate manifest.

### Cost and call volume warning

The live evaluation matrix runs **8 incident cases × 2 repeats** (concurrency 1, warmup 0).
Each case executes incident classification, agent selection, investigation agent turns (which may execute multiple model and fixture tool calls), and findings correlation.
A single live run typically issues **~48–64 outbound provider HTTP requests** (with a higher ceiling if complex investigation turns occur).
Ensure your provider account has sufficient quota, budget limits, and rate limits before initiating a live eval run.

### Candidate manifest examples

Candidate manifests pin non-secret runtime connection metadata. Never put real API keys in candidate files; only reference the environment variable name in `credential_environment_variable`.

> **Note on IDs and hashes:** The example UUIDs (`11111111-...`) and 64-character SHA-256 hashes (`11111111...`) in the templates below are placeholders. When creating candidate manifests, replace them with stable immutable candidate labels. The eval runtime validates their shape and derives fresh in-memory domain entities for the standalone run without requiring persisted control-plane database records.
#### OpenAI (`openai`)

```json
{
  "schema_version": "sre-eval-candidate/v1",
  "candidate_id": "openai-gpt4o-2026.09.01",
  "tenant": "eval-tenant",
  "provider_connection_revision": {
    "id": "11111111-1111-1111-1111-111111111111",
    "version": "1",
    "hash": "1111111111111111111111111111111111111111111111111111111111111111",
    "endpoint": "https://api.openai.com/v1",
    "organization": "org-your-org-id"
  },
  "model_deployment": {
    "id": "22222222-2222-2222-2222-222222222222",
    "version": "1",
    "hash": "2222222222222222222222222222222222222222222222222222222222222222",
    "provider": "openai",
    "model": "gpt-4o"
  },
  "assignment_release": {
    "id": "33333333-3333-3333-3333-333333333333",
    "version": "1",
    "hash": "3333333333333333333333333333333333333333333333333333333333333333"
  },
  "releases": {
    "agent": "agents-2026.08.25.1",
    "prompt": "sre-prompts-2026.08.25.1",
    "model": "models-2026.08.25.1",
    "tool": "tools-2026.08.25.1",
    "policy": "policies-2026.08.25.1"
  },
  "generation": {
    "temperature": 0.0,
    "maximum_output_tokens": 4096,
    "seed": 240831
  },
  "credential_environment_variable": "SRE_EVAL_OPENAI_KEY"
}
```

#### Azure OpenAI (`azure_openai`)

Azure OpenAI requires `endpoint` (the resource endpoint URL) and `api_version` (pinned API version):

```json
{
  "schema_version": "sre-eval-candidate/v1",
  "candidate_id": "azure-gpt4o-2026.09.01",
  "tenant": "eval-tenant",
  "provider_connection_revision": {
    "id": "11111111-1111-1111-1111-111111111111",
    "version": "1",
    "hash": "1111111111111111111111111111111111111111111111111111111111111111",
    "endpoint": "https://your-resource.openai.azure.com",
    "api_version": "2024-06-01"
  },
  "model_deployment": {
    "id": "22222222-2222-2222-2222-222222222222",
    "version": "1",
    "hash": "2222222222222222222222222222222222222222222222222222222222222222",
    "provider": "azure_openai",
    "model": "your-gpt-4o-deployment-name"
  },
  "assignment_release": {
    "id": "33333333-3333-3333-3333-333333333333",
    "version": "1",
    "hash": "3333333333333333333333333333333333333333333333333333333333333333"
  },
  "releases": {
    "agent": "agents-2026.08.25.1",
    "prompt": "sre-prompts-2026.08.25.1",
    "model": "models-2026.08.25.1",
    "tool": "tools-2026.08.25.1",
    "policy": "policies-2026.08.25.1"
  },
  "generation": {
    "temperature": 0.0,
    "maximum_output_tokens": 4096,
    "seed": 240831
  },
  "credential_environment_variable": "SRE_EVAL_AZURE_KEY"
}
```

### Step 1: Bootstrap a new candidate baseline

When running a new live candidate for the first time, no baseline exists yet. Set `SRE_EVAL_WRITE_BASELINE=1` to generate a candidate-specific immutable baseline:

```bash
export SRE_EVAL_CANDIDATE=/secure/job-input/candidate-openai-2026.09.01.json
export SRE_EVAL_WRITE_BASELINE=1
export SRE_EVAL_OPENAI_KEY="your-actual-secret-key"
bash eng/ci.sh --live-eval
```

**Windows / PowerShell:**

```powershell
$env:SRE_EVAL_CANDIDATE = 'C:\secure\job-input\candidate-openai-2026.09.01.json'
$env:SRE_EVAL_WRITE_BASELINE = '1'
$env:SRE_EVAL_OPENAI_KEY = 'your-actual-secret-key'
pwsh -File eng/ci.ps1 -LiveEval
```

The test runner writes the new baseline file to `tests/SreAiAgent.Evals/Baselines/baseline-<candidate_id>-<hash>.json` and outputs the path to the console.

### Step 2: Normal evaluation run against the pinned baseline

After bootstrapping the baseline, subsequent runs must pin `SRE_EVAL_BASELINE` to that baseline and unset `SRE_EVAL_WRITE_BASELINE`:

```bash
export SRE_EVAL_CANDIDATE=/secure/job-input/candidate-openai-2026.09.01.json
export SRE_EVAL_BASELINE=/path/to/tests/SreAiAgent.Evals/Baselines/baseline-openai-gpt4o-2026.09.01-xxxxxxxx.json
unset SRE_EVAL_WRITE_BASELINE
export SRE_EVAL_OPENAI_KEY="your-actual-secret-key"
bash eng/ci.sh --live-eval
```

**Windows / PowerShell:**

```powershell
$env:SRE_EVAL_CANDIDATE = 'C:\secure\job-input\candidate-openai-2026.09.01.json'
$env:SRE_EVAL_BASELINE = 'C:\path\to\tests\SreAiAgent.Evals\Baselines\baseline-openai-gpt4o-2026.09.01-xxxxxxxx.json'
$env:SRE_EVAL_WRITE_BASELINE = $null
$env:SRE_EVAL_OPENAI_KEY = 'your-actual-secret-key'
pwsh -File eng/ci.ps1 -LiveEval
```

### Windows command caveats

- In PowerShell, use `$env:VARIABLE_NAME = 'value'` to set environment variables and `$env:VARIABLE_NAME = $null` to unset them.
- In cmd.exe, use `set VARIABLE_NAME=value` (note: trailing spaces in cmd `set` become part of the variable value).
- Always use absolute file paths for `SRE_EVAL_CANDIDATE` and `SRE_EVAL_BASELINE`.
- Never commit API keys or files containing keys into git history.

A requested live run fails when the candidate, baseline, capability, endpoint, or
credential is absent or incompatible; it is not converted to a skipped test. The
runner goes through the production model gateway/provider resolver and never calls
a provider SDK directly.
## Completing a platform evaluation

A live run can be the evidence for promoting a model assignment release
(`docs/DECISIONS.md` D50). For that, the candidate manifest has to describe the
release exactly, using the management API's own values:

| Manifest member | From |
|---|---|
| `assignment_release.id`, `.version`, `.hash` | `GET /v1/model-assignment-releases`: `release_id`, `version`, `configuration_hash` |
| `model_deployment.id`, `.version`, `.hash` | `GET /v1/model-deployments`: the one deployment version every slot uses |
| `provider_connection_revision.id`, `.version`, `.hash` | `GET /v1/model-providers/{id}/revisions`: that deployment's revision |

The run writes `summary.json` (`sre-eval-summary/v2`), which names those
identities, the dataset and the baseline. An operator requests the evaluation
with the dataset as `<dataset_id>@<version>` and the baseline id the run will use
(or `bootstrap` for a first run with `SRE_EVAL_WRITE_BASELINE=1`); the CI job,
authenticated as an `SreEvaluationRunner` identity, posts the file unchanged:

```bash
curl -X POST "$API/v1/model-assignment-releases/$RELEASE/evaluations/$EVALUATION" \
  -H "Authorization: Bearer $RUNNER_TOKEN" -H 'Content-Type: application/json' \
  --data-binary @artifacts/evals/$RUN_ID/summary.json
sha256sum artifacts/evals/$RUN_ID/summary.json   # equals the response's artifact_hash
```

The platform derives pass or fail from the summary's gate and refuses a summary
for another release, deployment, revision, dataset or baseline. A release whose
slots use more than one deployment cannot be evaluated by one run.

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
