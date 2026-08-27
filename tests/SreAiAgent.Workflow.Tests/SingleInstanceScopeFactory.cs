using Microsoft.Extensions.DependencyInjection;
using SreAiAgent.Application.Abstractions.Workflow;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// A scope factory that always resolves the same activities instance.
/// </summary>
/// <remarks>
/// The in-process fan-out takes a scope per agent so that each one gets its own
/// DbContext. A test asserting on ordering and merging needs the opposite: one
/// instance that records every call, whichever scope asked for it.
/// </remarks>
internal sealed class SingleInstanceScopeFactory(IInvestigationActivities activities)
    : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public IServiceScope CreateScope() => this;

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType)
        => serviceType == typeof(IInvestigationActivities) ? activities : null;

    public void Dispose()
    {
        // The instance outlives every scope handed out here, so there is nothing
        // to release.
    }
}
