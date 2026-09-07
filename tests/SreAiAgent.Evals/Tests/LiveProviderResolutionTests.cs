using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;

namespace SreAiAgent.Evals.Harness;

[Collection(EvalEnvironmentCollection.Name)]
public sealed class LiveProviderResolutionTests
{
    public static TheoryData<string, string, string?, string?, string?, string, string> LiveProviders() => new()
    {
        // provider, endpoint, region, apiVersion, organization, envVarName, secretKey
        { "openai", "https://api.openai.com/v1", null, null, "org-sre-ai", "TEST_LIVE_OPENAI_KEY", "sk-live-test-key-openai-12345" },
        { "azure_openai", "https://sre-ai-eastus.openai.azure.com", "eastus", "2024-06-01", null, "TEST_LIVE_AZURE_KEY", "azure-secret-key-67890" },
        { "anthropic", "https://api.anthropic.com/v1", null, "2023-06-01", null, "TEST_LIVE_ANTHROPIC_KEY", "sk-ant-test-key-anthropic-abcde" },
        { "openai_compatible", "https://localhost:8080/v1", null, null, null, "TEST_LIVE_COMPAT_KEY", "compat-secret-key-fghij" },
    };

    [Theory]
    [MemberData(nameof(LiveProviders))]
    public async Task Live_candidate_resolves_through_control_plane_and_invokes_http_adapter_with_honest_auth_and_endpoint(
        string provider,
        string endpoint,
        string? region,
        string? apiVersion,
        string? organization,
        string envVarName,
        string secretKey)
    {
        Environment.SetEnvironmentVariable(envVarName, secretKey);
        try
        {
            CandidateDefinition definition = new()
            {
                SchemaVersion = "sre-eval-candidate/v1",
                CandidateId = $"live-{provider}-candidate-2026.09.01",
                Tenant = "eval-tenant",
                ProviderConnectionRevision = new ProviderConnectionRevisionIdentity
                {
                    Id = "44444444-4444-4444-4444-444444444444",
                    Version = "1",
                    Hash = "4444444444444444444444444444444444444444444444444444444444444444",
                    Endpoint = endpoint,
                    Region = region,
                    ApiVersion = apiVersion,
                    Organization = organization,
                },
                ModelDeployment = new ModelDeploymentIdentity
                {
                    Id = "55555555-5555-5555-5555-555555555555",
                    Version = "1",
                    Hash = "5555555555555555555555555555555555555555555555555555555555555555",
                    Provider = provider,
                    Model = provider == "azure_openai" ? "gpt-4o-deployment" : "test-model",
                },
                AssignmentRelease = new VersionedIdentity
                {
                    Id = "66666666-6666-6666-6666-666666666666",
                    Version = "1",
                    Hash = "6666666666666666666666666666666666666666666666666666666666666666",
                },
                Releases = new ReleaseIdentity
                {
                    Agent = "agents-2026.08.25.1",
                    Prompt = "sre-prompts-2026.08.25.1",
                    Model = "models-2026.08.25.1",
                    Tool = "tools-2026.08.25.1",
                    Policy = "policies-2026.08.25.1",
                },
                Generation = new GenerationIdentity
                {
                    Temperature = 0.2m,
                    MaximumOutputTokens = 2048,
                    Seed = 240831,
                },
                CredentialEnvironmentVariable = envVarName,
            };

            LoadedDataset dataset = EvalTestData.Dataset();
            EvalConfig config = EvalTestData.Config();

            CandidateSnapshot snapshot = CandidateFreezer.Freeze(definition, dataset, config);

            // 1. Snapshot and candidate hash must NOT contain the secret or the secret env var name.
            string snapshotJson = JsonSerializer.Serialize(snapshot, EvalJsonContext.Default.CandidateSnapshot);
            snapshotJson.ShouldNotContain(secretKey, Case.Insensitive);
            snapshotJson.ShouldNotContain(envVarName, Case.Insensitive);
            snapshot.CandidateHash.Length.ShouldBe(64);

            // 2. Snapshot must safely include the endpoint and provider connection metadata.
            snapshot.ProviderConnectionRevision.Endpoint.ShouldBe(endpoint);
            snapshot.ProviderConnectionRevision.Region.ShouldBe(region);
            snapshot.ProviderConnectionRevision.ApiVersion.ShouldBe(apiVersion);
            snapshot.ProviderConnectionRevision.Organization.ShouldBe(organization);

            // 3. Build pinned assignment and revision from PinnedCandidateFactory.
            var modelRegistry = Substitute.For<IModelProfileRegistry>();
            modelRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Result<ModelProfile>.Success(new ModelProfile
                {
                    Name = "fast",
                    Version = "v1",
                    Provider = provider,
                    ModelName = "test-model",
                    Temperature = 0.2f,
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputTokens = 2048,
                    SupportsStructuredOutput = true,
                    SupportsToolCalling = true,
                    SupportsParallelToolCalls = true,
                    InputCostPerMillionTokens = 1.0m,
                    OutputCostPerMillionTokens = 2.0m,
                }));

            var agentRegistry = Substitute.For<IAgentRegistry>();
            agentRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Result<AgentDefinition>.Success(new AgentDefinition
                {
                    Name = "database_investigator",
                    Version = "v1",
                    PromptRef = "database:v1",
                    ModelProfileRef = "fast",
                    Capabilities = new HashSet<string> { "database" },
                    IncidentTypes = new HashSet<string> { "database" },
                    AllowedTools = new HashSet<string>(),
                    Budget = new AgentExecutionBudget(5, TimeSpan.FromMinutes(2), 5, 5, 10000, 0.5m),
                    OutputSchema = "AgentFinding",
                }));

