using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using DbDelta.Core.Apply;
using DbDelta.Core.Providers;
using DbDelta.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Real server-name patterns belong here, not in the committed defaults: the naming scheme of a real
// environment is not something this repo should carry. Gitignored.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<SafetyOptions>(builder.Configuration.GetSection(SafetyOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<ServerClassifier>();
builder.Services.AddSingleton<CompareSessionStore>();
builder.Services.AddSingleton<IScriptExecutor, SqlServerScriptExecutor>();
builder.Services.AddSingleton<RunLogStore>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddScoped<CompareService>();
builder.Services.AddScoped<ApplyService>();
builder.Services.AddScoped<DataCompareService>();
builder.Services.AddScoped<FkMapService>();
builder.Services.AddScoped<InstanceService>();
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

api.MapPost("/instance", async (
    ConnectionRequest request,
    ConnectionFactory connections,
    InstanceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.SurveyAsync(request, cancellationToken));
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

api.MapPost("/instance/describe", async (
    InstanceDetailRequest request,
    ConnectionFactory connections,
    InstanceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.DescribeAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "That connection is incomplete");
    }
    catch (SqlException ex)
    {
        var (title, detail) = Explain(ex, request.Connection, connections);
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
        : Results.Ok((await service.ScriptAsync(session, data, cancellationToken)).Response);
});

api.MapGet("/compare/{id}/script/download", async (
    string id,
    CompareService service,
    DataCompareService data,
    CompareSessionStore sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    var built = await service.ScriptAsync(session, data, cancellationToken);
    var (content, fileName, contentType) = ScriptPackager.Package(built.Script, session.Target.DatabaseName);

    return Results.File(content, contentType, fileName);
});

api.MapPost("/compare/{id}/schema/select", (
    string id,
    SchemaSelectionRequest request,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    try
    {
        return Results.Ok(SchemaSelectionService.Select(session, request));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot select that object");
    }
});

api.MapPost("/compare/{id}/schema/scope", (
    string id,
    SchemaScopeRequest request,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null ? Results.NotFound() : Results.Ok(SchemaSelectionService.SetScope(session, request.Scope));
});

api.MapPost("/compare/{id}/schema/clear", (
    string id,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null ? Results.NotFound() : Results.Ok(SchemaSelectionService.Clear(session));
});

api.MapGet("/compare/{id}/schema/select", (string id, CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null ? Results.NotFound() : Results.Ok(SchemaSelectionService.Describe(session));
});

api.MapGet("/compare/{id}/data/key", async (
    string id,
    string table,
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
        return Results.Ok(await service.KeyOptionsAsync(session, table, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot read that table");
    }
});

api.MapPost("/compare/{id}/data/key", async (
    string id,
    KeyChoiceRequest request,
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
        return Results.Ok(await service.ChooseKeyAsync(session, request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot use those columns as a key");
    }
});

api.MapPost("/compare/{id}/data/scan", async (
    string id,
    long? maxTableBytes,
    DataCompareService service,
    CompareSessionStore sessions,
    IOptions<SafetyOptions> safety,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Find(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(await service.ScanAsync(
            session, maxTableBytes ?? safety.Value.MaxScanTableBytes, cancellationToken));
});

api.MapGet("/compare/{id}/data/scan", (string id, CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    if (session is null)
    {
        return Results.NotFound();
    }

    var cached = DataCompareService.CachedScan(session);
    return cached is null ? Results.NoContent() : Results.Ok(cached);
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
    FkDirection? direction,
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
        return Results.Ok(service.Build(session, table, depth ?? 1, direction ?? FkDirection.Both));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot map that table");
    }
});

api.MapGet("/profiles", async (ProfileStore profiles, CancellationToken cancellationToken) =>
    Results.Ok(await profiles.ListAsync(cancellationToken)));

api.MapPost("/profiles", async (
    SaveProfileRequest request,
    ProfileStore profiles,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await profiles.SaveAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot save that profile");
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot save that profile");
    }
});

api.MapDelete("/profiles/{name}", async (
    string name,
    ProfileStore profiles,
    CancellationToken cancellationToken) =>
    Results.Ok(await profiles.DeleteAsync(name, cancellationToken)));

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
