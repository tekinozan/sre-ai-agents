using System.Text.Json.Serialization;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.ModelProviders;

[JsonConverter(typeof(StronglyTypedIdJsonConverter<ProviderConnectionId>))]
public readonly record struct ProviderConnectionId(Guid Value) : IStronglyTypedId<ProviderConnectionId>
{
    public static ProviderConnectionId New() => new(Guid.CreateVersion7());
    public static ProviderConnectionId From(Guid value) => new(value);
    public static ProviderConnectionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));
    public static bool TryParse(string? s, IFormatProvider? provider, out ProviderConnectionId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(StronglyTypedIdJsonConverter<ProviderConnectionRevisionId>))]
public readonly record struct ProviderConnectionRevisionId(Guid Value) : IStronglyTypedId<ProviderConnectionRevisionId>
{
    public static ProviderConnectionRevisionId New() => new(Guid.CreateVersion7());
    public static ProviderConnectionRevisionId From(Guid value) => new(value);
    public static ProviderConnectionRevisionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));
    public static bool TryParse(string? s, IFormatProvider? provider, out ProviderConnectionRevisionId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(StronglyTypedIdJsonConverter<ModelDeploymentVersionId>))]
public readonly record struct ModelDeploymentVersionId(Guid Value) : IStronglyTypedId<ModelDeploymentVersionId>
{
    public static ModelDeploymentVersionId New() => new(Guid.CreateVersion7());
    public static ModelDeploymentVersionId From(Guid value) => new(value);
    public static ModelDeploymentVersionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));
    public static bool TryParse(string? s, IFormatProvider? provider, out ModelDeploymentVersionId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(StronglyTypedIdJsonConverter<ModelAssignmentReleaseId>))]
public readonly record struct ModelAssignmentReleaseId(Guid Value) : IStronglyTypedId<ModelAssignmentReleaseId>
{
    public static ModelAssignmentReleaseId New() => new(Guid.CreateVersion7());
    public static ModelAssignmentReleaseId From(Guid value) => new(value);
    public static ModelAssignmentReleaseId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));
    public static bool TryParse(string? s, IFormatProvider? provider, out ModelAssignmentReleaseId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

[JsonConverter(typeof(StronglyTypedIdJsonConverter<ModelAssignmentEvaluationId>))]
public readonly record struct ModelAssignmentEvaluationId(Guid Value) : IStronglyTypedId<ModelAssignmentEvaluationId>
{
    public static ModelAssignmentEvaluationId New() => new(Guid.CreateVersion7());
    public static ModelAssignmentEvaluationId From(Guid value) => new(value);
    public static ModelAssignmentEvaluationId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));
    public static bool TryParse(string? s, IFormatProvider? provider, out ModelAssignmentEvaluationId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
