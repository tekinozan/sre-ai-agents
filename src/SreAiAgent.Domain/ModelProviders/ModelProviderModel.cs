using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.ModelProviders;

public enum ProviderAdapterKind
{
    Scripted = 0,
    OpenAi = 1,
    OpenAiCompatible = 2,
    AzureOpenAi = 3,
    Anthropic = 4,
    Gemini = 5,
}

[Flags]
public enum ModelCapability
{
    None = 0,
    StructuredOutput = 1 << 0,
    SystemMessage = 1 << 1,
    Seed = 1 << 2,
    ToolCalling = 1 << 3,
    ParallelToolCalls = 1 << 4,
}

public sealed record ProviderConnection
{
    private ProviderConnection(
        ProviderConnectionId id,
        string tenant,
        string displayName,
        ProviderConnectionRevisionId? activeRevisionId,
        DateTimeOffset createdAt,
        DateTimeOffset? archivedAt)
    {
        Id = id;
        Tenant = Required(tenant, nameof(tenant));
        DisplayName = Required(displayName, nameof(displayName));
        ActiveRevisionId = activeRevisionId;
        CreatedAt = createdAt;
        ArchivedAt = archivedAt;
    }

    public ProviderConnectionId Id { get; }
    public string Tenant { get; }
    public string DisplayName { get; private init; }
    public ProviderConnectionRevisionId? ActiveRevisionId { get; private init; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ArchivedAt { get; }

    public static ProviderConnection Create(string tenant, string displayName, DateTimeOffset createdAt)
        => new(ProviderConnectionId.New(), tenant, displayName, null, createdAt, null);

    public static ProviderConnection Restore(
        ProviderConnectionId id,
        string tenant,
        string displayName,
        ProviderConnectionRevisionId? activeRevisionId,
        DateTimeOffset createdAt,
        DateTimeOffset? archivedAt)
        => new(id, tenant, displayName, activeRevisionId, createdAt, archivedAt);

    public ProviderConnection WithActiveRevision(ProviderConnectionRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.ProviderConnectionId != Id || !string.Equals(revision.Tenant, Tenant, StringComparison.Ordinal))
        {
            throw Invalid("An active revision must belong to the same tenant and provider connection.");
        }

        return this with { ActiveRevisionId = revision.Id };
    }

    public ProviderConnection Rename(string displayName) => this with { DisplayName = Required(displayName, nameof(displayName)) };

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw Invalid($"'{name}' is required.") : value;

    private static DomainException Invalid(string message)
        => new(DomainErrorCodes.ModelConfigurationInvalid, message);
}

