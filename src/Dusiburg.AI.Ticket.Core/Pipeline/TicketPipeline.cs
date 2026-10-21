using System.Diagnostics;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

/// <summary>
/// Pipeline deterministica (G8, PLAN §5 Fase 6): contesto → classificazione → retrieval → generazione → verifica → confidenza e
/// decisione → composizione con la dicitura AI → pubblicazione. Il modello scrive, il codice decide. Ogni passo è cronometrato e
/// ha il suo span. Non scrive nel database: lo fa <see cref="AgentRunService"/>, così la Cli può provare la pipeline a secco.
/// Con <c>PIPELINE_MODE=agent</c> (Fase 9b) retrieval e generazione li sostituisce l'agente con i tool (<see cref="IToolAgentDrafter"/>);
/// tutto il resto non cambia, e la verifica accetta solo le fonti che l'agente ha letto davvero.
/// </summary>
public sealed class TicketPipeline(
    TicketContextLoader loader,
    TicketLanguageModel model,
    HybridRetriever retriever,
    ZammadPublisher publisher,
    TicketDbContext db,
    AgentOptions options,
    ZammadOptions zammadOptions,
    IToolAgentDrafter? toolAgent = null)
{
    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Pipeline);

    public AgentOptions Options => options;

    /// <summary>
    /// La stessa pipeline con un altro modello: il confronto con il cloud della Fase 8 (G10). L'agente, se c'è, usa la stessa fabbrica.
    /// </summary>
    public TicketPipeline WithModel(TicketLanguageModel other, IChatClientFactory? agentModels = null) =>
        new(loader, other, retriever, publisher, db, options, zammadOptions, agentModels is null ? toolAgent : toolAgent?.WithModels(agentModels));

    /// <summary>La stessa pipeline in un'altra modalità: la valutazione confronta fissa e agente sugli stessi casi.</summary>
    public TicketPipeline WithMode(PipelineMode mode) => new(loader, model, retriever, publisher, db, options with { Mode = mode }, zammadOptions, toolAgent);

    /// <summary>Passi fino alla bozza composta, senza scrivere né in Zammad né nel database.</summary>
    public async Task<DraftProposal> ProposeAsync(long ticketId, long? articleId, CancellationToken cancellationToken)
    {
        (IncomingTicket ticket, TimeSpan contextTime) = await StepAsync("contesto", () => loader.LoadAsync(ticketId, articleId, cancellationToken));

        return await ProposeForAsync(ticket, contextTime, cancellationToken);
    }

    /// <summary>
    /// I passi dopo il contesto, su un ticket già caricato: lo usa anche la valutazione della Fase 8, che costruisce il ticket del
    /// test set dal database invece che da Zammad.
    /// </summary>
    public async Task<DraftProposal> ProposeForAsync(IncomingTicket ticket, TimeSpan contextTime, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        (ModelCall<ModelClassification> classification, TimeSpan classificationTime) = await StepAsync("classificazione", () =>
            model.ClassifyAsync(PromptBuilder.Classification(ticket), cancellationToken));

        RetrievalResult retrieval;
        TimeSpan retrievalTime;
        ModelCall<ModelDraft> draft;
        TimeSpan generationTime;
        IReadOnlyList<ToolCallRecord> toolCalls = [];

        if (options.Mode == PipelineMode.Agent)
        {
            IToolAgentDrafter agent = toolAgent
                ?? throw new InvalidOperationException($"{AgentOptions.Keys.PipelineMode}=agent ma la modalità agente non è registrata (AddTicketAgentic).");

            (ToolAgentDraft agentDraft, generationTime) = await StepAsync("agente", () => agent.DraftAsync(ticket, classification.Value, cancellationToken));
            retrieval = await EvidenceAsync(ticket, agentDraft.Evidence, cancellationToken);
            retrievalTime = TimeSpan.Zero;
            draft = agentDraft.Draft;
            toolCalls = agentDraft.ToolCalls;
        }
        else
        {
            (retrieval, retrievalTime) = await StepAsync("retrieval", () => retriever.SearchAsync(Query(ticket), cancellationToken));

            List<ChatMessage> prompt = PromptBuilder.Draft(ticket, classification.Value, retrieval.PromptSources);
            (draft, generationTime) = await StepAsync("generazione", () =>
                model.DraftAsync(prompt, [.. retrieval.PromptSources.Select(s => s.Key)], cancellationToken));
        }

        IReadOnlyList<RetrievedSource> sources = retrieval.PromptSources;

        long verificationStart = Stopwatch.GetTimestamp();
        VerificationResult verification;
        ConfidenceAssessment confidence;
        ComposedDraft composed;

        using (Activity? activity = Telemetry.StartActivity("verifica"))
        {
            string ticketText = string.Join("\n", ticket.Thread.Select(m => m.Text).Prepend(ticket.Title));
            verification = DraftVerifier.Verify(draft.Value, sources, ticketText);
            confidence = ConfidenceCalculator.Assess(sources, verification, classification.Value.MissingInformation.Count,
                draft.Value.SelfAssessment, draft.Value.CanAnswer && !string.IsNullOrWhiteSpace(verification.Answer), options.ConfidenceMinForDraft);
            composed = DraftComposer.Compose(confidence.Kind, verification.Answer, draft.Value.Questions,
                classification.Value.MissingInformation, ticket.Customer.ContactFirstName);

            activity?.SetTag("ticket.draft_kind", confidence.Kind.ToString());
            activity?.SetTag("ticket.confidence", confidence.Confidence);
        }

        TimeSpan verificationTime = Stopwatch.GetElapsedTime(verificationStart);
        IReadOnlyDictionary<string, long> zammadTickets = await ZammadTicketsOfSourcesAsync(sources, cancellationToken);
        var timings = new PipelineTimings(contextTime, classificationTime, retrievalTime, generationTime, verificationTime, TimeSpan.Zero,
            contextTime + Stopwatch.GetElapsedTime(start));

        return new DraftProposal(ticket, classification, retrieval, draft, verification, confidence, composed, zammadTickets, timings, options.Mode, toolCalls);
    }

    /// <summary>Le fonti lette dall'agente come risultato del retrieval, con i filtri del cliente: le leggono verifica, confidenza e nota.</summary>
    private async Task<RetrievalResult> EvidenceAsync(IncomingTicket ticket, IReadOnlyList<SourceEvidence> evidence, CancellationToken cancellationToken)
    {
        ChunkCatalog catalog = await retriever.CatalogAsync(cancellationToken);
        IReadOnlyList<RetrievedSource> sources = evidence.ToRetrievedSources(catalog);
        TimeSpan none = TimeSpan.Zero;

        return new RetrievalResult([.. sources.SelectMany(s => s.Chunks)], sources, sources, catalog.Resolve(Query(ticket)), new RetrievalTimings(none, none, none, none));
    }

    /// <summary>Bozza condivisa, nota interna e tag; restituisce la proposta con il tempo di pubblicazione e ciò che è stato scritto.</summary>
    public async Task<(DraftProposal Proposal, PublicationResult Publication)> PublishAsync(
        DraftProposal proposal, int? runId, string? traceUrl, CancellationToken cancellationToken)
    {
        string note = InternalNoteComposer.Compose(proposal, options.ConfidenceMinForDraft, zammadOptions.BaseUrl, traceUrl, runId);

        (PublicationResult publication, TimeSpan publicationTime) = await StepAsync("pubblicazione", () =>
            publisher.PublishAsync(proposal.Ticket.ZammadTicketId, proposal.Draft, note, cancellationToken));

        PipelineTimings timings = proposal.Timings with { Publication = publicationTime, Total = proposal.Timings.Total + publicationTime };

        return (proposal with { Timings = timings }, publication);
    }

    /// <summary>Testo della ricerca: titolo e messaggi del cliente, puliti e anonimizzati come l'indice (Fase 5).</summary>
    internal static RetrievalQuery Query(IncomingTicket ticket)
    {
        CustomerContext customer = ticket.Customer;
        string text = SourceText.Query(ticket.Title, string.Join("\n\n", ticket.CustomerMessages.Select(m => m.Text)),
            KnownNames.Of(customer.ContactName, customer.OrganizationName));

        return new RetrievalQuery(text, customer.ProductCode, customer.Mode, customer.Version);
    }

    /// <summary>I ticket storici importati in Zammad (Fase 4) diventano link nella nota interna.</summary>
    private async Task<IReadOnlyDictionary<string, long>> ZammadTicketsOfSourcesAsync(IReadOnlyList<RetrievedSource> sources, CancellationToken cancellationToken)
    {
        List<string> numbers = [.. sources.Where(s => s.Source == Shared.Domain.ChunkSource.HistoricalTicket).Select(s => s.Key)];

        return numbers.Count == 0
            ? new Dictionary<string, long>()
            : await db.HistoricalTickets.AsNoTracking()
                .Where(t => numbers.Contains(t.Number) && t.ZammadTicketId != null)
                .ToDictionaryAsync(t => t.Number, t => (long)t.ZammadTicketId!.Value, cancellationToken);
    }

    private static async Task<(T Result, TimeSpan Duration)> StepAsync<T>(string name, Func<Task<T>> step)
    {
        using Activity? activity = Telemetry.StartActivity(name);
        long start = Stopwatch.GetTimestamp();

        T result = await step();

        return (result, Stopwatch.GetElapsedTime(start));
    }
}
