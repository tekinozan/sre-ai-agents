using System.Reflection;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Holds the convention that keeps this assembly out of the ordinary CI run.
/// </summary>
/// <remarks>
/// Every suite here needs a Docker daemon, so every one carries
/// <c>Category=Integration</c> and the default gate filters them out. A class
/// added without the trait would start requiring Docker of everybody running
/// <c>dotnet test</c>, and the failure would look like a broken test rather than
/// a missing attribute.
///
/// This test deliberately carries no trait of its own. That is not an oversight:
/// it is what stops the filtered run from finding zero tests in this assembly,
/// which the test platform reports as a failure.
/// </remarks>
public sealed class SuiteConventionTests
{
    [Fact]
    public void EveryIntegrationSuiteIsTraited()
    {
        Type[] untraited = [.. typeof(SuiteConventionTests).Assembly
            .GetTypes()
            .Where(static type => type != typeof(SuiteConventionTests))
            .Where(static type => type.GetMethods()
                .Any(static method => method.GetCustomAttributes<FactAttribute>().Any()
                    || method.GetCustomAttributes<TheoryAttribute>().Any()))
            .Where(static type => !type.GetCustomAttributes<TraitAttribute>()
                .Any(static trait => trait.Name == "Category" && trait.Value == "Integration"))];

        untraited.ShouldBeEmpty(
            "Every suite needing Docker must carry [Trait(\"Category\", \"Integration\")].");
    }
}
