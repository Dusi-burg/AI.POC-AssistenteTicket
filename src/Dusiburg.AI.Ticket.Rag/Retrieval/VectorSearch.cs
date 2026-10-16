using System.Data;
using System.Data.Common;
using Dapper;
using Dusiburg.AI.Ticket.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Componente vettoriale di P5 (Dapper, G4): distanza coseno esatta sui chunk del prodotto e della modalità. Nessun indice
/// vettoriale: con ~900 chunk la scansione è di pochi millisecondi. Usa la connessione del contesto EF.
/// </summary>
public sealed class VectorSearch(TicketDbContext db)
{
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, AppliedFilters filters, int top, CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        IEnumerable<VectorHit> hits = await connection.QueryAsync<VectorHit>(new CommandDefinition(
            """
            SELECT TOP (@top) c.Id AS ChunkId, CAST(VECTOR_DISTANCE('cosine', c.Embedding, @q) AS float) AS Distance
            FROM rag.Chunk AS c
            WHERE c.Embedding IS NOT NULL
              AND (@productId IS NULL OR c.ProductId = @productId)
              AND (@modeId IS NULL OR c.DeploymentModeId IS NULL OR c.DeploymentModeId = @modeId)
            ORDER BY Distance, c.Id;
            """,
            new
            {
                top,
                q = new VectorParameter(query),
                productId = filters.ProductId,
                modeId = (byte?)filters.Mode
            },
            cancellationToken: cancellationToken));

        return [.. hits];
    }
}

/// <summary>
/// Parametro <c>VECTOR</c> nativo (<see cref="SqlVector{T}"/>) per Dapper, che il tipo non lo conosce: niente JSON né <c>CAST</c>
/// nel testo SQL (come Sinistri).
/// </summary>
internal sealed class VectorParameter(float[] values) : SqlMapper.ICustomQueryParameter
{
    public void AddParameter(IDbCommand command, string name) =>
        command.Parameters.Add(new SqlParameter(name, new SqlVector<float>(values)));
}
