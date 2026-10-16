using System.Text.Json;
using Dusiburg.AI.Ticket.Data.Seed;

namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>Serializzazione di <c>tickets.json</c>: la usano il comando e il test che verifica che il file sia aggiornato.</summary>
public static class TicketsJson
{
    public static string Serialize(TicketsFile tickets) => JsonSerializer.Serialize(tickets, SeedData.JsonOptions) + "\n";

    /// <summary>Il checkout su Windows può convertire gli a capo: il confronto li ignora.</summary>
    public static string Normalize(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal);
}
