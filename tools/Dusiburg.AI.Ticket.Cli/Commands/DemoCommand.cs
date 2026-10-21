using System.CommandLine;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary><c>demo &lt;n&gt;</c>: il ticket "in arrivo" di uno scenario del PLAN §6.4 (fase-4.md §4).</summary>
internal static class DemoCommand
{
    public static Command Create(IServiceProvider services)
    {
        var scenario = new Argument<int>("scenario") { Description = $"Numero dello scenario, da 1 a {DemoCatalog.Scenarios.Count}." };
        var command = new Command("demo", "Crea in Zammad il ticket in arrivo di uno scenario demo.") { scenario };

        command.SetAction(async (parse, cancellationToken) =>
        {
            int number = parse.GetValue(scenario);
            DemoScenario? demo = DemoCatalog.Scenarios.SingleOrDefault(s => s.Number == number);

            if (demo is null)
            {
                Console.Error.WriteLine($"Scenario {number} inesistente: scegliere fra 1 e {DemoCatalog.Scenarios.Count}.");
                return 1;
            }

            using IServiceScope scope = services.CreateScope();
            var creator = new DemoTicketCreator(
                services.GetRequiredService<ZammadClient>(), scope.ServiceProvider.GetRequiredService<TicketDbContext>(), SeedDataFiles.Directory());

            DemoTicketCreated created = await creator.CreateAsync(number, cancellationToken);
            Uri zammad = services.GetRequiredService<ZammadOptions>().BaseUrl;

            Console.WriteLine($"Scenario {demo.Number} — {demo.Title}");
            Console.WriteLine($"Ticket {created.Number} creato da {created.CustomerEmail} nel gruppo '{created.Group}': {new Uri(zammad, $"#ticket/zoom/{created.Id}")}");
            Console.WriteLine($"Atteso dall'agente: {demo.ExpectedBehavior}");

            return 0;
        });

        return command;
    }
}
