using Dusiburg.AI.Ticket.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Risorse esterne (G3): nessun container lato Windows. Valore negli user-secrets dell'AppHost:
// ConnectionStrings:sql = Server=(localdb)\localdev;Database=Ticket;Trusted_Connection=True;TrustServerCertificate=True
var sql = builder.AddConnectionString("sql");

// Porte fisse dai launchSettings: Agent 5401, Web 5402. O2C usa 5101-5106, Sinistri 5201-5202, Trasporto 5301-5306.
// L'Agent ascolta su tutte le interfacce senza il proxy di Aspire: il webhook di Zammad arriva da WSL su 172.24.176.1:5401
// (fase-0.md §1 bis), e la regola del firewall di Windows "WSL -> Helpdesk Agent (TCP 5401)" ammette solo la rete di WSL.
var agent = builder.AddProject<Projects.Dusiburg_AI_Ticket_Agent>("agent")
    .WithEndpoint("http", endpoint =>
    {
        endpoint.IsProxied = false;
        endpoint.TargetHost = "0.0.0.0";
    })
    .WithReference(sql)
    .WithHttpHealthCheck("/health");

// Manopole della demo (PLAN §8): si impostano sull'AppHost — riga di comando, user-secrets o variabili d'ambiente — e
// arrivano all'Agent; senza valore vale il default del codice (AiOptions, ZammadOptions).
agent.WithConfigurationEnvironment(builder, [.. ModelSettings.All, .. RetrievalSettings.All, .. PipelineSettings.All, ZammadSettings.BaseUrlKey]);

// Ollama (G5) non è un container: è quello installato sul PC. Se all'avvio non risponde, l'AppHost lo avvia come propria
// risorsa ("ollama serve") e lo ferma quando si chiude; se è già in esecuzione non lo tocca.
var ollamaEndpoint = new Uri(builder.Configuration[ModelSettings.OllamaEndpointKey] is { Length: > 0 } configured ? configured : ModelSettings.DefaultOllamaEndpoint);

if (builder.ExecutionContext.IsRunMode && ollamaEndpoint.IsLoopback && !AppHostExtensions.IsListening(ollamaEndpoint))
{
    // Con l'AI nativa di Zammad (Fase 9a) Ollama deve ascoltare anche verso WSL: la variabile d'ambiente OLLAMA_HOST dell'utente
    // (0.0.0.0:11434) vale anche per l'Ollama avviato qui. L'Agent continua a chiamarlo su 127.0.0.1 (G5); la regola del
    // firewall "WSL -> Ollama (TCP 11434)" ammette solo la rete di WSL.
    string ollamaHost = builder.Configuration[ModelSettings.OllamaHostKey] is { Length: > 0 } inherited
        ? inherited
        : $"{ollamaEndpoint.Host}:{ollamaEndpoint.Port}";

    // Contesto di default uguale a OLLAMA_NUM_CTX della pipeline: l'AI di Zammad non passa num_ctx, e con un contesto diverso
    // Ollama ricarica il modello a ogni cambio di chiamante (~4,5 s, misurato nella Fase 9a).
    string contextLength = builder.Configuration[ModelSettings.OllamaNumCtxKey] is { Length: > 0 } numCtx ? numCtx : ModelSettings.DefaultOllamaNumCtx;

    var ollama = builder.AddExecutable("ollama", "ollama", ".", "serve")
        .WithEnvironment("OLLAMA_HOST", ollamaHost)
        .WithEnvironment("OLLAMA_CONTEXT_LENGTH", contextLength);

    agent.WaitFor(ollama);
}

// Zammad nello stack Docker ufficiale dentro WSL (G7), avviato in primo piano: tiene viva la VM finché gira l'AppHost.
// L'Agent non lo aspetta (il primo avvio richiede decine di secondi): lo stato si legge in /api/health.
var zammad = ZammadStack.FromConfiguration(builder.Configuration, builder.AppHostDirectory);

if (zammad.Managed)
{
    builder.AddZammadStack(zammad);
}

// Token dell'utente tecnico ai-assistant, scritto da "zammad bootstrap" (Fase 4): Parameters:zammad-api-token.
// Il parametro si dichiara solo se valorizzato, così l'AppHost parte anche senza e /api/health segnala la mancanza.
if (builder.Configuration["Parameters:zammad-api-token"] is { Length: > 0 })
{
    agent.WithEnvironment(ZammadSettings.ApiTokenKey, builder.AddParameter("zammad-api-token", secret: true));
}

// Segreto HMAC del webhook di Zammad (generato da "zammad bootstrap"): l'Agent verifica la firma delle consegne (Fase 6).
if (builder.Configuration["Parameters:zammad-webhook-secret"] is { Length: > 0 })
{
    agent.WithEnvironment("ZAMMAD_WEBHOOK_SECRET", builder.AddParameter("zammad-webhook-secret", secret: true));
}

// Chiave del modello Claude, solo per il confronto della Fase 8 (G10): Parameters:anthropic-api-key, dichiarata se valorizzata.
if (builder.Configuration["Parameters:anthropic-api-key"] is { Length: > 0 })
{
    agent.WithEnvironment("ANTHROPIC_API_KEY", builder.AddParameter("anthropic-api-key", secret: true));
}

// Console: solo HTTP verso l'Agent, nessun riferimento a sql. I link a Zammad e al dashboard arrivano come variabili.
var web = builder.AddProject<Projects.Dusiburg_AI_Ticket_Web>("web")
    .WithReference(agent)
    .WithEnvironment(ZammadSettings.BaseUrlKey, zammad.BrowserUrl)
    .WithHttpHealthCheck("/health");

if (builder.Configuration["ASPNETCORE_URLS"]?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } dashboard)
{
    web.WithEnvironment("TICKET_DASHBOARD_URL", dashboard);

    // Link alla traccia di ogni elaborazione nella nota interna di Zammad (Fase 6).
    agent.WithEnvironment("TICKET_DASHBOARD_URL", dashboard);
}

builder.Build().Run();
