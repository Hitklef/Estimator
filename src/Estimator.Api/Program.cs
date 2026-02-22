using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Estimator.Core.Orchestrator;
using Estimator.Core.Services;
using NLog;
using NLog.Web;
using System.Text.Json;

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Debug("init main");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    #region Options
    builder.Services.Configure<AiSettings>(builder.Configuration.GetSection(AiSettings.SectionName));
    #endregion

    #region Service
    builder.Services.AddSingleton<ModelManager>();
    builder.Services.AddSingleton<LlamaModelService>();

    builder.Services.AddSingleton<ArchitectAgent>();
    builder.Services.AddSingleton<EstimatorAgent>();
    builder.Services.AddSingleton<ReviewerAgent>();

    builder.Services.AddSingleton<AgentOrchestrator>();
    #endregion

    // Add services to the container.
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
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

    app.MapPost("/estimate", async (ProjectRequest request, AgentOrchestrator orchestrator) =>
    {
        if (string.IsNullOrWhiteSpace(request.Description)) 
        {
            return Results.BadRequest("Description is required.");
        }
           
        var resultJson = await orchestrator.RunWorkflowAsync(request.Description);

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var finalResult = JsonSerializer.Deserialize<List<ProjectTask>>(resultJson, options);
            return Results.Ok(new { status = "Success", data = finalResult });
        }
        catch
        {
            return Results.Ok(new { status = "Warning", message = resultJson });
        }
    });

    app.Run();
}
catch (Exception exception)
{
    logger.Error(exception, "Stopped program because of exception");
    throw;
}
finally
{
    LogManager.Shutdown();
}

public record ProjectRequest(string Description);