using Estimator.Api.Services;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Estimator.Core.Orchestrator;
using Estimator.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AiSettings>(builder.Configuration.GetSection(AiSettings.SectionName));

builder.Services.AddSingleton<ModelManager>();
builder.Services.AddSingleton<IAgentModelGateway, LlamaModelService>();
builder.Services.AddSingleton<DecomposerAgent>();
builder.Services.AddSingleton<EstimatorAgent>();
builder.Services.AddSingleton<ValidatorAgent>();
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var modelManager = scope.ServiceProvider.GetRequiredService<ModelManager>();
    await modelManager.EnsureModelDownloadedAsync();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ModelCapacityException ex)
    {
        app.Logger.LogWarning(ex, "Model capacity limit reached while processing request.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                code = ModelCapacityException.ErrorCode,
                message = ex.Message
            });
        }
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogWarning(ex, "Agent response could not be processed.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                message = "The AI agents returned data in an unexpected format. Please retry the request."
            });
        }
    }
    catch (Exception ex) when (!string.IsNullOrWhiteSpace(ex.Message) &&
                               ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
    {
        app.Logger.LogWarning(ex, "Raw NoKvSlot surfaced from LLM layer.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                code = ModelCapacityException.ErrorCode,
                message = ModelCapacityException.DefaultMessage
            });
        }
    }
});

app.MapPost("/estimate", async (ProjectRequest request, AgentOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Description))
    {
        return Results.BadRequest(new { status = "Error", message = "Description is required." });
    }

    var plan = await orchestrator.RunWorkflowAsync(request.Description, cancellationToken);
    return Results.Ok(new { status = "Success", data = plan });
});

app.MapPost("/estimate/document", async (
    HttpRequest request,
    IDocumentTextExtractor extractor,
    AgentOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            status = "Error",
            message = "Request must be multipart/form-data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

    if (file is null)
    {
        return Results.BadRequest(new
        {
            status = "Error",
            message = "A file is required. Use form-data with a file field named 'file'."
        });
    }

    var extraction = await extractor.ExtractTextAsync(file, cancellationToken);
    if (!extraction.IsSuccess)
    {
        return Results.BadRequest(new { status = "Error", message = extraction.Error });
    }

    var plan = await orchestrator.RunWorkflowAsync(extraction.Text, cancellationToken);
    return Results.Ok(new
    {
        status = "Success",
        source = new
        {
            file_name = file.FileName,
            file_extension = extraction.FileExtension,
            extracted_characters = extraction.Text.Length
        },
        data = plan
    });
})
.Accepts<IFormFile>("multipart/form-data")
.DisableAntiforgery();

app.Run();

public sealed record ProjectRequest(string Description);
