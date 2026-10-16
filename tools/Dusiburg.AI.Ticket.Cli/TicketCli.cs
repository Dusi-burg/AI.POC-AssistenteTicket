using System.CommandLine;
using Dusiburg.AI.Ticket.Cli.Commands;

namespace Dusiburg.AI.Ticket.Cli;

/// <summary>
/// Riga di comando del POC. Exit code 0 esito positivo, 1 errore (configurazione, Zammad, database). I comandi successivi
/// (<c>embed</c>, <c>search</c>, <c>eval</c>, <c>bench</c>) si aggiungono qui fase per fase.
/// </summary>
internal static class TicketCli
{
    public const string VerboseOption = "--verbose";

    public const int ExitError = 1;

    public static async Task<int> InvokeAsync(string[] args, IServiceProvider services)
    {
        // Letta prima di costruire l'host (Program.cs); dichiarata qui perché compaia nell'help.
        var verbose = new Option<bool>(VerboseOption) { Description = "Mostra a console anche i log informativi.", Recursive = true };

        var root = new RootCommand("Assistente ticket: bozze di risposta in Zammad con RAG locale.")
        {
            verbose,
            ZammadCommands.Create(services),
            DemoCommand.Create(services)
        };

        // Le eccezioni arrivano a Program.cs, che stampa solo il messaggio (senza lo stack trace del gestore predefinito).
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
    }
}
