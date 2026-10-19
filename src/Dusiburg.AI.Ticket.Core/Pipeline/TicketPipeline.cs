using System.Diagnostics;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

/// <summary>
/// Pipeline deterministica (G8, PLAN §5 Fase 6): contesto → classificazione → retrieval → generazione → verifica → confidenza e
/// decisione → composizione con la dicitura AI → pubblicazione. Il modello scrive, il codice decide. Ogni passo è cronometrato e
/// ha il suo span. Non scrive nel database: lo fa <see cref="AgentRunService"/>, così la Cli può provare la pipeline a secco.
/// </summary>
public sealed class TicketPipeline(
    TicketContextLoader loader,
    TicketLanguageModel model,
    HybridRetriever retriever,
    ZammadPublisher publisher,
    TicketDbContext db,
    AgentOptions options,
    ZammadOptions zammadOptions)
{
    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Pipeline);

    public AgentOptions Options => options;

    /// <summary>Passi fino alla bozza composta, senza scrivere né in Zammad né nel database.</summary>
    public async Task<DraftProposal> ProposeAsync(long ticketId, long? articleId, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        (IncomingTicket ticket, TimeSpan contextTime) = await StepAsync("contesto", () => loader.LoadAsync(ticketId, articleId, cancellationToken));

        (ModelCall<ModelClassification> classification, TimeSpan classificationTime) = await StepAsync("classificazione", () =>
            model.ClassifyAsync(PromptBuilder.Classification(ticket), cancellationToken));

        (RetrievalResult retrieval, TimeSpan retrievalTime) = await StepAsync("retrieval", () => retriever.SearchAsync(Query(ticket), cancellationToken));
        IReadOnlyList<RetrievedSource> sources = retrieval.PromptSources;

        List<ChatMessage> prompt = PromptBuilder.Draft(ticket, classification.Value, sources);
        (ModelCall<ModelDraft> draft, TimeSpan generationTime) = await StepAsync("generazione", () =>
            model.DraftAsync(prompt, [.. sources.Select(s => s.Key)], cancellationToken));

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
            Stopwatch.GetElapsedTime(start));

        return new DraftProposal(ticket, classification, retrieval, draft, verification, confidence, composed, zammadTickets, timings);
    }

    /// <summary>Bozza condivisa, nota interna e tag; restituisce la proposta con il tempo di pubblicazione e i tag scritti.</summary>
    public async Task<(DraftProposal Proposal, IReadOnlyList<string> Tags)> PublishAsync(
        DraftProposal proposal, int? runId, string? traceUrl, CancellationToken cancellationToken)
    {
        string note = InternalNoteComposer.Compose(proposal, options.ConfidenceMinForDraft, zammadOptions.BaseUrl, traceUrl, runId);

        (IReadOnlyList<string> tags, TimeSpan publicationTime) = await StepAsync("pubblicazione", () =>
            publisher.PublishAsync(proposal.Ticket.ZammadTicketId, proposal.Draft, note, cancellationToken));

        PipelineTimings timings = proposal.Timings with { Publication = publicationTime, Total = proposal.Timings.Total + publicationTime };

        return (proposal with { Timings = timings }, tags);
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
