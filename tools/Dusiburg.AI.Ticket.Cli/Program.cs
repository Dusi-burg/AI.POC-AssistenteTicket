using Dusiburg.AI.Ticket.Agentic;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Cli;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Core;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Rag;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// Uso: dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- <comando> (zammad bootstrap | zammad seed | zammad ai on|off|status | demo <n> | embed | search | eval retrieval | draft <ticket> | reply <ticket> | close <ticket>)
// Gli argomenti non vanno alla configurazione: li interpreta System.CommandLine.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });

UserSecretsStore secrets = UserSecretsStore.ForAppHost();
builder.Configuration.AddAppHostSecrets(secrets);

// Solo telemetria: niente resilienza standard sugli HttpClient, i cui retry su POST duplicherebbero ticket e utenti.
builder.ConfigureOpenTelemetry();

LogLevel consoleLevel = args.Contains(TicketCli.VerboseOption) ? LogLevel.Information : LogLevel.Warning;
builder.Logging.AddFilter<ConsoleLoggerProvider>(level => level >= consoleLevel);

builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTicketZammad(builder.Configuration);
builder.Services.AddTicketAi(builder.Configuration);
builder.Services.AddTicketRag(builder.Configuration);
builder.Services.AddTicketCore(builder.Configuration);

// Modalità agente (eval drafts --modo agente): ticket-mcp sulla porta fissa, con l'AppHost acceso.
builder.Services.AddTicketAgentic(builder.Configuration, ToolAgentOptions.LocalMcpUrl);
builder.Services.AddSingleton(_ => new TicketDbContextOptions(AiOptions.ReadEmbeddingDimensions(builder.Configuration)));
builder.Services.AddDbContext<TicketDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("sql")
    ?? throw new InvalidOperationException($"ConnectionStrings:sql non configurata: impostarla negli user-secrets dell'AppHost ({UserSecretsStore.SqlConnectionStringKey}).")));

using IHost host = builder.Build();

await host.StartAsync();

try
{
    // Il token serve solo ai comandi che parlano con Zammad: embed, search ed eval lavorano su database e modelli.
    bool usesZammad = args.FirstOrDefault(a => !a.StartsWith('-')) is "zammad" or "demo" or "draft" or "reply" or "close";

    if (usesZammad && !host.Services.GetRequiredService<ZammadOptions>().HasApiToken)
    {
        await Console.Error.WriteLineAsync(
            $"Token di Zammad assente: impostare {UserSecretsStore.AdminTokenKey} negli user-secrets dell'AppHost o la variabile {CliConfiguration.AdminTokenVariable}.");

        return TicketCli.ExitError;
    }

    return await TicketCli.InvokeAsync(args, host.Services);
}
catch (Exception exception) when (exception is ZammadApiException or InvalidOperationException or HttpRequestException or TimeoutException or IOException)
{
    await Console.Error.WriteLineAsync($"Errore: {exception.Message}");

    return TicketCli.ExitError;
}
finally
{
    await host.StopAsync();
}
