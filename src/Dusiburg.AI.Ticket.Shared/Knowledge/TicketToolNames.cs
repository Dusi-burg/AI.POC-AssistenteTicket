namespace Dusiburg.AI.Ticket.Shared.Knowledge;

/// <summary>Nomi dei tool del server MCP <c>ticket-mcp</c> (Fase 9b): li usano il server e la guardia della modalità agente.</summary>
public static class TicketToolNames
{
    public const string SearchSources = "cerca_fonti";
    public const string ReadSource = "leggi_fonte";
    public const string ReleaseNotes = "note_di_rilascio";
    public const string CustomerHistory = "storico_cliente";
    public const string SearchZammad = "cerca_ticket_zammad";

    public static IReadOnlyList<string> All { get; } = [SearchSources, ReadSource, ReleaseNotes, CustomerHistory, SearchZammad];
}
