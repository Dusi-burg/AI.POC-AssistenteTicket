using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

/// <summary>Durata di ogni passo della pipeline: le stesse misure compaiono come span nel dashboard di Aspire.</summary>
public sealed record PipelineTimings(
    TimeSpan Context, TimeSpan Classification, TimeSpan Retrieval, TimeSpan Generation, TimeSpan Verification, TimeSpan Publication, TimeSpan Total);

/// <summary>
/// Tutto ciò che la pipeline ha prodotto per un articolo del cliente, prima e dopo la decisione del codice. Nella modalità agente
/// (Fase 9b) <see cref="Retrieval"/> contiene le fonti lette con i tool e il tempo dell'agente è in <c>Timings.Generation</c>.
/// </summary>
public sealed record DraftProposal(
    IncomingTicket Ticket,
    ModelCall<ModelClassification> Classification,
    RetrievalResult Retrieval,
    ModelCall<ModelDraft> ModelDraft,
    VerificationResult Verification,
    ConfidenceAssessment Confidence,
    ComposedDraft Draft,
    IReadOnlyDictionary<string, long> ZammadTicketOfSource,
    PipelineTimings Timings,
    PipelineMode Mode = PipelineMode.Fixed,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null)
{
    public IReadOnlyList<ToolCallRecord> Calls => ToolCalls ?? [];
}
