using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dusiburg.AI.Ticket.Shared.Demo;

namespace Dusiburg.AI.Ticket.Data.Seed;

/// <summary>
/// Contenuto di <c>data/</c> letto e già espanso: i modelli comuni ai due prodotti (<c>{P}</c>, <c>{prodotto}</c>) diventano un
/// articolo e un caso per prodotto, i clienti comprendono quelli di <see cref="DemoCatalog"/>.
/// </summary>
public sealed class SeedData
{
    public const string TicketsFileName = "tickets.json";

    private SeedData(
        IReadOnlyList<CustomerDefinition> customers,
        IReadOnlyList<ReleaseNoteDefinition> releaseNotes,
        IReadOnlyList<KbArticleDefinition> kbArticles,
        IReadOnlyList<CaseInstance> cases,
        NoiseFile noise,
        IReadOnlyList<TrapDefinition> traps,
        TicketsFile? tickets)
    {
        Customers = customers;
        ReleaseNotes = releaseNotes;
        KbArticles = kbArticles;
        Cases = cases;
        Noise = noise;
        Traps = traps;
        Tickets = tickets;
    }

    /// <summary>Clienti del catalogo demo seguiti da quelli di <c>clienti.json</c>.</summary>
    public IReadOnlyList<CustomerDefinition> Customers { get; }

    public IReadOnlyList<ReleaseNoteDefinition> ReleaseNotes { get; }

    /// <summary>Un articolo per prodotto (i modelli comuni sono già espansi).</summary>
    public IReadOnlyList<KbArticleDefinition> KbArticles { get; }

    /// <summary>Un caso per prodotto (i casi trasversali compaiono una volta per prodotto, con lo stesso codice).</summary>
    public IReadOnlyList<CaseInstance> Cases { get; }

    public NoiseFile Noise { get; }

    public IReadOnlyList<TrapDefinition> Traps { get; }

    /// <summary>Ticket generati; <c>null</c> finché il generatore non ha scritto <c>tickets.json</c>.</summary>
    public TicketsFile? Tickets { get; }

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static SeedData Load(string dataDirectory)
    {
        CustomersFile customers = Read<CustomersFile>(dataDirectory, "clienti.json");
        ReleaseNotesFile notes = Read<ReleaseNotesFile>(dataDirectory, "note-rilascio.json");
        string ticketsPath = Path.Combine(dataDirectory, TicketsFileName);

        return new SeedData(
            [.. DemoCatalog.Customers.Select(FromDemo), .. customers.Customers],
            notes.ReleaseNotes,
            ReadAll<KbFile>(dataDirectory, "kb").SelectMany(f => f.Articles).SelectMany(ExpandArticle).ToList(),
            ReadAll<CasesFile>(dataDirectory, "scenari").SelectMany(f => f.Cases).SelectMany(ExpandCase).ToList(),
            Read<NoiseFile>(dataDirectory, "rumore.json"),
            Read<TrapsFile>(dataDirectory, "trabocchetti.json").Traps,
            File.Exists(ticketsPath) ? Read<TicketsFile>(dataDirectory, TicketsFileName) : null);
    }

    /// <summary>Cartella <c>data/</c> del repository: la prima che si trova risalendo da <paramref name="startDirectory"/>.</summary>
    public static string? FindDataDirectory(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "data");

            if (File.Exists(Path.Combine(candidate, "clienti.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string ProductName(string productCode) => DemoCatalog.Product(productCode).Name;

    private static CustomerDefinition FromDemo(DemoCustomer demo) =>
        new(demo.EmailDomain, demo.Name, demo.ProductCode, demo.Version, demo.DeploymentMode, demo.SlaLevel);

    private static IEnumerable<KbArticleDefinition> ExpandArticle(KbArticleDefinition article) =>
        article.Products.Select(product => article with
        {
            Code = ExpandProduct(article.Code, product),
            Products = [product],
            Title = ExpandProduct(article.Title, product),
            Sections = [.. article.Sections.Select(s => new KbSection(ExpandProduct(s.Title, product), ExpandProduct(s.Body, product)))]
        });

    private static IEnumerable<CaseInstance> ExpandCase(CaseDefinition definition) =>
        definition.Products.Select(product => new CaseInstance(
            definition,
            product,
            definition.ErrorCode is null ? null : ExpandProduct(definition.ErrorCode, product),
            definition.VersionFrom.For(product),
            definition.VersionTo.For(product),
            [.. definition.Kb.Select(code => ExpandProduct(code, product))],
            // Le note citate da un caso trasversale valgono solo per il loro prodotto.
            [.. definition.ReleaseNotes.Where(code => code.StartsWith($"RN-{product}-", StringComparison.Ordinal))]));

    /// <summary><c>{P}</c> → codice del prodotto, <c>{prodotto}</c> → nome.</summary>
    internal static string ExpandProduct(string text, string product) =>
        text.Replace("{P}", product, StringComparison.Ordinal).Replace("{prodotto}", ProductName(product), StringComparison.Ordinal);

    private static T Read<T>(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);

        using FileStream stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException($"File vuoto: {path}");
    }

    private static IEnumerable<T> ReadAll<T>(string directory, string folder) =>
        Directory.GetFiles(Path.Combine(directory, folder), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => Read<T>(Path.GetDirectoryName(path)!, Path.GetFileName(path)));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // A capo fissi: il file versionato non dipende dal sistema operativo che lo genera.
            NewLine = "\n",
            // Testi in italiano leggibili nel file versionato (accenti e virgolette non escapati).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}

/// <summary>Un caso della matrice per un prodotto, con codice errore, intervallo di versioni e fonti già risolti.</summary>
public sealed record CaseInstance(
    CaseDefinition Definition,
    string Product,
    string? ErrorCode,
    string? VersionFrom,
    string? VersionTo,
    IReadOnlyList<string> Kb,
    IReadOnlyList<string> ReleaseNotes)
{
    public string Code => Definition.Code;

    /// <summary>Sostituisce i segnaposto del caso: <c>{P}</c>, <c>{prodotto}</c>, <c>{codice}</c>, <c>{versione}</c>.</summary>
    public string Expand(string text, string? version) =>
        SeedData.ExpandProduct(text, Product)
            .Replace("{codice}", ErrorCode ?? string.Empty, StringComparison.Ordinal)
            .Replace("{versione}", version ?? string.Empty, StringComparison.Ordinal);
}
