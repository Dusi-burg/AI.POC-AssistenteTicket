namespace Dusiburg.AI.Ticket.Shared.Telemetry;

public static class TicketTelemetry
{
    /// <summary>Filtro usato da ServiceDefaults per raccogliere tutte le sorgenti del POC.</summary>
    public const string SourceWildcard = "Dusiburg.AI.Ticket.*";

    public static class Sources
    {
        public const string Chat = "Dusiburg.AI.Ticket.Ai.Chat";
        public const string Embedding = "Dusiburg.AI.Ticket.Ai.Embedding";
        public const string Zammad = "Dusiburg.AI.Ticket.Zammad";
        public const string Retrieval = "Dusiburg.AI.Ticket.Rag.Retrieval";
        public const string Pipeline = "Dusiburg.AI.Ticket.Core.Pipeline";

        /// <summary>Server MCP (<c>mcp.tool …</c>) e modalità agente (<c>agent.tool …</c>), Fase 9b.</summary>
        public const string Mcp = "Dusiburg.AI.Ticket.Mcp";
        public const string Agentic = "Dusiburg.AI.Ticket.Agentic";
    }
}