            TimeProvider timeProvider = new DeterministicTimeProvider(DateTimeOffset.UtcNow);

            (PinnedModelAssignment assignment, ProviderConnectionRevision pinnedRevision) =
                await PinnedCandidateFactory.AssignmentAsync(
                    snapshot,
                    definition.CredentialEnvironmentVariable,
                    ["database_investigator"],
                    modelRegistry,
                    agentRegistry,
                    timeProvider,
                    TestContext.Current.CancellationToken);

            pinnedRevision.Endpoint.ShouldBe(endpoint);
            pinnedRevision.Region.ShouldBe(region);
            pinnedRevision.ApiVersion.ShouldBe(apiVersion);
            pinnedRevision.Organization.ShouldBe(organization);
            pinnedRevision.CredentialRef.ShouldBe($"env://{envVarName}");

            // 4. Set up recording HTTP handler to intercept outbound provider HTTP calls.
            List<HttpRequestMessage> recordedRequests = [];
            RecordingHttpMessageHandler recordingHandler = new(req =>
            {
                recordedRequests.Add(req);
                string responseBody = provider == "anthropic"
                    ? """
                    {
                        "id": "msg-test-123",
                        "model": "test-model",
                        "stop_reason": "end_turn",
                        "content": [{"type": "text", "text": "{\"summary\":\"healthy\"}"}],
                        "usage": {"input_tokens": 12, "output_tokens": 24}
                    }
                    """
                    : """
                    {
                        "id": "chatcmpl-test-123",
                        "model": "test-model",
                        "choices": [{"message": {"content": "{\"summary\":\"healthy\"}"}, "finish_reason": "stop"}],
                        "usage": {"prompt_tokens": 12, "completion_tokens": 24}
                    }
                    """;

                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
                return response;
            });

            IHttpClientFactory clientFactory = new StubHttpClientFactory(recordingHandler);

            var hostEnv = Substitute.For<IHostEnvironment>();
            hostEnv.EnvironmentName.Returns(Environments.Development);

            var llmOptions = Substitute.For<IOptions<LlmOptions>>();
            llmOptions.Value.Returns(new LlmOptions
            {
                AllowedCustomEndpointHosts = ["localhost"],
                CredentialFileRoot = Path.GetTempPath(),
            });

            ICredentialResolver credentialResolver = new ExternalReferenceCredentialResolver(llmOptions);
            ProviderEndpointPolicy endpointPolicy = new(hostEnv, llmOptions);
            ScriptedChatClient scriptedChatClient = new(timeProvider);

            ProviderClientCatalog catalog = new(clientFactory, credentialResolver, endpointPolicy, scriptedChatClient);

            var revisionReader = Substitute.For<IProviderRevisionReader>();
            revisionReader.FindRevisionAsync(snapshot.Tenant, pinnedRevision.Id, Arg.Any<CancellationToken>())
                .Returns(pinnedRevision);

