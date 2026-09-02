using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal interface IProviderRevisionReader
{
    Task<ProviderConnectionRevision?> FindRevisionAsync(
        string tenant,
        ProviderConnectionRevisionId revisionId,
        CancellationToken cancellationToken);
}
