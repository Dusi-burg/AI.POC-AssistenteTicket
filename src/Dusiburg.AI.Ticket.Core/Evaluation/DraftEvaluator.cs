using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Core.Evaluation;

/// <summary>
/// <c>eval drafts</c> (fase-8.md §2): la pipeline della Fase 6 a secco sui ticket del test set. Il contesto si costruisce dal
/// database (oggetto, primo messaggio del cliente, cliente), il resto è identico all'Agent: classificazione, retrieval, bozza,
/// verifica, confidenza e decisione. Le fonti rilevanti sono quelle dello stesso caso, come in <c>eval retrieval</c>.
/// </summary>
public sealed class DraftEvaluator(TicketDbContext db, RetrievalEvaluator retrievalEvaluator)
{
    /// <summary>Casi normali oltre ai trabocchetti: con i 15 trabocchetti fanno i ~30 della griglia manuale.</summary>
    public const int DefaultNormalCases = 15;

    /// <summary>I trabocchetti del test set e <paramref name="normalCount"/> casi normali stratificati per prodotto e modalità.</summary>
    public async Task<IReadOnlyList<EvalCase>> SelectCasesAsync(Data.Seed.SeedData seed, int normalCount, CancellationToken cancellationToken)
    {
        (IReadOnlyList<EvalCase> cases, _) = await retrievalEvaluator.LoadCasesAsync(seed, cancellationToken);

        return Select(cases, normalCount);
    }

