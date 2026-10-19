using Dusiburg.AI.Ticket.Agent.Events;
using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Agent.Runs;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Rag;
using Dusiburg.AI.Ticket.ServiceDefaults.Problems;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Manopole dei modelli dall'AppHost; client di chat ed embedding ospitati qui come librerie (G8).
builder.Services.AddTicketAi(builder.Configuration);

// Zammad: indirizzo e token dall'AppHost (ZAMMAD_URL, ZAMMAD_API_TOKEN). La resilienza standard non riprova i POST (nota
// interna, tag): un retry dopo un timeout li duplicherebbe. La bozza è un PUT e si può ripetere.
builder.Services.AddTicketZammad(builder.Configuration);
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers è sperimentale: toglie il gestore aggiunto da ServiceDefaults.
builder.Services.AddHttpClient<ZammadClient>()
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());
#pragma warning restore EXTEXP0001

// Database Ticket (l'Agent ne è l'unico proprietario), indice e retrieval (Fase 5), pipeline (Fase 6).
builder.Services.AddSingleton(_ => new TicketDbContextOptions(AiOptions.ReadEmbeddingDimensions(builder.Configuration)));
builder.Services.AddDbContext<TicketDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString(SqlHealthProbe.ConnectionStringName) ?? SqlHealthProbe.MissingConnectionString));
builder.Services.AddTicketRag(builder.Configuration);
builder.Services.AddTicketCore(builder.Configuration);

// Eventi di Zammad (P1) e coda con un solo consumatore (P2).
builder.Services.AddSingleton<TicketWorkQueue>();
builder.Services.AddSingleton<TicketEventIntake>();
builder.Services.AddHostedService<TicketWorker>();
builder.Services.AddHostedService<PollingEventSource>();

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

app.MapGet("/", () => new { service = "Dusiburg.AI.Ticket.Agent", phase = 6 });

app.MapGroup("/api")
    .MapHealthEndpoints()
    .MapRunEndpoints();

app.MapZammadWebhook();

app.LogConnectionStringPresence(SqlHealthProbe.ConnectionStringName);

app.Run();

/// <summary>Tipo di riferimento dell'assembly per <c>WebApplicationFactory</c> nei test.</summary>
public sealed class AgentEntryPoint;
