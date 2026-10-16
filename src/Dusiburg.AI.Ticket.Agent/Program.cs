using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.ServiceDefaults.Problems;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Zammad;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Manopole dei modelli dall'AppHost; client di chat ed embedding ospitati qui come librerie (G8).
builder.Services.AddTicketAi(builder.Configuration);

// Zammad: indirizzo e token dall'AppHost (ZAMMAD_URL, ZAMMAD_API_TOKEN), client tipizzato con la resilienza standard.
builder.Services.AddTicketZammad(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHealthProbe, SqlHealthProbe>();
builder.Services.AddSingleton<IHealthProbe, RagIndexHealthProbe>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<HealthReportCache>();
builder.Services.AddHealthChecks().AddCheck<TicketHealthCheck>(TicketHealthCheck.Name);

// JSON camelCase con enum come stringhe, uguale ai client (TicketJson).
builder.Services.ConfigureHttpJsonOptions(options => TicketJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails(ApiProblems.Configure);
builder.Services.Configure<ExceptionHandlerOptions>(ApiProblems.ConfigureExceptionHandler);

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();

app.MapGet("/", () => new { service = "Dusiburg.AI.Ticket.Agent", phase = 2 });

app.MapGroup("/api")
    .MapHealthEndpoints();

app.LogConnectionStringPresence(SqlHealthProbe.ConnectionStringName);

app.Run();

/// <summary>Tipo di riferimento dell'assembly per <c>WebApplicationFactory</c> nei test.</summary>
public sealed class AgentEntryPoint;
