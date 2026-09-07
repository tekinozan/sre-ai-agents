using SreAiAgent.AlertWorker;

WebApplication app = AlertWorkerApplication.CreateApp(args);
await app.RunAsync().ConfigureAwait(false);
