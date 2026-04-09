using Estimator.Api.Services;
using Estimator.Core.Agents;
using Estimator.Core.Models;
using Estimator.Core.Models.Options;
using Estimator.Core.Orchestrator;
using Estimator.Core.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AiSettings>(builder.Configuration.GetSection(AiSettings.SectionName));

builder.Services.AddSingleton<ModelManager>();
builder.Services.AddSingleton<ModelPromptFormatter>();
builder.Services.AddSingleton<ModelOutputConsoleTracer>();
builder.Services.AddSingleton<LlamaModelService>();
builder.Services.AddSingleton<LlamaCppServerManager>();
builder.Services.AddSingleton<LlamaCppHttpModelService>();
builder.Services.AddSingleton<OllamaModelService>();
builder.Services.AddSingleton<IAgentModelGateway>(provider =>
{
    var settings = provider.GetRequiredService<IOptions<AiSettings>>().Value;
    if (settings.UseLlamaCppServer)
    {
        return provider.GetRequiredService<LlamaCppHttpModelService>();
    }

    if (settings.UseOllama)
    {
        return provider.GetRequiredService<OllamaModelService>();
    }

    if (settings.UseLLamaSharp)
    {
        return provider.GetRequiredService<LlamaModelService>();
    }

    throw new InvalidOperationException($"Unsupported ModelProvider '{settings.ModelProvider}'.");
});
builder.Services.AddSingleton<DecomposerAgent>();
builder.Services.AddSingleton<EstimatorAgent>();
builder.Services.AddSingleton<ValidatorAgent>();
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<IEstimateSessionStore, InMemoryEstimateSessionStore>();

var app = builder.Build();

var runtimeSettings = app.Services.GetRequiredService<IOptions<AiSettings>>().Value;
if (runtimeSettings.UseLlamaCppServer)
{
    var serverManager = app.Services.GetRequiredService<LlamaCppServerManager>();
    app.Lifetime.ApplicationStopping.Register(serverManager.Stop);
}

using (var scope = app.Services.CreateScope())
{
    var modelManager = scope.ServiceProvider.GetRequiredService<ModelManager>();
    await modelManager.EnsureModelReadyAsync();
}

app.UseHttpsRedirection();

static CancellationTokenSource CreateWorkflowTimeoutCts(CancellationToken requestToken, int workflowTimeoutSeconds)
{
    var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
    if (workflowTimeoutSeconds > 0)
    {
        linked.CancelAfter(TimeSpan.FromSeconds(workflowTimeoutSeconds));
    }

    return linked;
}

app.Use(async (context, next) =>
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var requestId = context.TraceIdentifier;
    app.Logger.LogInformation(
        "Request started. RequestId={RequestId} Method={Method} Path={Path}",
        requestId,
        context.Request.Method,
        context.Request.Path);

    await next();

    stopwatch.Stop();
    app.Logger.LogInformation(
        "Request completed. RequestId={RequestId} StatusCode={StatusCode} DurationMs={DurationMs}",
        requestId,
        context.Response.StatusCode,
        stopwatch.ElapsedMilliseconds);
});

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
    catch (ModelInferenceTimeoutException ex)
    {
        app.Logger.LogWarning(ex, "Model inference timed out while processing request.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                code = ModelInferenceTimeoutException.ErrorCode,
                message = ex.Message
            });
        }
    }
    catch (OperationCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogWarning(ex, "Request processing timed out.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                code = ModelInferenceTimeoutException.ErrorCode,
                message = ModelInferenceTimeoutException.DefaultMessage
            });
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("Request aborted by client.");
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
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled request error.");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                status = "Error",
                message = "Unexpected server error during AI processing. Please retry."
            });
        }
    }
});

