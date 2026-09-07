using SreAiAgent.InvestigationWorker;

WebApplication app = InvestigationWorkerApplication.CreateApp(args);
await app.RunAsync().ConfigureAwait(false);
