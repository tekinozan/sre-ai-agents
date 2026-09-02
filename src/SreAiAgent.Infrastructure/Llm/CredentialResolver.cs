using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Llm;

internal sealed class ExternalReferenceCredentialResolver(IOptions<LlmOptions> options) : ICredentialResolver
{
    internal const string EnvironmentPrefix = "env://";
    internal const string FilePrefix = "file://";
    private readonly string _credentialRoot = Path.GetFullPath(options.Value.CredentialFileRoot);

    public async ValueTask<Result<CredentialValue>> ResolveAsync(
        string credentialReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialReference))
        {
            return Missing();
        }

        if (credentialReference.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
        {
            string name = credentialReference[EnvironmentPrefix.Length..];
            if (!IsSafeEnvironmentName(name))
            {
                return Missing();
            }

            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value)
                ? Missing()
                : Result<CredentialValue>.Success(new CredentialValue(value));
        }

        if (credentialReference.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            string relative = credentialReference[FilePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            string path = Path.GetFullPath(Path.Combine(_credentialRoot, relative));
            string rootPrefix = _credentialRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _credentialRoot
                : _credentialRoot + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(path))
            {
                return Missing();
            }

            string value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(value)
                ? Missing()
                : Result<CredentialValue>.Success(new CredentialValue(value));
        }

        return Missing();
    }

    private static bool IsSafeEnvironmentName(string value)
        => value.Length is > 0 and <= 128
           && value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static Result<CredentialValue> Missing()
        => Result<CredentialValue>.Failure(Error.Permanent(
            ApplicationErrorCodes.CredentialUnavailable,
            "The configured credential reference could not be resolved."));
}
