using System.Globalization;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Data.SqlClient;

namespace Dusiburg.AI.Ticket.Agent.Health;

/// <summary>
/// Controllo 13 di <c>/api/health</c> (fase-5.md §7): l'indice del retrieval ha chunk, tutti con il vettore, calcolati con il modello
/// e la dimensione configurati. Solo avvisi: senza indice l'Agent risponde, ma le bozze non avrebbero fonti.
/// </summary>
public sealed class RagIndexHealthProbe(IConfiguration configuration, AiOptions options) : IHealthProbe
{
    public const int Number = 13;

    private const string Name = "Indice RAG";

    public async Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken) =>
        [await ProbeResult.MeasureAsync(Number, Name, () => CheckAsync(cancellationToken))];

    private async Task<(ProbeStatus, string)> CheckAsync(CancellationToken cancellationToken)
    {
        string? connectionString = configuration.GetConnectionString(SqlHealthProbe.ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (ProbeStatus.Warning, "non verificabile: connection string mancante (controllo 1)");
        }

        var target = new SqlConnectionStringBuilder(connectionString);
        string database = target.InitialCatalog;
        target.InitialCatalog = "master";

        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await IndexTableExistsAsync(connection, database, cancellationToken))
        {
            return (ProbeStatus.Warning, $"'{database}' o rag.Chunk assenti: eseguire tools/Dusiburg.AI.Ticket.DbInit e poi Cli embed");
        }

        IndexState state = await ReadStateAsync(connection, cancellationToken);

        return Evaluate(state);
    }

    internal (ProbeStatus, string) Evaluate(IndexState state)
    {
        if (state.Chunks == 0)
        {
            return (ProbeStatus.Warning, "indice vuoto: eseguire 'Cli embed'");
        }

        string counts = $"{state.Chunks} chunk (ticket {state.Tickets}, KB {state.KbSections}, note {state.ReleaseNotes})";

        if (state.Model is null)
        {
            return (ProbeStatus.Warning, $"{counts} senza EmbeddingInfo: eseguire 'Cli embed'");
        }

        if (state.Model != options.EmbeddingModel || state.Dimensions != options.EmbeddingDimensions)
        {
            return (ProbeStatus.Warning,
                $"{counts} calcolati con {state.Model} ({state.Dimensions}) ma la configurazione usa {options.EmbeddingModel} " +
                $"({options.EmbeddingDimensions}): rieseguire 'Cli embed'");
        }

        if (state.WithoutEmbedding > 0)
        {
            return (ProbeStatus.Warning, $"{counts}, {state.WithoutEmbedding} senza vettore: eseguire 'Cli embed --solo-mancanti'");
        }

        return (ProbeStatus.Ok,
            $"{counts}, {state.Model} ({state.Dimensions}), aggiornato il {state.UpdatedAt!.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}");
    }

    /// <summary>Il database esiste (letto da <c>master</c>) e ha <c>rag.Chunk</c>; se sì la connessione resta sul database.</summary>
    private static async Task<bool> IndexTableExistsAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@database)";
        command.Parameters.AddWithValue("@database", database);

        if (await command.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return false;
        }

        await connection.ChangeDatabaseAsync(database, cancellationToken);

        await using SqlCommand table = connection.CreateCommand();
        table.CommandText = "SELECT OBJECT_ID(N'rag.Chunk')";

        return await table.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static async Task<IndexState> ReadStateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*),
                   COUNT(CASE WHEN Embedding IS NULL THEN 1 END),
                   COUNT(HistoricalTicketId), COUNT(KbArticleId), COUNT(ReleaseNoteId)
            FROM rag.Chunk;
            SELECT Model, CAST(Dimensions AS int), UpdatedAt FROM rag.EmbeddingInfo;
            """;

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        (int chunks, int withoutEmbedding, int tickets, int kb, int notes) =
            (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));

        await reader.NextResultAsync(cancellationToken);
        bool hasInfo = await reader.ReadAsync(cancellationToken);

        return new IndexState(chunks, withoutEmbedding, tickets, kb, notes,
            hasInfo ? reader.GetString(0) : null, hasInfo ? reader.GetInt32(1) : null, hasInfo ? reader.GetDateTimeOffset(2) : null);
    }

    internal sealed record IndexState(
        int Chunks, int WithoutEmbedding, int Tickets, int KbSections, int ReleaseNotes, string? Model, int? Dimensions, DateTimeOffset? UpdatedAt);
}
