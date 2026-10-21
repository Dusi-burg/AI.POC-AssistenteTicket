using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Verification;

/// <summary>Segnali della confidenza (P6), tutti fra 0 e 1, e la decisione del codice.</summary>
public sealed record ConfidenceAssessment(
    double Retrieval, double Citation, double Coverage, double? SelfAssessment, double Confidence, DraftKind Kind, string Reason);

/// <summary>
/// Confidenza calcolata dal codice (P6), non dal modello: l'autovalutazione di un 9B non è calibrata e pesa poco.
/// <list type="bullet">
/// <item><b>Retrieval</b>: distanza coseno della fonte più vicina fra quelle del prompt. Il punteggio RRF non serve, dipende solo
/// dalle posizioni; la distanza separa i casi con storico (0,13–0,30) da quelli senza (0,44–0,52, fase-5.md "Esito").</item>
/// <item><b>Citazioni</b>: media fra citazioni verificate e termini tecnici supportati dalle fonti; 0 se una risposta non cita nulla.</item>
/// <item><b>Copertura</b>: cala con le informazioni mancanti indicate dalla classificazione.</item>
/// </list>
/// Sotto la soglia (<c>CONFIDENCE_MIN_FOR_DRAFT</c>) la risposta non si propone: chiarimenti se mancano informazioni o lo storico
/// è vicino, escalation se il ticket è chiaro ma lo storico non ha nulla di simile. Lo stesso vale, a qualunque confidenza, se il
/// modello dichiara di non poter rispondere o restituisce una risposta vuota (CHECKPOINT 6: bozza vuota pubblicata come risposta).
/// </summary>
public static class ConfidenceCalculator
{
    /// <summary>Distanza a cui il retrieval vale 1: tipica di un ticket dello stesso caso.</summary>
    public const double GoodDistance = 0.20;

    /// <summary>Distanza a cui il retrieval vale 0: tipica di un tema assente dallo storico.</summary>
    public const double PoorDistance = 0.45;

    /// <summary>Sotto questo valore di retrieval lo storico "non ha nulla di simile".</summary>
    public const double NoHistoryRetrieval = 0.2;

    public const double RetrievalWeight = 0.45;
    public const double CitationWeight = 0.25;
    public const double CoverageWeight = 0.15;
    public const double SelfAssessmentWeight = 0.15;

    /// <summary>Per ogni informazione mancante la copertura perde questo valore.</summary>
    public const double MissingInformationPenalty = 0.3;

    /// <param name="modelCanAnswer">"puoRispondere" del modello con una risposta non vuota.</param>
    public static ConfidenceAssessment Assess(
        IReadOnlyList<RetrievedSource> promptSources, VerificationResult verification, int missingInformation, double? selfAssessment,
        bool modelCanAnswer, double threshold)
    {
        double retrieval = RetrievalScore(promptSources);
        double citation = verification.Citations.Count == 0 ? 0 : (verification.CitationRatio + verification.SupportRatio) / 2;
        double coverage = Math.Max(0, 1 - MissingInformationPenalty * missingInformation);
        double self = Math.Clamp(selfAssessment ?? 0.5, 0, 1);
        double confidence = Math.Round(
            RetrievalWeight * retrieval + CitationWeight * citation + CoverageWeight * coverage + SelfAssessmentWeight * self, 3);

        (DraftKind kind, string reason) = !modelCanAnswer
            ? NoAnswer(retrieval, missingInformation, "il modello non può rispondere con le fonti")
            : confidence >= threshold
                ? (DraftKind.Answer, $"confidenza {confidence:0.00} ≥ soglia {threshold:0.00}")
                : NoAnswer(retrieval, missingInformation, $"confidenza {confidence:0.00} < soglia {threshold:0.00}");

        return new ConfidenceAssessment(Math.Round(retrieval, 3), Math.Round(citation, 3), Math.Round(coverage, 3), selfAssessment, confidence, kind, reason);
    }

    private static (DraftKind Kind, string Reason) NoAnswer(double retrieval, int missingInformation, string cause) =>
        missingInformation == 0 && retrieval < NoHistoryRetrieval
            ? (DraftKind.Escalation, $"{cause}, ticket chiaro ma nessun caso simile nello storico")
            : (DraftKind.Clarification, $"{cause}, " +
                (missingInformation > 0 ? $"{missingInformation} informazioni mancanti" : "fonti non sufficienti"));

    /// <summary>Lineare fra <see cref="GoodDistance"/> (1) e <see cref="PoorDistance"/> (0); 0 senza fonti con una distanza.</summary>
    public static double RetrievalScore(IReadOnlyList<RetrievedSource> promptSources)
    {
        double? best = promptSources.SelectMany(s => s.Chunks).Select(c => c.VectorDistance).Where(d => d is not null).Min();

        return best is { } distance ? Math.Clamp((PoorDistance - distance) / (PoorDistance - GoodDistance), 0, 1) : 0;
    }
}