app.MapPost("/estimate", async (
    EstimateRequest request,
    IEstimateSessionStore sessionStore,
    AgentOrchestrator orchestrator,
    IOptions<AiSettings> options,
    CancellationToken cancellationToken) =>
{
    var hasSessionId = !string.IsNullOrWhiteSpace(request.SessionId);
    var hasDescription = !string.IsNullOrWhiteSpace(request.Description);
    var hasAnswer = !string.IsNullOrWhiteSpace(request.Answer);

    if (!hasSessionId && !hasDescription)
    {
        return Results.BadRequest(new
        {
            status = "Error",
            message = "Description is required for a new session."
        });
    }

    if (!hasSessionId && hasAnswer)
    {
        return Results.BadRequest(new
        {
            status = "Error",
            message = "Answer requires an existing session_id."
        });
    }

    WorkflowSession session;
    if (hasSessionId)
    {
        if (!sessionStore.TryGet(request.SessionId!, out var existingSession) || existingSession is null)
        {
            return Results.BadRequest(new
            {
                status = "Error",
                message = "Session not found or expired. Start a new request with description."
            });
        }

        session = existingSession;

        if (hasDescription)
        {
            var incomingDescription = request.Description!.Trim();
            if (!session.ProjectDescription.Equals(incomingDescription, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    status = "Error",
                    message = "Description cannot be changed for an existing session."
                });
            }
        }
    }
    else
    {
        session = sessionStore.Create(request.Description!);
    }

    if (session.Result is not null)
    {
        return Results.Ok(new
        {
            status = "Success",
            session_id = session.SessionId,
            data = session.Result
        });
    }

    if (string.IsNullOrWhiteSpace(request.Answer) && !string.IsNullOrWhiteSpace(session.PendingQuestion))
    {
        return Results.Ok(new
        {
            status = "NeedsClarification",
            session_id = session.SessionId,
            question = session.PendingQuestion,
            clarification_round = session.QuestionsAsked,
            max_clarification_rounds = Math.Max(0, options.Value.MaxClarificationRounds)
        });
    }

    if (hasAnswer)
    {
        if (string.IsNullOrWhiteSpace(session.PendingQuestion))
        {
            return Results.BadRequest(new
            {
                status = "Error",
                message = "No pending clarification question for this session."
            });
        }

        session.Clarifications.Add(new ClarificationExchange
        {
            Question = session.PendingQuestion,
            Answer = request.Answer!.Trim()
        });
        session.PendingQuestion = null;
    }

    using var workflowTimeoutCts = CreateWorkflowTimeoutCts(cancellationToken, options.Value.WorkflowTimeoutSeconds);

    var step = await orchestrator.RunSessionAsync(session, workflowTimeoutCts.Token);
    sessionStore.Save(session);

    if (step.Status == WorkflowStepStatus.NeedsClarification)
    {
        return Results.Ok(new
        {
            status = "NeedsClarification",
            session_id = session.SessionId,
            question = step.Question,
            clarification_round = session.QuestionsAsked,
            max_clarification_rounds = Math.Max(0, options.Value.MaxClarificationRounds)
        });
    }

    return Results.Ok(new
    {
        status = "Success",
        session_id = session.SessionId,
        data = step.Result
    });
});

app.MapPost("/estimate/document", async (
    HttpRequest request,
    IDocumentTextExtractor extractor,
    IEstimateSessionStore sessionStore,
    AgentOrchestrator orchestrator,
    IOptions<AiSettings> options,
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

    var session = sessionStore.Create(extraction.Text);
    using var workflowTimeoutCts = CreateWorkflowTimeoutCts(cancellationToken, options.Value.WorkflowTimeoutSeconds);

    var step = await orchestrator.RunSessionAsync(session, workflowTimeoutCts.Token);
    sessionStore.Save(session);

    if (step.Status == WorkflowStepStatus.NeedsClarification)
    {
        return Results.Ok(new
        {
            status = "NeedsClarification",
            session_id = session.SessionId,
            question = step.Question,
            clarification_round = session.QuestionsAsked,
            max_clarification_rounds = Math.Max(0, options.Value.MaxClarificationRounds),
            source = new
            {
                file_name = file.FileName,
                file_extension = extraction.FileExtension,
                extracted_characters = extraction.Text.Length
            }
        });
    }

    return Results.Ok(new
    {
        status = "Success",
        session_id = session.SessionId,
        source = new
        {
            file_name = file.FileName,
            file_extension = extraction.FileExtension,
            extracted_characters = extraction.Text.Length
        },
        data = step.Result
    });
})
.Accepts<IFormFile>("multipart/form-data")
.DisableAntiforgery();

app.Run();

public sealed record EstimateRequest(string? Description, string? SessionId, string? Answer);
