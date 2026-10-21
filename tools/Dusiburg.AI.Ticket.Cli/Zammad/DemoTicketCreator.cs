using System.Text.Json;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Cli.Zammad;

internal sealed record DemoTicketsFile(IReadOnlyList<DemoTicketDefinition> Tickets);

/// <summary>Testo del ticket "in arrivo" di uno scenario (<c>data/demo.json</c>, scritto a mano).</summary>
internal sealed record DemoTicketDefinition(int Scenario, string ContactFirstName, string ContactLastName, string Subject, string Body);

internal sealed record DemoTicketCreated(long Id, string Number, string CustomerEmail, string Group);

/// <summary>
/// <c>demo &lt;n&gt;</c> (fase-4.md §4): crea il ticket dello scenario <c>n</c> come lo scriverebbe il cliente, senza
/// <c>import_mode</c>: è l'evento che dalla Fase 6 fa partire l'agente. Ogni esecuzione crea un ticket nuovo.
/// </summary>
internal sealed class DemoTicketCreator(ZammadClient zammad, TicketDbContext db, string dataDirectory)
{
    public const string FileName = "demo.json";

    public static IReadOnlyList<DemoTicketDefinition> Load(string dataDirectory)
    {
        using FileStream stream = File.OpenRead(Path.Combine(dataDirectory, FileName));

        return JsonSerializer.Deserialize<DemoTicketsFile>(stream, SeedData.JsonOptions)?.Tickets
            ?? throw new InvalidDataException($"{FileName} vuoto.");
    }

    public async Task<DemoTicketCreated> CreateAsync(int scenarioNumber, CancellationToken cancellationToken)
    {
        DemoScenario scenario = DemoCatalog.Scenarios.SingleOrDefault(s => s.Number == scenarioNumber)
            ?? throw new ArgumentOutOfRangeException(nameof(scenarioNumber), $"Scenario {scenarioNumber} inesistente: gli scenari vanno da 1 a {DemoCatalog.Scenarios.Count}.");
        DemoTicketDefinition definition = Load(dataDirectory).Single(t => t.Scenario == scenarioNumber);
        DemoCustomer customer = DemoCatalog.Customer(scenario.CustomerEmailDomain);

        bool organizationSeeded = await db.Customers.AnyAsync(c => c.EmailDomain == customer.EmailDomain && c.ZammadOrganizationId != null, cancellationToken);

        if (!organizationSeeded)
        {
            throw new InvalidOperationException($"Organizzazione di '{customer.Name}' assente in Zammad: lanciare prima 'zammad seed'.");
        }

        ZammadUsers users = await ZammadUsers.LoadAsync(zammad, cancellationToken);
        string fullName = $"{definition.ContactFirstName} {definition.ContactLastName}";
        string email = ZammadDirectory.PersonEmail(fullName, customer.EmailDomain);
        await users.EnsureCustomerAsync(fullName, email, customer.EmailDomain, cancellationToken);

        string group = ZammadDirectory.GroupFor(customer.ProductCode);
        long groupId = (await zammad.ListAllAsync<ZammadGroup>(ZammadResources.Groups, cancellationToken)).Single(g => g.Name == group).Id;

        // Per conto del cliente: il ticket e il primo articolo risultano suoi, come da portale (mittente Customer).
        ZammadTicket ticket = await zammad.CreateAsync<ZammadTicket>(ZammadResources.Tickets, new Dictionary<string, object?>
        {
            ["title"] = definition.Subject,
            ["group_id"] = groupId,
            ["article"] = new Dictionary<string, object?>
            {
                ["subject"] = definition.Subject,
                ["body"] = definition.Body,
                ["content_type"] = "text/plain",
                ["type"] = "web",
                ["internal"] = false
            }
        }, cancellationToken, onBehalfOf: email);

        return new DemoTicketCreated(ticket.Id, ticket.Number, email, group);
    }
}
