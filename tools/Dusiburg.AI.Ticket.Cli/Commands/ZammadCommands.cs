using System.CommandLine;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary><c>zammad bootstrap</c> e <c>zammad seed</c> (fase-4.md §2–3).</summary>
internal static class ZammadCommands
{
    public const int DefaultSeedCount = 80;

    public static Command Create(IServiceProvider services) => new("zammad", "Prepara l'istanza Zammad del POC.")
    {
        Bootstrap(services),
        Seed(services)
    };

    private static Command Bootstrap(IServiceProvider services)
    {
        var rotate = new Option<bool>("--rotate-token") { Description = "Crea un nuovo token per ai-assistant anche se quello salvato è valido." };
        var command = new Command("bootstrap", "Gruppi, campi personalizzati, tag, utenti, token di ai-assistant, webhook e trigger (idempotente).") { rotate };

        command.SetAction(async (parse, cancellationToken) =>
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            var settings = new BootstrapSettings(
                new Uri(configuration[ZammadOptions.Keys.WebhookTarget] is { Length: > 0 } target ? target : ZammadOptions.DefaultWebhookTarget),
                SeedDataFiles.Load().Noise.Operators,
                RestartDownWindow: TimeSpan.FromSeconds(30),
                RestartUpTimeout: TimeSpan.FromMinutes(3),
                PollInterval: TimeSpan.FromSeconds(1));

            var bootstrapper = new ZammadBootstrapper(
                services.GetRequiredService<ZammadClient>(), services.GetRequiredService<UserSecretsStore>(), settings, Console.Out, TimeProvider.System);

            Console.WriteLine($"Bootstrap di {services.GetRequiredService<ZammadOptions>().BaseUrl}");
            BootstrapReport report = await bootstrapper.RunAsync(parse.GetValue(rotate), cancellationToken);

            Console.WriteLine();
            Console.WriteLine(report.Changed.Count == 0
                ? $"Nulla da fare: {report.Present.Count} elementi già presenti."
                : $"Fatto: {report.Changed.Count} modifiche, {report.Present.Count} elementi già presenti. Riavviare l'AppHost per dare all'Agent il token nuovo.");

            return 0;
        });

        return command;
    }

    private static Command Seed(IServiceProvider services)
    {
        var count = new Option<int>("--count") { Description = "Ticket storici da importare, i più recenti.", DefaultValueFactory = _ => DefaultSeedCount };
        var command = new Command("seed", "Organizzazioni, referenti e ticket storici dal database Ticket (idempotente).") { count };

        command.SetAction(async (parse, cancellationToken) =>
        {
            using IServiceScope scope = services.CreateScope();
            var seeder = new ZammadSeeder(
                services.GetRequiredService<ZammadClient>(), scope.ServiceProvider.GetRequiredService<TicketDbContext>(),
                SeedDataFiles.Load().Noise.Operators, Console.Out);

            Console.WriteLine($"Seed di {services.GetRequiredService<ZammadOptions>().BaseUrl}");
            ZammadSeedReport report = await seeder.RunAsync(parse.GetValue(count), cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Fatto: organizzazioni {report.OrganizationsCreated} create ({report.OrganizationsPresent} già presenti), " +
                $"referenti {report.CustomersCreated} creati, ticket {report.TicketsCreated} importati ({report.TicketsPresent} già presenti).");

            return 0;
        });

        return command;
    }
}

/// <summary>Cartella <c>data/</c> del repository, cercata dalla cartella corrente e da quella dell'eseguibile.</summary>
internal static class SeedDataFiles
{
    public static string Directory() =>
        SeedData.FindDataDirectory(System.IO.Directory.GetCurrentDirectory())
        ?? SeedData.FindDataDirectory(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException("Cartella data/ del repository non trovata: lanciare la Cli dalla root del repository.");

    public static SeedData Load() => SeedData.Load(Directory());
}
