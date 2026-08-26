using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Prompting;

/// <summary>
/// Substitutes declared variables into a prompt template and does nothing else
/// (ARCHITECTURE.md 8.1).
/// </summary>
/// <remarks>
/// Deliberately not a template engine. A general engine brings expressions,
/// conditionals, includes and, in several widely used cases, arbitrary code
/// execution. None of that is needed to substitute eight named values, and all of
/// it is reachable from a template somebody edits under pressure during an
/// incident. This renderer scans for <c>{{name}}</c>, looks the name up in a
/// declared set, and refuses anything it does not recognise.
///
/// The rendered text is never logged. A rendered prompt contains the retrieved
/// evidence, which makes it the single richest source of sensitive data in the
/// system; what travels onward is the SHA-256 hash, which identifies the prompt
/// without revealing it.
/// </remarks>
internal sealed class ControlledPromptRenderer : IPromptRenderer
{
    private const int MaxVariableNameLength = 64;

    /// <summary>
    /// Pooled builders. Rendering runs for every model call of every agent, and
    /// prompts reach tens of kilobytes once evidence is folded in; a fresh
    /// StringBuilder per render would push those buffers straight onto the large
    /// object heap.
    /// </summary>
    private static readonly ObjectPool<StringBuilder> BuilderPool =
        new DefaultObjectPoolProvider().CreateStringBuilderPool(
            initialCapacity: 4096,
            maximumRetainedCapacity: 128 * 1024);

    private readonly SreMetrics _metrics;

    public ControlledPromptRenderer(SreMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    /// <inheritdoc />
    public Result<RenderedPrompt> Render(
        PromptDefinition definition,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(variables);

        // Required variables are checked before any work: a prompt rendered with
        // an empty hole where the incident should be produces an agent
        // confidently investigating nothing.
        foreach (string required in definition.RequiredVariables)
        {
            if (!variables.TryGetValue(required, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                _metrics.PromptRenderFailed(definition.Name, ApplicationErrorCodes.PromptRenderFailed.Value);
                return Result<RenderedPrompt>.Failure(Error.Permanent(
                    ApplicationErrorCodes.PromptRenderFailed,
                    $"Prompt '{definition.Reference}' requires variable '{required}', which was missing or empty."));
            }
        }

        Result<string> system = Substitute(definition, definition.SystemTemplate, variables);
        if (system.IsFailure)
        {
            _metrics.PromptRenderFailed(definition.Name, system.Error.Code.Value);
            return Result<RenderedPrompt>.Failure(system.Error);
        }

        Result<string> user = Substitute(definition, definition.UserTemplate, variables);
        if (user.IsFailure)
        {
            _metrics.PromptRenderFailed(definition.Name, user.Error.Code.Value);
            return Result<RenderedPrompt>.Failure(user.Error);
        }

        int totalLength = system.Value.Length + user.Value.Length;
        if (totalLength > definition.MaxRenderedCharacters)
        {
            // Enforced here rather than left to the provider's context limit.
            // Discovering the overflow at the provider costs a paid round trip
            // and returns an error that says nothing about which prompt or which
            // evidence caused it.
            _metrics.PromptRenderFailed(definition.Name, ApplicationErrorCodes.PromptRenderFailed.Value);
            return Result<RenderedPrompt>.Failure(Error.Permanent(
                ApplicationErrorCodes.PromptRenderFailed,
                $"Prompt '{definition.Reference}' rendered to "
                + $"{totalLength.ToString(CultureInfo.InvariantCulture)} characters, over its limit of "
                + $"{definition.MaxRenderedCharacters.ToString(CultureInfo.InvariantCulture)}."));
        }

        return Result<RenderedPrompt>.Success(new RenderedPrompt(
            System: system.Value,
            User: user.Value,
            Hash: ComputeHash(system.Value, user.Value),
            CharacterCount: totalLength));
    }

    /// <summary>
    /// Walks the template once, copying literal spans and replacing recognised
    /// placeholders.
    /// </summary>
    /// <remarks>
    /// A single forward scan over spans, with no regex and no intermediate
    /// strings. The alternative, a chain of <c>string.Replace</c> calls, allocates
    /// a full copy of the template per variable.
    /// </remarks>
    private static Result<string> Substitute(
        PromptDefinition definition,
        string template,
        IReadOnlyDictionary<string, string> variables)
    {
        ReadOnlySpan<char> remaining = template;
        int openIndex = remaining.IndexOf("{{", StringComparison.Ordinal);
        if (openIndex < 0)
        {
            return Result<string>.Success(template);
        }

        StringBuilder builder = BuilderPool.Get();
        try
        {
            while (openIndex >= 0)
            {
                builder.Append(remaining[..openIndex]);
                ReadOnlySpan<char> afterOpen = remaining[(openIndex + 2)..];

                int closeIndex = afterOpen.IndexOf("}}", StringComparison.Ordinal);
                if (closeIndex < 0)
                {
                    // An unclosed placeholder means the template is malformed.
                    // Emitting the rest verbatim would ship "{{incident_title"
                    // to a model as if it were prose.
                    return Result<string>.Failure(Error.Permanent(
                        ApplicationErrorCodes.PromptRenderFailed,
                        $"Prompt '{definition.Reference}' has an unclosed '{{{{' placeholder."));
                }

                ReadOnlySpan<char> name = afterOpen[..closeIndex].Trim();
                if (name.Length is 0 or > MaxVariableNameLength)
                {
                    return Result<string>.Failure(Error.Permanent(
                        ApplicationErrorCodes.PromptRenderFailed,
                        $"Prompt '{definition.Reference}' has a placeholder with an invalid name."));
                }

                string variableName = name.ToString();

                if (!definition.RequiredVariables.Contains(variableName)
                    && !definition.OptionalVariables.Contains(variableName))
                {
                    // The allowlist is the whole point. A template referencing an
                    // undeclared name would otherwise pick up whatever the caller
                    // happened to pass, which is how unrelated context leaks into
                    // a prompt.
                    return Result<string>.Failure(Error.Permanent(
                        ApplicationErrorCodes.PromptRenderFailed,
                        $"Prompt '{definition.Reference}' references undeclared variable '{variableName}'. "
                        + "Declare it in required_variables or optional_variables."));
                }

                if (variables.TryGetValue(variableName, out string? value))
                {
                    builder.Append(value);
                }

                remaining = afterOpen[(closeIndex + 2)..];
                openIndex = remaining.IndexOf("{{", StringComparison.Ordinal);
            }

            builder.Append(remaining);
            return Result<string>.Success(builder.ToString());
        }
        finally
        {
            BuilderPool.Return(builder);
        }
    }

    private static string ComputeHash(string system, string user)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(system.Length + user.Length + 1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxBytes);

        try
        {
            int written = Encoding.UTF8.GetBytes(system, buffer);
            buffer[written++] = 0x1F;
            written += Encoding.UTF8.GetBytes(user, buffer.AsSpan(written));

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(buffer.AsSpan(0, written), digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            // Cleared: the buffer held the rendered prompt, and a pooled array
            // handed to the next caller with that content still in it is exactly
            // the kind of leak this codebase is trying to avoid.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
