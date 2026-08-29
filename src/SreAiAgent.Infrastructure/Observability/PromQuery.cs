using System.Text;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Builds the label matcher that scopes a metric query, and nothing else.
/// </summary>
/// <remarks>
/// This is the only place anything a model supplied reaches a query language, so
/// it is deliberately a small type of its own rather than a private helper on the
/// provider: it can be read in one sitting and tested directly.
///
/// The tool schema constrains label values to 256 characters and nothing more, so
/// a value arriving here can contain quotes, backslashes and newlines. Injected
/// log content reaching a model is an explicit threat in ARCHITECTURE.md 15.4, and
/// "close the matcher and open another" is the obvious thing to try.
/// </remarks>
internal static class PromQuery
{
    /// <summary>The token an expression uses to say where the matcher goes.</summary>
    public const string SelectorPlaceholder = "{{selector}}";

    /// <summary>
    /// Label names the platform owns. A model-supplied label may not set one.
    /// </summary>
    /// <remarks>
    /// The policy engine checks the <c>service</c> argument against the execution
    /// allowlist, but it has no reason to look inside <c>labels</c>. Without this,
    /// an agent restricted to one service could pass
    /// <c>labels: { service: "payments" }</c> and read another one's metrics with
    /// every authorization check having passed.
    /// </remarks>
    private static readonly string[] ReservedLabels = ["service", "environment"];

    /// <summary>
    /// Renders an expression template against a scope.
    /// </summary>
    /// <exception cref="ObservabilityQueryException">
    /// A label name is not a legal PromQL identifier, a label value contains a
    /// control character, or the template has no placeholder to scope.
    /// </exception>
    public static string Render(
        string expression,
        string service,
        string environment,
        IReadOnlyDictionary<string, string> labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        if (!expression.Contains(SelectorPlaceholder, StringComparison.Ordinal))
        {
            // Rendering it anyway would produce a valid query with no matcher,
            // which answers for every service in every environment at once.
            throw new ObservabilityQueryException(
                $"Expression '{expression}' has no {SelectorPlaceholder} and would not be scoped to a service.");
        }

        return expression.Replace(
            SelectorPlaceholder,
            BuildSelector(service, environment, labels),
            StringComparison.Ordinal);
    }

    /// <summary>Builds <c>{service="a",environment="b",k="v"}</c>, fully escaped.</summary>
    public static string BuildSelector(
        string service,
        string environment,
        IReadOnlyDictionary<string, string> labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentNullException.ThrowIfNull(labels);

        StringBuilder builder = new(64);
        builder.Append("{service=\"").Append(EscapeValue(service, "service"))
            .Append("\",environment=\"").Append(EscapeValue(environment, "environment"))
            .Append('"');

        foreach ((string name, string value) in labels.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (Array.Exists(ReservedLabels, reserved => string.Equals(reserved, name, StringComparison.Ordinal)))
            {
                throw new ObservabilityQueryException(
                    $"Label '{name}' is set by the platform and cannot be supplied as a filter. Overriding it "
                    + "would read a scope the execution allowlist never granted.");
            }

            if (!IsLegalLabelName(name))
            {
                // Dropping it instead would widen the query rather than narrow
                // it, which is the wrong direction to fail in.
                throw new ObservabilityQueryException(
                    $"Label name '{name}' is not a legal identifier. Expected [a-zA-Z_][a-zA-Z0-9_]*.");
            }

            builder.Append(',').Append(name).Append("=\"").Append(EscapeValue(value, name)).Append('"');
        }

        return builder.Append('}').ToString();
    }

    private static bool IsLegalLabelName(string name)
    {
        if (name.Length == 0 || (!char.IsAsciiLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapeValue(string value, string name)
    {
        StringBuilder escaped = new(value.Length + 8);

        foreach (char character in value)
        {
            // Prometheus would accept \n and \t as escapes, but a control
            // character in a value a model produced is not something to
            // faithfully forward. Refusing keeps the accepted alphabet small
            // enough to reason about.
            if (char.IsControl(character))
            {
                throw new ObservabilityQueryException(
                    $"Label '{name}' has a control character in its value.");
            }

            if (character is '\\' or '"')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}

/// <summary>
/// A query that could not be built safely.
/// </summary>
/// <remarks>
/// Thrown rather than returned because the observability ports answer with a
/// result type that has no failure case: they describe what a backend said, and
/// "this question cannot be asked safely" is not an answer. The tool gateway
/// catches it and records a failed tool call with the reason.
/// </remarks>
internal sealed class ObservabilityQueryException : Exception
{
    public ObservabilityQueryException(string message)
        : base(message)
    {
    }

    public ObservabilityQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ObservabilityQueryException()
    {
    }
}
