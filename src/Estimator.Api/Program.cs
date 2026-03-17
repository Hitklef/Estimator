using Estimator.Core.Agents;
using Estimator.Core.Models.Options;
using Estimator.Core.Orchestrator;
using Estimator.Core.Services;
using NLog;
using NLog.Web;

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Debug("API bootstrap started.");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    builder.Services.Configure<AiSettings>(builder.Configuration.GetSection(AiSettings.SectionName));

    builder.Services.AddSingleton<ModelManager>();
    builder.Services.AddSingleton<IAgentModelGateway, LlamaModelService>();
    builder.Services.AddSingleton<DecomposerAgent>();
    builder.Services.AddSingleton<EstimatorAgent>();
    builder.Services.AddSingleton<ValidatorAgent>();
    builder.Services.AddSingleton<IEstimationPolicy, EstimationPolicy>();
    builder.Services.AddSingleton<AgentOrchestrator>();

    builder.Services.AddOpenApi();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var modelManager = scope.ServiceProvider.GetRequiredService<ModelManager>();
        await modelManager.EnsureModelDownloadedAsync();
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseHttpsRedirection();

    app.MapPost("/estimate", async (ProjectRequest request, AgentOrchestrator orchestrator, CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            return Results.BadRequest(new { status = "Error", message = "Description is required." });
        }

        var plan = await orchestrator.RunWorkflowAsync(request.Description, cancellationToken);
        return Results.Ok(new { status = "Success", data = plan });
    });

    app.Run();
}
catch (Exception exception)
{
    logger.Error(exception, "Application stopped due to an exception.");
    throw;
}
finally
{
    LogManager.Shutdown();
}

public sealed record ProjectRequest(string Description);
