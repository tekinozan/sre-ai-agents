namespace SreAiAgent.Evals.Harness;

#pragma warning disable CA1711 // "Collection" is xUnit's convention here.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EvalEnvironmentCollection
{
    public const string Name = "EvalEnvironment";
}
#pragma warning restore CA1711