            ChatClientResolver resolver = new(revisionReader, catalog);

            // 5. Resolve classifier client through ChatClientResolver.
            Result<IProviderChatClient> resolveResult = await resolver.ResolveAsync(assignment.Classifier, TestContext.Current.CancellationToken);
            resolveResult.IsSuccess.ShouldBeTrue();

            IProviderChatClient chatClient = resolveResult.Value;

            JsonElement schema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}""").RootElement;
            ProviderChatRequest chatRequest = new(
                "System prompt",
                "User prompt",
                assignment.Classifier.ProviderModel,
                schema,
                assignment.Classifier.Temperature,
                assignment.Classifier.MaxOutputTokens,
                assignment.Classifier.ProviderParameters.TryGetValue("seed", out string? s) ? int.Parse(s, CultureInfo.InvariantCulture) : null,
                assignment.Classifier.ProviderParameters);

            Result<ProviderChatResponse> chatResponse = await chatClient.GetResponseAsync(chatRequest, TestContext.Current.CancellationToken);
            chatResponse.IsSuccess.ShouldBeTrue();
            chatResponse.Value.Text.ShouldBe("{\"summary\":\"healthy\"}");

            // 6. Assert outbound HTTP request properties.
            recordedRequests.Count.ShouldBe(1);
            HttpRequestMessage request = recordedRequests[0];

            switch (provider)
            {
                case "openai":
                    request.RequestUri!.AbsoluteUri.ShouldBe("https://api.openai.com/v1/chat/completions");
                    request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
                    request.Headers.Authorization.Parameter.ShouldBe(secretKey);
                    request.Headers.GetValues("OpenAI-Organization").Single().ShouldBe("org-sre-ai");
                    break;

                case "azure_openai":
                    request.RequestUri!.AbsoluteUri.ShouldBe("https://sre-ai-eastus.openai.azure.com/openai/deployments/gpt-4o-deployment/chat/completions?api-version=2024-06-01");
                    request.Headers.GetValues("api-key").Single().ShouldBe(secretKey);
                    request.Headers.Authorization.ShouldBeNull();
                    break;

                case "anthropic":
                    request.RequestUri!.AbsoluteUri.ShouldBe("https://api.anthropic.com/v1/messages");
                    request.Headers.GetValues("x-api-key").Single().ShouldBe(secretKey);
                    request.Headers.GetValues("anthropic-version").Single().ShouldBe("2023-06-01");
                    break;

                case "openai_compatible":
                    request.RequestUri!.AbsoluteUri.ShouldBe("https://localhost:8080/v1/chat/completions");
                    request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
                    request.Headers.Authorization.Parameter.ShouldBe(secretKey);
                    break;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [Fact]
    public async Task Capabilities_and_parameters_are_honest_and_provider_compatible()
    {
        CandidateDefinition baseDef = EvalTestData.Candidate();
        LoadedDataset dataset = EvalTestData.Dataset();
        EvalConfig config = EvalTestData.Config();

        var modelRegistry = Substitute.For<IModelProfileRegistry>();
        modelRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<ModelProfile>.Success(new ModelProfile
            {
                Name = "fast",
                Version = "v1",
                Provider = "openai",
                ModelName = "test-model",
                Temperature = 0.2f,
                Timeout = TimeSpan.FromSeconds(30),
                MaxOutputTokens = 2048,
                SupportsStructuredOutput = true,
                SupportsToolCalling = true,
                SupportsParallelToolCalls = true,
                InputCostPerMillionTokens = 1.0m,
                OutputCostPerMillionTokens = 2.0m,
            }));

        var agentRegistry = Substitute.For<IAgentRegistry>();
        agentRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<AgentDefinition>.Success(new AgentDefinition
            {
                Name = "database_investigator",
                Version = "v1",
                PromptRef = "database:v1",
                ModelProfileRef = "fast",
                Capabilities = new HashSet<string> { "database" },
                IncidentTypes = new HashSet<string> { "database" },
                AllowedTools = new HashSet<string>(),
                Budget = new AgentExecutionBudget(5, TimeSpan.FromMinutes(2), 5, 5, 10000, 0.5m),
                OutputSchema = "AgentFinding",
            }));

        TimeProvider timeProvider = new DeterministicTimeProvider(DateTimeOffset.UtcNow);

        // OpenAI: declares and requires Seed
        CandidateDefinition openAiDef = baseDef with
        {
            ModelDeployment = baseDef.ModelDeployment with { Provider = "openai", Model = "gpt-4o" },
            ProviderConnectionRevision = baseDef.ProviderConnectionRevision with { Endpoint = "https://api.openai.com/v1" },
            CredentialEnvironmentVariable = "DUMMY_KEY",
        };
        CandidateSnapshot openAiSnapshot = CandidateFreezer.Freeze(openAiDef, dataset, config);
        var (openAiAssignment, openAiRev) = await PinnedCandidateFactory.AssignmentAsync(openAiSnapshot, "DUMMY_KEY", ["database_investigator"], modelRegistry, agentRegistry, timeProvider, CancellationToken.None);
        openAiRev.DeclaredCapabilities.HasFlag(ModelCapability.Seed).ShouldBeTrue();
        openAiAssignment.Classifier.Capabilities.HasFlag(ModelCapability.Seed).ShouldBeTrue();
        openAiAssignment.Classifier.ProviderParameters.ShouldContainKey("seed");

        // Anthropic: declares NO Seed, requires NO Seed, parameters has no seed
        CandidateDefinition anthropicDef = baseDef with
        {
            ModelDeployment = baseDef.ModelDeployment with { Provider = "anthropic", Model = "claude-3-5-sonnet" },
            ProviderConnectionRevision = baseDef.ProviderConnectionRevision with { Endpoint = "https://api.anthropic.com/v1" },
            CredentialEnvironmentVariable = "DUMMY_KEY",
        };
        CandidateSnapshot anthropicSnapshot = CandidateFreezer.Freeze(anthropicDef, dataset, config);
        var (anthropicAssignment, anthropicRev) = await PinnedCandidateFactory.AssignmentAsync(anthropicSnapshot, "DUMMY_KEY", ["database_investigator"], modelRegistry, agentRegistry, timeProvider, CancellationToken.None);
        anthropicRev.DeclaredCapabilities.HasFlag(ModelCapability.Seed).ShouldBeFalse();
        anthropicAssignment.Classifier.Capabilities.HasFlag(ModelCapability.Seed).ShouldBeFalse();
        anthropicAssignment.Classifier.ProviderParameters.ShouldNotContainKey("seed");

        // OpenAI-compatible: declares NO Seed/ToolCalling, requires NO Seed/ToolCalling, parameters has no seed
        CandidateDefinition compatDef = baseDef with
        {
            ModelDeployment = baseDef.ModelDeployment with { Provider = "openai_compatible", Model = "llama-3" },
            ProviderConnectionRevision = baseDef.ProviderConnectionRevision with { Endpoint = "https://localhost:8080/v1" },
            CredentialEnvironmentVariable = "DUMMY_KEY",
        };
        CandidateSnapshot compatSnapshot = CandidateFreezer.Freeze(compatDef, dataset, config);
        var (compatAssignment, compatRev) = await PinnedCandidateFactory.AssignmentAsync(compatSnapshot, "DUMMY_KEY", ["database_investigator"], modelRegistry, agentRegistry, timeProvider, CancellationToken.None);
        compatRev.DeclaredCapabilities.HasFlag(ModelCapability.Seed).ShouldBeFalse();
        compatRev.DeclaredCapabilities.HasFlag(ModelCapability.ToolCalling).ShouldBeFalse();
        compatAssignment.Classifier.Capabilities.HasFlag(ModelCapability.Seed).ShouldBeFalse();
        compatAssignment.Classifier.ProviderParameters.ShouldNotContainKey("seed");
    }

    [Fact]
    public void Candidate_definition_and_snapshot_roundtrip_and_exclude_credentials_from_hash()
    {
        CandidateDefinition candidate = new()
        {
            SchemaVersion = "sre-eval-candidate/v1",
            CandidateId = "openai-test-candidate-2026.09.01",
            Tenant = "eval-tenant",
            ProviderConnectionRevision = new ProviderConnectionRevisionIdentity
            {
                Id = "11111111-1111-1111-1111-111111111111",
                Version = "1",
                Hash = "1111111111111111111111111111111111111111111111111111111111111111",
                Endpoint = "https://api.openai.com/v1",
                Region = "us-east",
                ApiVersion = "2024-06-01",
                Organization = "org-test-123",
            },
            ModelDeployment = new ModelDeploymentIdentity
            {
                Id = "22222222-2222-2222-2222-222222222222",
                Version = "1",
                Hash = "2222222222222222222222222222222222222222222222222222222222222222",
                Provider = "openai",
                Model = "gpt-4o",
            },
            AssignmentRelease = new VersionedIdentity
            {
                Id = "33333333-3333-3333-3333-333333333333",
                Version = "1",
                Hash = "3333333333333333333333333333333333333333333333333333333333333333",
            },
            Releases = new ReleaseIdentity
            {
                Agent = "agents-2026.08.25.1",
                Prompt = "sre-prompts-2026.08.25.1",
                Model = "models-2026.08.25.1",
                Tool = "tools-2026.08.25.1",
                Policy = "policies-2026.08.25.1",
            },
            Generation = new GenerationIdentity
            {
                Temperature = 0.5m,
                MaximumOutputTokens = 2048,
                Seed = 12345,
            },
            CredentialEnvironmentVariable = "SRE_EVAL_OPENAI_KEY",
        };

        string json = JsonSerializer.Serialize(candidate, EvalJsonContext.Default.CandidateDefinition);
        json.ShouldContain("\"endpoint\": \"https://api.openai.com/v1\"");
        json.ShouldContain("\"organization\": \"org-test-123\"");
        json.ShouldContain("\"credential_environment_variable\": \"SRE_EVAL_OPENAI_KEY\"");

        CandidateDefinition? roundtripped = JsonSerializer.Deserialize(json, EvalJsonContext.Default.CandidateDefinition);
        roundtripped.ShouldNotBeNull();
        roundtripped.CandidateId.ShouldBe(candidate.CandidateId);
        roundtripped.ProviderConnectionRevision.Endpoint.ShouldBe("https://api.openai.com/v1");
        roundtripped.ProviderConnectionRevision.Organization.ShouldBe("org-test-123");
        roundtripped.CredentialEnvironmentVariable.ShouldBe("SRE_EVAL_OPENAI_KEY");

        CandidateSnapshot snapshot = CandidateFreezer.Freeze(roundtripped, EvalTestData.Dataset(), EvalTestData.Config());
        string snapshotJson = JsonSerializer.Serialize(snapshot, EvalJsonContext.Default.CandidateSnapshot);
        snapshotJson.ShouldContain("\"endpoint\": \"https://api.openai.com/v1\"");
        snapshotJson.ShouldContain("\"organization\": \"org-test-123\"");
        snapshotJson.ShouldNotContain("credential_environment_variable");
        snapshotJson.ShouldNotContain("SRE_EVAL_OPENAI_KEY");
    }

    [Fact]
    public void Bootstrap_write_path_creates_candidate_specific_baseline_file_and_never_overwrites()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sre-eval-baseline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            CandidateSnapshot snapshot = EvalTestData.Snapshot() with
            {
                CandidateId = "openai-gpt4o-2026.09.01",
                ModelDeployment = EvalTestData.Snapshot().ModelDeployment with
                {
                    Provider = "openai",
                    Model = "gpt-4o",
                },
            };

            EvalAggregate aggregate = new(
                16,
                0,
                0m,
                new MetricDistribution(100, 150),
                new MetricDistribution(1000, 1200),
                new MetricDistribution(200, 250),
                new MetricDistribution(1200, 1450),
                new DecimalMetricDistribution(0.01m, 0.02m),
                new DecimalMetricDistribution(0.95m, 0.98m),
                new Dictionary<string, DecimalMetricDistribution>(StringComparer.Ordinal)
                {
                    ["diagnosis_correctness"] = new(1m, 1m),
                    ["evidence_grounding"] = new(1m, 1m),
                    ["uncertainty_calibration"] = new(1m, 1m),
                    ["actionability"] = new(1m, 1m),
                });

            EvalBaseline baseline = EvalRunner.SynthesizeBaseline(snapshot, aggregate, snapshot.Dataset.Hash[..8]);

            baseline.BaselineId.ShouldStartWith("baseline-openai-gpt4o-2026.09.01-");
            baseline.BaselineId.ShouldNotContain("scripted-smoke");

            string writtenPath = EvalRunner.WriteBaselineFile(baseline, snapshot, tempDir);
            File.Exists(writtenPath).ShouldBeTrue();
            Path.GetFileName(writtenPath).ShouldBe($"{baseline.BaselineId}.json");
            Path.GetFileName(writtenPath).ShouldNotContain("scripted-smoke");

            // Attempting to write again must throw and strictly forbid overwrite
            InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
                EvalRunner.WriteBaselineFile(baseline, snapshot, tempDir));

            error.Message.ShouldContain("already exists");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Missing_baseline_normal_live_run_fails_fast_when_write_baseline_is_not_set()
    {
        string? priorCandidate = Environment.GetEnvironmentVariable("SRE_EVAL_CANDIDATE");
        string? priorBaseline = Environment.GetEnvironmentVariable("SRE_EVAL_BASELINE");
        string? priorWrite = Environment.GetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable);
        string dummyCandidatePath = Path.Combine(Path.GetTempPath(), "dummy-candidate-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            File.WriteAllText(dummyCandidatePath, JsonSerializer.Serialize(EvalTestData.Candidate(), EvalJsonContext.Default.CandidateDefinition));

            Environment.SetEnvironmentVariable("SRE_EVAL_CANDIDATE", dummyCandidatePath);
            Environment.SetEnvironmentVariable("SRE_EVAL_BASELINE", null);
            Environment.SetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable, null);

            EndToEndEvalTests tests = new();
            InvalidOperationException error = Should.Throw<InvalidOperationException>(async () =>
                await tests.Explicit_live_matrix_requires_exact_candidate_and_credentials());

            error.Message.ShouldContain("missing SRE_EVAL_BASELINE");
        }
        finally
        {
            if (File.Exists(dummyCandidatePath))
            {
                File.Delete(dummyCandidatePath);
            }

            Environment.SetEnvironmentVariable("SRE_EVAL_CANDIDATE", priorCandidate);
            Environment.SetEnvironmentVariable("SRE_EVAL_BASELINE", priorBaseline);
            Environment.SetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable, priorWrite);
        }
    }

    [Fact]
    public async Task Bootstrap_run_with_failing_gate_does_not_write_or_promote_baseline()
    {
        string? priorWrite = Environment.GetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable);
        string tempArtifactRoot = Path.Combine(Path.GetTempPath(), "sre-eval-fail-bootstrap-" + Guid.NewGuid().ToString("N"));
        string baselinesDir = Path.Combine(RepositoryPaths.EvalProject, "Baselines");
        string[] baselinesBefore = Directory.Exists(baselinesDir)
            ? Directory.GetFiles(baselinesDir)
            : [];

        try
        {
            Environment.SetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable, "1");

            LoadedDataset dataset = DatasetLoader.Load(Path.Combine(RepositoryPaths.EvalProject, "Datasets", "incidents", "v1"));
            CandidateDefinition candidate = CandidateFreezer.LoadDefinition(Path.Combine(RepositoryPaths.EvalProject, "Candidates", "scripted-2026.08.31.1.json"));
            EvalConfig baseConfig = CandidateFreezer.LoadConfig(Path.Combine(RepositoryPaths.EvalProject, "eval-config.v1.json"));
            EvalConfig impossibleConfig = baseConfig with
            {
                AbsoluteThresholds = baseConfig.AbsoluteThresholds with
                {
                    MinimumQuality = 2.0m,
                },
            };

            EvalBaseline placeholderBaseline = EvalRunner.SynthesizePlaceholderBaseline(candidate, dataset, impossibleConfig);
            EvalRunner runner = new(dataset, candidate, impossibleConfig, placeholderBaseline);

            EvalRunResult result = await runner.RunScriptedSmokeAsync(tempArtifactRoot, TestContext.Current.CancellationToken);

            result.Gate.PromotionEligible.ShouldBeFalse();
            result.Gate.Failures.ShouldContain("absolute_quality");
            result.WrittenBaselinePath.ShouldBeNull();

            string[] baselinesAfter = Directory.Exists(baselinesDir)
                ? Directory.GetFiles(baselinesDir)
                : [];

            baselinesAfter.OrderBy(static x => x, StringComparer.Ordinal)
                .SequenceEqual(baselinesBefore.OrderBy(static x => x, StringComparer.Ordinal))
                .ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalRunner.WriteBaselineEnvironmentVariable, priorWrite);
            if (Directory.Exists(tempArtifactRoot))
            {
                Directory.Delete(tempArtifactRoot, recursive: true);
            }
        }
    }
    [Fact]
    public void Baseline_filename_and_path_generation_strictly_guards_against_path_traversal()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sre-eval-traversal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            CandidateSnapshot maliciousCandidate = EvalTestData.Snapshot() with
            {
                CandidateId = "../../etc/passwd",
                ModelDeployment = EvalTestData.Snapshot().ModelDeployment with
                {
                    Provider = "openai",
                    Model = "gpt-4o",
                },
            };

            EvalAggregate aggregate = new(
                16,
                0,
                0m,
                new MetricDistribution(100, 150),
                new MetricDistribution(1000, 1200),
                new MetricDistribution(200, 250),
                new MetricDistribution(1200, 1450),
                new DecimalMetricDistribution(0.01m, 0.02m),
                new DecimalMetricDistribution(0.95m, 0.98m),
                new Dictionary<string, DecimalMetricDistribution>(StringComparer.Ordinal)
                {
                    ["diagnosis_correctness"] = new(1m, 1m),
                    ["evidence_grounding"] = new(1m, 1m),
                    ["uncertainty_calibration"] = new(1m, 1m),
                    ["actionability"] = new(1m, 1m),
                });

            EvalBaseline baseline = EvalRunner.SynthesizeBaseline(maliciousCandidate, aggregate, maliciousCandidate.Dataset.Hash[..8]);

            baseline.BaselineId.ShouldNotContain("..");
            baseline.BaselineId.ShouldNotContain("/");
            baseline.BaselineId.ShouldNotContain("\\");

            string writtenPath = EvalRunner.WriteBaselineFile(baseline, maliciousCandidate, tempDir);
            File.Exists(writtenPath).ShouldBeTrue();

            string tempDirWithSep = tempDir.EndsWith(Path.DirectorySeparatorChar)
                ? tempDir
                : tempDir + Path.DirectorySeparatorChar;

            writtenPath.StartsWith(tempDirWithSep, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            Path.GetFileName(writtenPath).ShouldNotContain("..");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
    [Theory]
    [InlineData("http://api.openai.com/v1", "must use HTTPS")]
    [InlineData("http://localhost:8080/v1", "must use HTTPS")]
    [InlineData("ftp://api.openai.com/v1", "must use HTTPS")]
    [InlineData("file:///etc/passwd", "must use HTTPS")]
    [InlineData("api/v1/chat", "must be a valid absolute URI")]
    [InlineData("/v1/chat/completions", "must be a valid absolute URI")]
    public void Candidate_with_http_or_relative_or_non_https_endpoint_is_rejected(string endpoint, string expectedError)
    {
        CandidateDefinition candidate = EvalTestData.Candidate() with
        {
            ProviderConnectionRevision = EvalTestData.Candidate().ProviderConnectionRevision with
            {
                Endpoint = endpoint,
            },
        };

        string tempPath = Path.Combine(Path.GetTempPath(), "sre-eval-endpoint-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(candidate, EvalJsonContext.Default.CandidateDefinition));
            InvalidDataException error = Should.Throw<InvalidDataException>(() =>
                CandidateFreezer.LoadDefinition(tempPath));

            error.Message.ShouldContain(expectedError);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Theory]
    [InlineData("env://MY_KEY")]
    [InlineData("../MY_KEY")]
    [InlineData("MY-KEY;DROP TABLE")]
    [InlineData("MY KEY WITH SPACES")]
    [InlineData("KEY$NAME")]
    [InlineData("KEY/NAME")]
    [InlineData("KEY\\NAME")]
    public void Candidate_with_unsafe_credential_env_var_name_is_rejected(string unsafeEnvVarName)
    {
        CandidateDefinition candidate = EvalTestData.Candidate() with
        {
            CredentialEnvironmentVariable = unsafeEnvVarName,
        };

        string tempPath = Path.Combine(Path.GetTempPath(), "sre-eval-env-var-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(candidate, EvalJsonContext.Default.CandidateDefinition));
            InvalidDataException error = Should.Throw<InvalidDataException>(() =>
                CandidateFreezer.LoadDefinition(tempPath));

            error.Message.ShouldContain("Credential environment variable name");
            error.Message.ShouldContain("is invalid");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