    /// <summary>
    /// Deterministica: tutti i trabocchetti, poi i casi normali con fonti rilevanti divisi in gruppi (prodotto, modalità) e presi a
    /// turno, a intervalli regolari nell'ordine per numero, così la scelta copre tutto il test set senza un seme casuale.
    /// </summary>
    public static IReadOnlyList<EvalCase> Select(IReadOnlyList<EvalCase> cases, int normalCount)
    {
        List<EvalCase> traps = [.. cases.Where(c => c.Trap is not null).OrderBy(c => c.TicketNumber, StringComparer.Ordinal)];
        List<List<EvalCase>> groups = [.. cases
            .Where(c => c.Trap is null && c.Relevant.Count > 0)
            .GroupBy(c => (c.ProductCode, c.Mode))
            .OrderBy(g => g.Key.ProductCode, StringComparer.Ordinal).ThenBy(g => g.Key.Mode)
            .Select(g => g.OrderBy(c => c.TicketNumber, StringComparer.Ordinal).ToList())];

        // A turno sui gruppi; un gruppo esaurito lascia il posto ai successivi.
        int[] quotas = new int[groups.Count];
        int assigned = 0;

        while (assigned < normalCount && Enumerable.Range(0, groups.Count).Any(g => quotas[g] < groups[g].Count))
        {
            for (int group = 0; group < groups.Count && assigned < normalCount; group++)
            {
                if (quotas[group] < groups[group].Count)
                {
                    quotas[group]++;
                    assigned++;
                }
            }
        }

        List<EvalCase> normal = [.. groups.SelectMany((group, index) => Spread(group, quotas[index]))];

        return [.. traps, .. normal.OrderBy(c => c.TicketNumber, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<DraftCaseRecord>> RunAsync(
        IReadOnlyList<EvalCase> cases, TicketPipeline pipeline, IProgress<EvalProgress>? progress, CancellationToken cancellationToken)
    {
        List<DraftCaseRecord> records = [];

        foreach (EvalCase evalCase in cases)
        {
            IncomingTicket ticket = await LoadTicketAsync(evalCase.TicketNumber, cancellationToken);

            try
            {
                DraftProposal proposal = await pipeline.ProposeForAsync(ticket, TimeSpan.Zero, cancellationToken);
                records.Add(ToRecord(evalCase, ticket, proposal));
            }
            // Un caso che non arriva alla bozza è un risultato da riportare; una configurazione sbagliata (chiave assente) ferma tutto.
            catch (Exception exception) when (exception is InvalidModelOutputException or HttpRequestException or TimeoutException
                || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                records.Add(Failed(evalCase, ticket, exception.Message));
            }

            progress?.Report(new EvalProgress(records.Count, cases.Count));
        }

        return records;
    }

    /// <summary>
    /// Il ticket del test set come lo vedrebbe l'Agent: oggetto, primo messaggio del cliente pulito come in Zammad (P4), contesto
    /// del cliente con prodotto, modalità e versione del ticket e SLA del cliente.
    /// </summary>
    internal async Task<IncomingTicket> LoadTicketAsync(string number, CancellationToken cancellationToken)
    {
        HistoricalTicket ticket = await db.HistoricalTickets.AsNoTracking().Include(t => t.Messages).SingleAsync(t => t.Number == number, cancellationToken);
        Customer customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == ticket.CustomerId, cancellationToken);
        string product = await db.Products.Where(p => p.Id == ticket.ProductId).Select(p => p.Code).SingleAsync(cancellationToken);
        string? version = ticket.ProductVersionId is { } versionId
            ? await db.ProductVersions.Where(v => v.Id == versionId).Select(v => v.Version).SingleAsync(cancellationToken)
            : null;

        string body = ticket.Messages.Where(m => m.Author == MessageAuthor.Customer).OrderBy(m => m.Sequence).FirstOrDefault()?.Body ?? ticket.Description;
        var message = new ThreadMessage(1, MessageAuthor.Customer, ticket.OpenedAt, MessageCleaner.Clean(body, [ticket.ContactName]));
        var context = new CustomerContext(customer.Name, ticket.ContactName, ticket.ContactEmail, product, version, ticket.DeploymentMode, customer.SlaLevel, "test set");

        return new IncomingTicket(0, ticket.Number, ticket.Subject, 1, [message], context);
    }

    internal static DraftCaseRecord ToRecord(EvalCase evalCase, IncomingTicket ticket, DraftProposal proposal)
    {
        ModelClassification classification = proposal.Classification.Value;
        ModelDraft model = proposal.ModelDraft.Value;
        VerificationResult verification = proposal.Verification;
        ConfidenceAssessment confidence = proposal.Confidence;
        PipelineTimings timings = proposal.Timings;
        List<string> prompt = [.. proposal.Retrieval.PromptSources.Select(s => s.Key)];

        return new DraftCaseRecord(
            evalCase.TicketNumber, evalCase.CaseCode, evalCase.Trap, evalCase.ProductCode, evalCase.Mode, evalCase.Version,
            ticket.Title, ticket.Thread[0].Text, null,
            classification.Category, classification.Priority, classification.MissingInformation, classification.SuspectedManipulation,
            model.CanAnswer, model.SelfAssessment, model.Answer,
            confidence.Kind, confidence.Confidence, confidence.Reason, proposal.Draft.Text,
            prompt, [.. prompt.Where(evalCase.Relevant.Contains)],
            [.. verification.Citations.Select(c => new CitationRecord(c.Id, c.IsVerified, evalCase.Relevant.Contains(c.Id)))],
            verification.UnsupportedTerms, verification.TechnicalTerms, verification.SignatureRemoved,
            proposal.ModelDraft.Attempts,
            Sum(proposal.Classification.InputTokens, proposal.ModelDraft.InputTokens),
            Sum(proposal.Classification.OutputTokens, proposal.ModelDraft.OutputTokens),
            new StepTimes(Ms(timings.Classification), Ms(timings.Retrieval), Ms(timings.Generation), Ms(timings.Verification)),
            proposal.Mode == PipelineMode.Agent
                ? new AgentTrace([.. proposal.Calls.Select(c => new ToolCallTrace(c.ToolName, c.Arguments, c.Outcome, Ms(c.Duration)))])
                : null);
    }

    private static DraftCaseRecord Failed(EvalCase evalCase, IncomingTicket ticket, string error) => new(
        evalCase.TicketNumber, evalCase.CaseCode, evalCase.Trap, evalCase.ProductCode, evalCase.Mode, evalCase.Version,
        ticket.Title, ticket.Thread[0].Text, error,
        null, null, [], false, false, null, string.Empty, null, null, null, null, [], [], [], [], 0, false, 0, null, null, null);

    /// <summary><paramref name="count"/> elementi a intervalli regolari, a partire dal centro del primo intervallo.</summary>
    private static IEnumerable<EvalCase> Spread(List<EvalCase> group, int count) =>
        Enumerable.Range(0, count).Select(i => group[(int)((i + 0.5) * group.Count / count)]);

    private static long? Sum(long? first, long? second) => first is { } a && second is { } b ? a + b : null;

    private static int Ms(TimeSpan duration) => (int)Math.Round(duration.TotalMilliseconds);
}
