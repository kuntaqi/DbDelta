using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using DbDelta.Core.Providers;
using DbDelta.SqlServer;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SafetyOptions>(builder.Configuration.GetSection(SafetyOptions.SectionName));
builder.Services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<ServerClassifier>();
builder.Services.AddSingleton<CompareSessionStore>();
builder.Services.AddScoped<CompareService>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapPost("/probe", async (
    ConnectionRequest request,
    CompareService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ProbeAsync(request, cancellationToken));
    }
    catch (SqlException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot reach that database");
    }
});

api.MapPost("/compare", async (
    CompareRequest request,
    CompareService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CompareAsync(request, cancellationToken));
    }
    catch (SqlException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Cannot reach that database");
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

api.MapPost("/compare/{id}/script", (
    string id,
    ScriptRequest request,
    CompareService service,
    CompareSessionStore sessions) =>
{
    var session = sessions.Find(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(service.Script(session, request.Include));
});

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