public sealed record ProviderConnectionRevision
{
    private ProviderConnectionRevision(
        ProviderConnectionRevisionId id,
        ProviderConnectionId providerConnectionId,
        string tenant,
        int revision,
        ProviderAdapterKind adapterKind,
        string endpoint,
        string? region,
        string? apiVersion,
        string? organization,
        string credentialRef,
        ModelCapability declaredCapabilities,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
    {
        Id = id;
        ProviderConnectionId = providerConnectionId;
        Tenant = Required(tenant, nameof(tenant));
        Revision = revision > 0 ? revision : throw Invalid("Revision must be positive.");
        AdapterKind = Enum.IsDefined(adapterKind) ? adapterKind : throw Invalid("Adapter kind is invalid.");
        Endpoint = Required(endpoint, nameof(endpoint));
        Region = EmptyToNull(region);
        ApiVersion = EmptyToNull(apiVersion);
        Organization = EmptyToNull(organization);
        CredentialRef = Required(credentialRef, nameof(credentialRef));
        DeclaredCapabilities = declaredCapabilities;
        ConfigurationHash = configurationHash;
        CreatedAt = createdAt;
        CreatedBy = Required(createdBy, nameof(createdBy));
    }

    public ProviderConnectionRevisionId Id { get; }
    public ProviderConnectionId ProviderConnectionId { get; }
    public string Tenant { get; }
    public int Revision { get; }
    public ProviderAdapterKind AdapterKind { get; }
    public string Endpoint { get; }
    public string? Region { get; }
    public string? ApiVersion { get; }
    public string? Organization { get; }
    public string CredentialRef { get; }
    public ModelCapability DeclaredCapabilities { get; }
    public string ConfigurationHash { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CreatedBy { get; }

    public static ProviderConnectionRevision Create(
        ProviderConnectionId providerConnectionId,
        string tenant,
        int revision,
        ProviderAdapterKind adapterKind,
        string endpoint,
        string? region,
        string? apiVersion,
        string? organization,
        string credentialRef,
        ModelCapability declaredCapabilities,
        DateTimeOffset createdAt,
        string createdBy)
    {
        string canonical = string.Join('|',
            tenant, providerConnectionId.ToString(), revision.ToString(CultureInfo.InvariantCulture), adapterKind,
            endpoint, region, apiVersion, organization, credentialRef, ((int)declaredCapabilities).ToString(CultureInfo.InvariantCulture));
        return new(
            ProviderConnectionRevisionId.New(), providerConnectionId, tenant, revision, adapterKind, endpoint,
            region, apiVersion, organization, credentialRef, declaredCapabilities, Hash(canonical), createdAt, createdBy);
    }

    public static ProviderConnectionRevision Restore(
        ProviderConnectionRevisionId id,
        ProviderConnectionId providerConnectionId,
        string tenant,
        int revision,
        ProviderAdapterKind adapterKind,
        string endpoint,
        string? region,
        string? apiVersion,
        string? organization,
        string credentialRef,
        ModelCapability declaredCapabilities,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
        => new(id, providerConnectionId, tenant, revision, adapterKind, endpoint, region, apiVersion,
            organization, credentialRef, declaredCapabilities, configurationHash, createdAt, createdBy);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw Invalid($"'{name}' is required.") : value;
    private static DomainException Invalid(string message) => new(DomainErrorCodes.ModelConfigurationInvalid, message);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record ModelDeploymentVersion
{
    private ModelDeploymentVersion(
        ModelDeploymentVersionId id,
        string tenant,
        string logicalName,
        int version,
        ProviderConnectionRevisionId providerConnectionRevisionId,
        string providerModel,
        ModelCapability requiredCapabilities,
        float temperature,
        int maxOutputTokens,
        TimeSpan timeout,
        decimal inputCostPerMillionTokens,
        decimal outputCostPerMillionTokens,
        string currency,
        IReadOnlyDictionary<string, string> providerParameters,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
    {
        Id = id;
        Tenant = Required(tenant, nameof(tenant));
        LogicalName = Required(logicalName, nameof(logicalName));
        Version = version > 0 ? version : throw Invalid("Version must be positive.");
        ProviderConnectionRevisionId = providerConnectionRevisionId;
        ProviderModel = Required(providerModel, nameof(providerModel));
        RequiredCapabilities = requiredCapabilities;
        Temperature = temperature is >= 0 and <= 2 ? temperature : throw Invalid("Temperature must be between 0 and 2.");
        MaxOutputTokens = maxOutputTokens > 0 ? maxOutputTokens : throw Invalid("Max output tokens must be positive.");
        Timeout = timeout > TimeSpan.Zero ? timeout : throw Invalid("Timeout must be positive.");
        InputCostPerMillionTokens = inputCostPerMillionTokens >= 0 ? inputCostPerMillionTokens : throw Invalid("Input cost cannot be negative.");
        OutputCostPerMillionTokens = outputCostPerMillionTokens >= 0 ? outputCostPerMillionTokens : throw Invalid("Output cost cannot be negative.");
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        ProviderParameters = providerParameters.ToFrozenDictionary(StringComparer.Ordinal);
        ConfigurationHash = configurationHash;
        CreatedAt = createdAt;
        CreatedBy = Required(createdBy, nameof(createdBy));
    }

    public ModelDeploymentVersionId Id { get; }
    public string Tenant { get; }
    public string LogicalName { get; }
    public int Version { get; }
    public ProviderConnectionRevisionId ProviderConnectionRevisionId { get; }
    public string ProviderModel { get; }
    public ModelCapability RequiredCapabilities { get; }
    public float Temperature { get; }
    public int MaxOutputTokens { get; }
    public TimeSpan Timeout { get; }
    public decimal InputCostPerMillionTokens { get; }
    public decimal OutputCostPerMillionTokens { get; }
    public string Currency { get; }
    public IReadOnlyDictionary<string, string> ProviderParameters { get; }
    public string ConfigurationHash { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CreatedBy { get; }

    public decimal EstimateCost(long inputTokens, long outputTokens)
        => ((inputTokens * InputCostPerMillionTokens) + (outputTokens * OutputCostPerMillionTokens)) / 1_000_000m;

    public static ModelDeploymentVersion Create(
        string tenant,
        string logicalName,
        int version,
        ProviderConnectionRevisionId providerConnectionRevisionId,
        string providerModel,
        ModelCapability requiredCapabilities,
        float temperature,
        int maxOutputTokens,
        TimeSpan timeout,
        decimal inputCostPerMillionTokens,
        decimal outputCostPerMillionTokens,
        string currency,
        IReadOnlyDictionary<string, string>? providerParameters,
        DateTimeOffset createdAt,
        string createdBy)
    {
        IReadOnlyDictionary<string, string> parameters = providerParameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        string canonicalParameters = string.Join(';', parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + pair.Value));
        string canonical = string.Join('|', tenant, logicalName, version.ToString(CultureInfo.InvariantCulture), providerConnectionRevisionId,
            providerModel, ((int)requiredCapabilities).ToString(CultureInfo.InvariantCulture), temperature.ToString(CultureInfo.InvariantCulture),
            maxOutputTokens.ToString(CultureInfo.InvariantCulture), timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
            inputCostPerMillionTokens.ToString(CultureInfo.InvariantCulture), outputCostPerMillionTokens.ToString(CultureInfo.InvariantCulture), currency, canonicalParameters);
        return new(
            ModelDeploymentVersionId.New(), tenant, logicalName, version, providerConnectionRevisionId, providerModel,
            requiredCapabilities, temperature, maxOutputTokens, timeout, inputCostPerMillionTokens, outputCostPerMillionTokens,
            currency, parameters, Hash(canonical), createdAt, createdBy);
    }

    public static ModelDeploymentVersion Restore(
        ModelDeploymentVersionId id,
        string tenant,
        string logicalName,
        int version,
        ProviderConnectionRevisionId providerConnectionRevisionId,
        string providerModel,
        ModelCapability requiredCapabilities,
        float temperature,
        int maxOutputTokens,
        TimeSpan timeout,
        decimal inputCostPerMillionTokens,
        decimal outputCostPerMillionTokens,
        string currency,
        IReadOnlyDictionary<string, string> providerParameters,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
        => new(id, tenant, logicalName, version, providerConnectionRevisionId, providerModel,
            requiredCapabilities, temperature, maxOutputTokens, timeout, inputCostPerMillionTokens,
            outputCostPerMillionTokens, currency, providerParameters, configurationHash, createdAt, createdBy);

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw Invalid($"'{name}' is required.") : value;
    private static DomainException Invalid(string message) => new(DomainErrorCodes.ModelConfigurationInvalid, message);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record ModelAssignmentRelease
{
    private ModelAssignmentRelease(
        ModelAssignmentReleaseId id,
        string tenant,
        string name,
        int version,
        ModelDeploymentVersionId classifierDeploymentVersionId,
        ModelDeploymentVersionId correlatorDeploymentVersionId,
        IReadOnlyDictionary<string, ModelDeploymentVersionId> agentDeploymentVersionIds,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
    {
        Id = id;
        Tenant = Required(tenant, nameof(tenant));
        Name = Required(name, nameof(name));
        Version = version > 0 ? version : throw Invalid("Version must be positive.");
        ClassifierDeploymentVersionId = classifierDeploymentVersionId;
        CorrelatorDeploymentVersionId = correlatorDeploymentVersionId;
        AgentDeploymentVersionIds = agentDeploymentVersionIds.Count > 0
            ? agentDeploymentVersionIds.ToFrozenDictionary(StringComparer.Ordinal)
            : throw Invalid("At least one agent slot is required.");
        ConfigurationHash = configurationHash;
        CreatedAt = createdAt;
        CreatedBy = Required(createdBy, nameof(createdBy));
    }

    public ModelAssignmentReleaseId Id { get; }
    public string Tenant { get; }
    public string Name { get; }
    public int Version { get; }
    public ModelDeploymentVersionId ClassifierDeploymentVersionId { get; }
    public ModelDeploymentVersionId CorrelatorDeploymentVersionId { get; }
    public IReadOnlyDictionary<string, ModelDeploymentVersionId> AgentDeploymentVersionIds { get; }
    public string ConfigurationHash { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CreatedBy { get; }

    public static ModelAssignmentRelease Create(
        string tenant,
        string name,
        int version,
        ModelDeploymentVersionId classifierDeploymentVersionId,
        ModelDeploymentVersionId correlatorDeploymentVersionId,
        IReadOnlyDictionary<string, ModelDeploymentVersionId> agentDeploymentVersionIds,
        DateTimeOffset createdAt,
        string createdBy)
    {
        ArgumentNullException.ThrowIfNull(agentDeploymentVersionIds);
        string slots = string.Join(';', agentDeploymentVersionIds.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "=" + pair.Value));
        string canonical = string.Join('|', tenant, name, version.ToString(CultureInfo.InvariantCulture), classifierDeploymentVersionId,
            correlatorDeploymentVersionId, slots);
        return new(
            ModelAssignmentReleaseId.New(), tenant, name, version, classifierDeploymentVersionId, correlatorDeploymentVersionId,
            agentDeploymentVersionIds, Hash(canonical), createdAt, createdBy);
    }

    public static ModelAssignmentRelease Restore(
        ModelAssignmentReleaseId id,
        string tenant,
        string name,
        int version,
        ModelDeploymentVersionId classifierDeploymentVersionId,
        ModelDeploymentVersionId correlatorDeploymentVersionId,
        IReadOnlyDictionary<string, ModelDeploymentVersionId> agentDeploymentVersionIds,
        string configurationHash,
        DateTimeOffset createdAt,
        string createdBy)
        => new(id, tenant, name, version, classifierDeploymentVersionId, correlatorDeploymentVersionId,
            agentDeploymentVersionIds, configurationHash, createdAt, createdBy);

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw Invalid($"'{name}' is required.") : value;
    private static DomainException Invalid(string message) => new(DomainErrorCodes.ModelConfigurationInvalid, message);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
