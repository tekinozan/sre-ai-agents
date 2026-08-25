namespace SreAiAgent.Domain.Evidence;

/// <summary>Where a piece of evidence came from (ARCHITECTURE.md 5.3).</summary>
public enum EvidenceSourceType
{
    Metric = 0,
    Log = 1,
    Trace = 2,
    Kubernetes = 3,
    Database = 4,
    Deployment = 5,
    Runbook = 6,
}
