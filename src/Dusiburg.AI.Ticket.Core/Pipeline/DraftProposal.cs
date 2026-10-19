using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Rag.Retrieval;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

/// <summary>Durata di ogni passo della pipeline: le stesse misure compaiono come span nel dashboard di Aspire.</summary>
public sealed record PipelineTimings(
    TimeSpan Context, TimeSpan Classification, TimeSpan Retrieval, TimeSpan Generation, TimeSpan Verification, TimeSpan Publication, TimeSpan Total);

/// <summary>Tutto ciò che la pipeline ha prodotto per un articolo del cliente, prima e dopo la decisione del codice.</summary>
public sealed record DraftProposal(
    IncomingTicket Ticket,
    ModelCall<ModelClassification> Classification,
    RetrievalResult Retrieval,
    ModelCall<ModelDraft> ModelDraft,
    VerificationResult Verification,
    ConfidenceAssessment Confidence,
    ComposedDraft Draft,
    IReadOnlyDictionary<string, long> ZammadTicketOfSource,
    PipelineTimings Timings);
