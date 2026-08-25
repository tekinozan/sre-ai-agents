using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Incidents;

/// <summary>
/// Raised when an operation is attempted from a state that does not permit it,
/// for example starting an investigation on a cancelled incident.
/// </summary>
public sealed class InvalidIncidentStateException : DomainException
{
    public InvalidIncidentStateException(IncidentStatus currentStatus, string operation)
        : base(
            DomainErrorCodes.IncidentInvalidState,
            $"Operation '{operation}' is not permitted while the incident is {currentStatus}.")
    {
        CurrentStatus = currentStatus;
        Operation = operation;
    }

    /// <summary>The state the incident was actually in.</summary>
    public IncidentStatus CurrentStatus { get; }

    /// <summary>The rejected operation.</summary>
    public string Operation { get; }
}
