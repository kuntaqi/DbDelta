using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using DbDelta.Core.Apply;
using DbDelta.Core.Providers;
using DbDelta.SqlServer;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SafetyOptions>(builder.Configuration.GetSection(SafetyOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<ServerClassifier>();
builder.Services.AddSingleton<CompareSessionStore>();
builder.Services.AddSingleton<IScriptExecutor, SqlServerScriptExecutor>();
builder.Services.AddSingleton<RunLogStore>();
builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<ApplyService>();
builder.Services.AddScoped<DataCompareService>();
builder.Services.AddScoped<FkMapService>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapPost("/probe", async (
    ConnectionRequest request,
    ConnectionFactory connections,
    CompareService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ProbeAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "That connection is incomplete");
    }
    catch (SqlException ex)
    {
        var (title, detail) = Explain(ex, request, connections);
        return Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: title);
    }
});

api.MapPost("/compare", async (
    CompareRequest request,
    ConnectionFactory connections,
    CompareService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CompareAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "That connection is incomplete");
    }
    catch (SqlException ex)
    {
        // Which side failed is worth naming: "cannot open database" against the target reads very
        // differently from the same error against the source.
        var side = Blames(ex, request.Target, connections) ? request.Target : request.Source;
        var (title, detail) = Explain(ex, side, connections);
        return Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: title);
    }
});

api.MapGet("/compare/{id}", (string id, CompareService service, CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null ? Results.NotFound() : Results.Ok(service.Describe(session));
});

api.MapGet("/compare/{id}/objects/{objectId}", (
    string id,
    string objectId,
    CompareService service,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    var detail = service.Detail(session, objectId);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

api.MapPost("/compare/{id}/script", async (
    string id,
    ScriptRequest request,
    CompareService service,
    DataCompareService data,
    CompareSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(await service.ScriptAsync(session, request.Include, data, cancellationToken));
});

api.MapPost("/compare/{id}/data/select", (
    string id,
    DataSelectionRequest request,
    DataCompareService service,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    try
    {
        return Results.Ok(service.Select(session, request));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot select that table");
    }
});

api.MapGet("/compare/{id}/data/select", (string id, CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null ? Results.NotFound() : Results.Ok(DataCompareService.Selection(session));
});

api.MapPost("/compare/{id}/apply", async (
    string id,
    ApplyRequest request,
    ApplyService service,
    CompareSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(await service.ApplyAsync(session, request, cancellationToken));
});

api.MapGet("/compare/{id}/volume", async (
    string id,
    DataCompareService service,
    CompareSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(await service.VolumeAsync(session, cancellationToken));
});

api.MapPost("/compare/{id}/data", async (
    string id,
    DataCompareRequestDto request,
    DataCompareService service,
    CompareSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    try
    {
        return Results.Ok(await service.CompareAsync(session, request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot compare that table");
    }
});

api.MapGet("/compare/{id}/fk", (
    string id,
    string table,
    int? depth,
    FkMapService service,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    try
    {
        return Results.Ok(service.Build(session, table, depth ?? 1));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot map that table");
    }
});

api.MapGet("/runs", async (ApplyService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RunsAsync(cancellationToken)));

api.MapGet("/runs/{runId}", async (string runId, ApplyService service, CancellationToken cancellationToken) =>
{
    var run = await service.RunAsync(runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(new { run.Id, run.Sql, run.Outcome, run.ServerMessage });
});

app.MapFallbackToFile("index.html");

app.Run();

static (string Title, string Detail) Explain(
    SqlException exception,
    ConnectionRequest request,
    ConnectionFactory connections)
{
    try
    {
        var resolved = connections.Resolve(request);
        return ConnectionProblem.Describe(exception, resolved.Server, resolved.Database);
    }
    catch (InvalidOperationException)
    {
        return ("Cannot reach that database", exception.Message);
    }
}

static bool Blames(SqlException exception, ConnectionRequest candidate, ConnectionFactory connections)
{
    if (exception.Number != 4060)
    {
        return false;
    }

    try
    {
        return exception.Message.Contains(connections.Resolve(candidate).Database, StringComparison.OrdinalIgnoreCase);
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

public partial class Program;
