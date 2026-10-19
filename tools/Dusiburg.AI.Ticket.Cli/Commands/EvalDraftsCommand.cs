using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Ai.Ollama;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Evaluation;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary>
/// <c>eval drafts</c> (fase-8.md §2): bozze sul test set, report in <c>eval/</c> con le letture in JSON e una griglia da compilare.
/// <c>--replay</c> rifà il report dalle letture senza il modello, <c>--griglia</c> aggiunge i giudizi, <c>--provider anthropic</c>
/// misura gli stessi casi con il modello cloud (G10).
/// </summary>
internal static class EvalDraftsCommand
{
    public const string GradeInstructions =
        "Per ogni voce: giudizio = \"corretta\" (si invia com'è), \"utile-con-modifiche\" (piccole modifiche) o \"da-scartare\"; " +
        "affermazioniNonSupportate = quante affermazioni tecniche della bozza non poggiano sulle fonti; nota e autore liberi. " +
        "Poi: eval drafts --replay <letture> --griglia <questo file>.";

    /// <summary>Come le API (camelCase, enum come stringhe), indentato e con gli accenti leggibili.</summary>
    internal static readonly JsonSerializerOptions JsonOptions =
        new(TicketJson.Options) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command Create(IServiceProvider services)
    {
        var provider = new Option<string?>("--provider") { Description = "ollama (default della configurazione) oppure anthropic per il confronto con il cloud." };
        provider.AcceptOnlyFromAmong(ModelProviders.All.ToArray());
        var normal = new Option<int>("--casi") { Description = "Casi normali oltre ai 15 trabocchetti.", DefaultValueFactory = _ => DraftEvaluator.DefaultNormalCases };
        var replay = new Option<string?>("--replay") { Description = "File delle letture (drafts-….json): rifà il report senza chiamare il modello." };
        var grades = new Option<string?>("--griglia") { Description = "Griglia compilata (griglia-….json) da applicare al report." };
        var command = new Command("drafts", "Bozze della pipeline sul test set: decisioni, verifica, tempi; report, letture e griglia in eval/.")
        {
            provider, normal, replay, grades
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            string directory = Path.Combine(Path.GetDirectoryName(SeedDataFiles.Directory())!, "eval");
            Directory.CreateDirectory(directory);

            string runFile = parse.GetValue(replay) is { } replayFile
                ? Path.GetFullPath(replayFile)
                : await RunAsync(services, parse.GetValue(provider), parse.GetValue(normal), directory, cancellationToken);

            DraftEvaluationRun run = JsonSerializer.Deserialize<DraftEvaluationRun>(await File.ReadAllTextAsync(runFile, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException($"{runFile} vuoto.");
            string? gradeFile = parse.GetValue(grades) is { } g ? Path.GetFullPath(g) : null;
            IReadOnlyList<DraftGrade>? sheet = gradeFile is null
                ? null
                : JsonSerializer.Deserialize<DraftGradeSheet>(await File.ReadAllTextAsync(gradeFile, cancellationToken), JsonOptions)?.Grades;

            string report = Path.ChangeExtension(runFile, ".md");
            await File.WriteAllTextAsync(report, DraftReportRenderer.Render(run, sheet, Path.GetFileName(runFile), gradeFile is null ? null : Path.GetFileName(gradeFile)), cancellationToken);

            DraftEvaluationSummary summary = DraftEvaluationSummary.Of(run, sheet);
            Console.WriteLine();
            Console.WriteLine($"  Casi {summary.Cases} (errori {summary.Failed}): risposte {summary.Answers}, trabocchetti rispettati {summary.TrapsMet}/{summary.TrapsWithExpectation}");
            Console.WriteLine($"  Termini non supportati nelle bozze proposte: {summary.PublishedUnsupportedTerms}/{summary.PublishedTechnicalTerms}");
            Console.WriteLine($"  Tempo medio {summary.Total.Mean / 1000:0.0} s, p95 {summary.Total.P95 / 1000:0.0} s{(summary.Cost is > 0 ? $", costo {summary.Cost:0.0000} $" : "")}");
            Console.WriteLine(summary.UsefulShare is { } useful ? $"  Bozze utili {summary.Useful}/{summary.Graded} ({useful:P1})" : "  Griglia non applicata");
            Console.WriteLine();
            Console.WriteLine($"Report: {report}");

            return 0;
        });

        return command;
    }

    /// <summary>Il giro col modello: letture in JSON e, se non c'è già, la griglia vuota accanto. Restituisce il file delle letture.</summary>
    private static async Task<string> RunAsync(IServiceProvider services, string? provider, int normalCount, string directory, CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        AiOptions aiOptions = services.GetRequiredService<AiOptions>();
        TicketPipeline pipeline = scope.ServiceProvider.GetRequiredService<TicketPipeline>();
        IChatClientFactory factory = services.GetRequiredService<IChatClientFactory>();

        if (provider is not null)
        {
            AiOptions overridden = aiOptions with { TaskProviders = Enum.GetValues<ModelTask>().ToDictionary(task => task, _ => provider) };
            factory = new ChatClientFactory(overridden, services.GetRequiredService<ILoggerFactory>());
            pipeline = pipeline.WithModel(new TicketLanguageModel(factory));
        }

        (string effectiveProvider, string classifyModel) = Describe(factory, ModelTask.Classify);
        (_, string draftModel) = Describe(factory, ModelTask.Draft);

        DraftEvaluator evaluator = scope.ServiceProvider.GetRequiredService<DraftEvaluator>();
        IReadOnlyList<EvalCase> cases = await evaluator.SelectCasesAsync(SeedDataFiles.Load(), normalCount, cancellationToken);

        Console.WriteLine($"Bozze di {cases.Count} casi del test set con {effectiveProvider} ({draftModel})…");
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<DraftCaseRecord> records = await evaluator.RunAsync(cases, pipeline, new ConsoleProgress(), cancellationToken);
        TimeSpan duration = Stopwatch.GetElapsedTime(start);

        IReadOnlyList<string> loaded = effectiveProvider == ModelProviders.Ollama
            ? await OllamaHealthProbe.DescribeLoadedModelsAsync(aiOptions, cancellationToken)
            : [];

        var run = new DraftEvaluationRun(DateTimeOffset.Now, effectiveProvider, classifyModel, draftModel, pipeline.Options.ConfidenceMinForDraft, duration, loaded, records);
        string name = $"drafts-{run.RunAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{Slug(draftModel)}";
        string runFile = Path.Combine(directory, $"{name}.json");
        await File.WriteAllTextAsync(runFile, JsonSerializer.Serialize(run, JsonOptions), cancellationToken);

        string gradeFile = Path.Combine(directory, $"griglia-{name["drafts-".Length..]}.json");

        if (!File.Exists(gradeFile))
        {
            var sheet = new DraftGradeSheet(Path.GetFileName(runFile), GradeInstructions, [.. records.Select(r => new DraftGrade(r.TicketNumber, null, null, null, null))]);
            await File.WriteAllTextAsync(gradeFile, JsonSerializer.Serialize(sheet, JsonOptions), cancellationToken);
            Console.WriteLine($"Griglia da compilare: {gradeFile}");
        }

        return runFile;
    }

    private static (string Provider, string ModelId) Describe(IChatClientFactory factory, ModelTask task)
    {
        using ChatModel model = factory.Create(task);

        return (model.Provider, model.ModelId);
    }

    /// <summary><c>qwen3.5:9b</c> → <c>qwen3.5-9b</c>, per il nome del file.</summary>
    internal static string Slug(string model) => model.Replace(':', '-').Replace('/', '-');

    private sealed class ConsoleProgress : IProgress<EvalProgress>
    {
        public void Report(EvalProgress value) => Console.WriteLine($"  {value.Done}/{value.Total}");
    }
}
