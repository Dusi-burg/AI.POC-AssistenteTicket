using System.Globalization;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Web.Api;

/// <summary>Come si mostrano elaborazioni, bozze ed esiti: etichette in italiano, classi dei badge, numeri.</summary>
public static class RunPresentation
{
    public static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string Label(AgentRunState state) => state switch
    {
        AgentRunState.Queued => "In coda",
        AgentRunState.Running => "In corso",
        AgentRunState.Completed => "Completata",
        AgentRunState.Failed => "Errore",
        AgentRunState.Skipped => "Saltata",
        _ => state.ToString()
    };

    public static string BadgeClass(AgentRunState state) => state switch
    {
        AgentRunState.Completed => "text-bg-success",
        AgentRunState.Failed => "text-bg-danger",
        AgentRunState.Running => "text-bg-primary",
        _ => "text-bg-secondary"
    };

    public static string Label(DraftKind kind) => kind switch
    {
        DraftKind.Answer => "Risposta",
        DraftKind.Clarification => "Chiarimenti",
        DraftKind.Escalation => "Escalation",
        _ => kind.ToString()
    };

    public static string BadgeClass(DraftKind kind) => kind switch
    {
        DraftKind.Answer => "text-bg-primary",
        DraftKind.Clarification => "text-bg-info",
        _ => "text-bg-warning"
    };

    public static string Label(DraftOutcome outcome) => outcome switch
    {
        DraftOutcome.Unknown => "In attesa",
        DraftOutcome.SentUnchanged => "Inviata com'era",
        DraftOutcome.SentEdited => "Inviata modificata",
        DraftOutcome.Discarded => "Scartata",
        _ => outcome.ToString()
    };

    public static string BadgeClass(DraftOutcome outcome) => outcome switch
    {
        DraftOutcome.SentUnchanged => "text-bg-success",
        DraftOutcome.SentEdited => "text-bg-primary",
        DraftOutcome.Discarded => "text-bg-dark",
        _ => "text-bg-light border"
    };

    /// <summary>Tag P8 che l'esito mette sul ticket (fase-7.md §2).</summary>
    public static string? AiTag(DraftOutcome outcome) => outcome switch
    {
        DraftOutcome.SentUnchanged => "ai-generata",
        DraftOutcome.SentEdited => "ai-generata-in-parte",
        _ => null
    };

    public static string Label(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Webhook => "webhook",
        RunTrigger.Polling => "polling",
        RunTrigger.Manual => "manuale",
        _ => trigger.ToString()
    };

    public static string Label(ChunkSource source) => source switch
    {
        ChunkSource.HistoricalTicket => "ticket risolto",
        ChunkSource.KbArticle => "articolo KB",
        ChunkSource.ReleaseNote => "nota di rilascio",
        _ => source.ToString()
    };

    public static string Label(TicketPriority? priority) => priority switch
    {
        TicketPriority.High => "alta",
        TicketPriority.Normal => "normale",
        TicketPriority.Low => "bassa",
        _ => "—"
    };

    public static string Label(DeploymentMode? mode) => mode switch
    {
        DeploymentMode.OnPrem => "on-premise",
        DeploymentMode.Saas => "SaaS",
        _ => "entrambe"
    };

    public static string Number(double value) => value.ToString("0.00", Italian);

    public static string Number(double? value) => value is { } v ? Number(v) : "—";

    public static string Percent(int part, int total) => total == 0 ? "—" : ((double)part / total).ToString("0%", Italian);

    public static string Seconds(int milliseconds) => (milliseconds / 1000.0).ToString("0.0", Italian) + " s";

    public static string Seconds(int? milliseconds) => milliseconds is { } ms ? Seconds(ms) : "—";

    public static string Time(DateTimeOffset? at) => at is { } value ? value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", Italian) : "—";
}
