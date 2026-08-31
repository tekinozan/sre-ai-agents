using SreAiAgent.Infrastructure.Observability.Providers.Kubernetes;
using SreAiAgent.Infrastructure.Observability.Providers.Shared;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// The scope of a Kubernetes read, which is where a model's values reach a request.
/// </summary>
/// <remarks>
/// Unlike PromQL and LogQL, a label selector has no quoting and no escape
/// sequence — <c>a=b,c=d</c> is the whole grammar. Nothing here can be made safe
/// by escaping it, so everything is validated against the identifier rules the
/// API server enforces and refused otherwise. These tests are what says so.
///
/// The adapter around this is not unit tested: it needs an API server, and a
/// stub of the client library would only assert that the mapping code calls the
/// methods the mapping code calls. That gap is deliberate and recorded.
/// </remarks>
public sealed class KubernetesSelectorTests
{
    [Fact]
    public void LabelsBecomeAStableCommaSeparatedSelector()
    {
        string selector = KubernetesSelector.Build(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app"] = "checkout",
            ["app.kubernetes.io/component"] = "api",
        });

        // Ordered, so a query is reproducible and two identical scopes produce
        // one cache key rather than two.
        selector.ShouldBe("app=checkout,app.kubernetes.io/component=api");
    }

    [Theory]
    [InlineData("checkout,app=admin")]   // a second requirement
    [InlineData("checkout admin")]       // whitespace ends the value
    [InlineData("-checkout")]            // must start alphanumeric
    [InlineData("checkout-")]            // and end alphanumeric
    public void AValueThatWouldAddOrInvertARequirementIsRefused(string value)
    {
        // There is no escaping to fall back on here, so the only safe answer is
        // to refuse. Dropping the label instead would widen the selector, which
        // returns pods from outside the scope that was asked for.
        Should.Throw<ObservabilityQueryException>(() =>
            KubernetesSelector.Build(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = value,
            }));
    }

    [Theory]
    [InlineData("app!")]
    [InlineData("app,tier")]
    [InlineData("")]
    public void AnIllegalKeyIsRefused(string key)
        => Should.Throw<ObservabilityQueryException>(() =>
            KubernetesSelector.Build(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = "checkout",
            }));

    [Fact]
    public void AnEmptyValueIsLegalBecauseItMeansThePresenceOfTheLabel()
        => KubernetesSelector.Build(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tier"] = string.Empty,
        }).ShouldBe("tier=");

    [Fact]
    public void ANamespaceOutsideTheConfiguredOneIsRefused()
    {
        Dictionary<string, string> byCluster = new(StringComparer.Ordinal) { ["prod-eu"] = "checkout" };

        // The service account is namespace-scoped, so this would fail at the API
        // server too — but as a forbidden response an agent reads as "no pods",
        // which it would then cite as evidence.
        Should.Throw<ObservabilityQueryException>(() =>
            KubernetesSelector.ResolveNamespace(byCluster, "prod-eu", "kube-system"));
    }

    [Fact]
    public void AnUnconfiguredClusterIsRefused()
        => Should.Throw<ObservabilityQueryException>(() =>
            KubernetesSelector.ResolveNamespace(
                new Dictionary<string, string>(StringComparer.Ordinal), "prod-eu", "checkout"));

    [Fact]
    public void AnEmptyRequestedNamespaceTakesTheConfiguredOne()
    {
        Dictionary<string, string> byCluster = new(StringComparer.Ordinal) { ["prod-eu"] = "checkout" };

        KubernetesSelector.ResolveNamespace(byCluster, "prod-eu", string.Empty).ShouldBe("checkout");
    }
}
