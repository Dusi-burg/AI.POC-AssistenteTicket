using System.Security.Cryptography;
using System.Text.Json;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Cli.Zammad;

/// <summary>Parametri del bootstrap che non vengono dall'helpdesk.</summary>
/// <param name="Operators">Operatori dello storico (<c>data/rumore.json</c>), creati come agenti.</param>
/// <param name="RestartDownWindow">Quanto aspettare che Zammad vada giù dopo le migrazioni (se non succede, si prosegue).</param>
/// <param name="RestartUpTimeout">Quanto aspettare che torni su.</param>
internal sealed record BootstrapSettings(
    Uri WebhookTarget,
    IReadOnlyList<string> Operators,
    TimeSpan RestartDownWindow,
    TimeSpan RestartUpTimeout,
    TimeSpan PollInterval);

/// <summary>Esito: cosa è stato creato o modificato e cosa c'era già. Un secondo giro senza modifiche ha <see cref="Changed"/> vuoto.</summary>
internal sealed class BootstrapReport
{
    public List<string> Changed { get; } = [];

    public List<string> Present { get; } = [];
}

/// <summary>
/// <c>zammad bootstrap</c> (fase-4.md §2): porta l'istanza Zammad alla struttura del POC. Ogni passo legge lo stato e crea solo
/// ciò che manca, quindi si può rilanciare. Serve il token di un amministratore.
/// </summary>
internal sealed class ZammadBootstrapper(ZammadClient zammad, UserSecretsStore secrets, BootstrapSettings settings, TextWriter output, TimeProvider time)
{
    private readonly BootstrapReport _report = new();

    public async Task<BootstrapReport> RunAsync(bool rotateToken, CancellationToken cancellationToken)
    {
        ZammadUser admin = await zammad.GetCurrentUserAsync(cancellationToken);

        if (!admin.IsAdmin)
        {
            throw new InvalidOperationException(
                $"Il token in uso è di '{admin.Login}', che non è amministratore: il bootstrap richiede un token con il ruolo Admin " +
                $"({UserSecretsStore.AdminTokenKey} negli user-secrets dell'AppHost o {CliConfiguration.AdminTokenVariable}).");
        }

        IReadOnlyDictionary<string, long> groups = await EnsureGroupsAsync(cancellationToken);
        await EnsureAttributesAsync(cancellationToken);
        await EnsureTagsAsync(cancellationToken);
        await EnsureUsersAsync(admin, groups, cancellationToken);
        await EnsureAssistantTokenAsync(admin, rotateToken, cancellationToken);
        await EnsureWebhookAsync(groups, cancellationToken);

        return _report;
    }

    private async Task<IReadOnlyDictionary<string, long>> EnsureGroupsAsync(CancellationToken cancellationToken)
    {
        List<ZammadGroup> existing = [.. await zammad.ListAllAsync<ZammadGroup>(ZammadResources.Groups, cancellationToken)];
        Dictionary<string, long> ids = [];

        foreach (string name in ZammadDirectory.Groups)
        {
            ZammadGroup? group = existing.SingleOrDefault(g => g.Name == name);

            if (group is null)
            {
                // Le bozze condivise (P3) si attivano per gruppo.
                group = await zammad.CreateAsync<ZammadGroup>(ZammadResources.Groups,
                    new { Name = name, Active = true, SharedDrafts = true, FollowUpPossible = "yes", Note = "Assistente ticket (POC)" }, cancellationToken);
                Changed($"gruppo '{name}' creato");
            }
            else if (group.SharedDrafts != true)
            {
                await zammad.UpdateAsync<ZammadGroup>(ZammadResources.Groups, group.Id, new { SharedDrafts = true }, cancellationToken);
                Changed($"gruppo '{name}': bozze condivise attivate");
            }
            else
            {
                Present($"gruppo '{name}'");
            }

            ids[name] = group.Id;
        }

        return ids;
    }

    /// <summary>Tutti i campi mancanti e poi una sola migrazione: ognuna riavvia il railsserver (fase-0.md V5).</summary>
    private async Task EnsureAttributesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ZammadObjectAttribute> existing = await zammad.GetAsync<List<ZammadObjectAttribute>>(ZammadResources.ObjectAttributes, cancellationToken);

        foreach (AttributeDefinition attribute in ZammadDirectory.Attributes)
        {
            if (existing.Any(a => a.Object == attribute.Object && a.Name == attribute.Name))
            {
                Present($"campo {attribute.Object}.{attribute.Name}");
                continue;
            }

            await zammad.CreateAsync<ZammadObjectAttribute>(ZammadResources.ObjectAttributes, new
            {
                attribute.Object, attribute.Name, attribute.Display, attribute.DataType, attribute.DataOption, attribute.Screens,
                attribute.Position, Active = true, Editable = true
            }, cancellationToken);
            Changed($"campo {attribute.Object}.{attribute.Name} creato");
        }

        IReadOnlyList<ZammadObjectAttribute> pending = [.. (await zammad.GetAsync<List<ZammadObjectAttribute>>(ZammadResources.ObjectAttributes, cancellationToken))
            .Where(a => a.ToCreate || a.ToMigrate)];

        if (pending.Count > 0)
        {
            output.WriteLine($"Migrazione di {pending.Count} campi: Zammad riavvia il railsserver, attendo che torni disponibile...");
            await zammad.ExecuteObjectMigrationsAsync(cancellationToken);
            await WaitForRestartAsync(cancellationToken);
            Changed($"migrazione dei campi eseguita ({pending.Count})");
        }
    }

    private async Task EnsureTagsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> existing = [.. (await zammad.GetAsync<List<ZammadTag>>(ZammadResources.Tags, cancellationToken)).Select(t => t.Name)];

        foreach (string tag in ZammadDirectory.Tags)
        {
            if (existing.Contains(tag))
            {
                Present($"tag '{tag}'");
                continue;
            }

            await zammad.CreateTagAsync(tag, cancellationToken);
            Changed($"tag '{tag}' creato");
        }
    }

    /// <summary>
    /// <c>ai-assistant</c> e gli operatori dello storico come agenti dei tre gruppi; anche l'amministratore riceve l'accesso ai
    /// gruppi, altrimenti non vedrebbe i ticket del POC.
    /// </summary>
    private async Task EnsureUsersAsync(ZammadUser admin, IReadOnlyDictionary<string, long> groups, CancellationToken cancellationToken)
    {
        long agentRole = (await zammad.ListAllAsync<ZammadRole>(ZammadResources.Roles, cancellationToken)).Single(r => r.Name == ZammadDirectory.AgentRole).Id;
        List<ZammadUserRecord> users = [.. await zammad.ListAllAsync<ZammadUserRecord>(ZammadResources.Users, cancellationToken)];
        Dictionary<string, IReadOnlyList<string>> fullAccess = groups.Values.ToDictionary(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => (IReadOnlyList<string>)["full"]);

        IEnumerable<(string Login, string Email, string First, string Last)> agents =
        [
            (ZammadDirectory.AssistantLogin, ZammadDirectory.AssistantEmail, "Assistente", "AI"),
            .. settings.Operators.Select(name => (ZammadDirectory.OperatorEmail(name), ZammadDirectory.OperatorEmail(name), ZammadDirectory.SplitName(name).First, ZammadDirectory.SplitName(name).Last)),
        ];

        foreach ((string login, string email, string first, string last) in agents)
        {
            ZammadUserRecord? user = users.SingleOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase));

            if (user is null)
            {
                await zammad.CreateAsync<ZammadUserRecord>(ZammadResources.Users, new
                {
                    Login = login, Email = email, Firstname = first, Lastname = last, RoleIds = new[] { agentRole }, GroupIds = fullAccess, Active = true
                }, cancellationToken);
                Changed($"utente '{login}' creato");
            }
            else
            {
                await EnsureGroupAccessAsync(user, fullAccess, cancellationToken);
            }
        }

        await EnsureGroupAccessAsync(users.Single(u => u.Id == admin.Id), fullAccess, cancellationToken);
    }

    private async Task EnsureGroupAccessAsync(ZammadUserRecord user, Dictionary<string, IReadOnlyList<string>> required, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<string>> current = user.GroupIds?.ToDictionary(g => g.Key, g => g.Value) ?? [];

        if (required.All(g => current.TryGetValue(g.Key, out IReadOnlyList<string>? access) && access.Contains("full")))
        {
            Present($"utente '{user.Login}'");
            return;
        }

        foreach ((string group, IReadOnlyList<string> access) in required)
        {
            current[group] = access;
        }

        await zammad.UpdateAsync<ZammadUserRecord>(ZammadResources.Users, user.Id, new { GroupIds = current }, cancellationToken);
        Changed($"utente '{user.Login}': accesso ai gruppi del POC");
    }

    /// <summary>
    /// Il token dell'Agent è quello di <c>ai-assistant</c>: se negli user-secrets c'è ancora quello dell'amministratore, lo si
    /// sposta sotto la chiave della Cli e si crea il token dell'utente tecnico agendo per suo conto.
    /// </summary>
    private async Task EnsureAssistantTokenAsync(ZammadUser admin, bool rotate, CancellationToken cancellationToken)
    {
        string? agentToken = secrets.Get(UserSecretsStore.ApiTokenKey);

        if (!rotate && agentToken is not null && await BelongsToAssistantAsync(agentToken, cancellationToken))
        {
            Present($"token di '{ZammadDirectory.AssistantLogin}' negli user-secrets");
            return;
        }

        Dictionary<string, string> updates = [];

        if (secrets.Get(UserSecretsStore.AdminTokenKey) is null && agentToken is not null
            && (await zammad.GetUserOfTokenAsync(agentToken, cancellationToken)).Id == admin.Id)
        {
            updates[UserSecretsStore.AdminTokenKey] = agentToken;
            Changed($"token amministratore spostato in {UserSecretsStore.AdminTokenKey}");
        }

        foreach (ZammadAccessToken old in (await zammad.ListAccessTokensAsync(ZammadDirectory.AssistantLogin, cancellationToken)).Where(t => t.Name == ZammadDirectory.TokenName))
        {
            await zammad.DeleteAccessTokenAsync(old.Id, ZammadDirectory.AssistantLogin, cancellationToken);
        }

        updates[UserSecretsStore.ApiTokenKey] = await zammad.CreateAccessTokenAsync(
            ZammadDirectory.TokenName, ZammadDirectory.TokenPermissions, ZammadDirectory.AssistantLogin, cancellationToken);
        secrets.Set(updates);
        Changed($"token di '{ZammadDirectory.AssistantLogin}' creato e salvato in {UserSecretsStore.ApiTokenKey}");
    }

    private async Task<bool> BelongsToAssistantAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            return (await zammad.GetUserOfTokenAsync(token, cancellationToken)).Login == ZammadDirectory.AssistantLogin;
        }
        catch (ZammadApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return false;
        }
    }

    /// <summary>Webhook firmato verso l'Agent e trigger sugli articoli del cliente, creato disattivo (lo attiva la Fase 6).</summary>
    private async Task EnsureWebhookAsync(IReadOnlyDictionary<string, long> groups, CancellationToken cancellationToken)
    {
        string secret = secrets.Get(UserSecretsStore.WebhookSecretKey) ?? CreateWebhookSecret();
        ZammadWebhook? webhook = (await zammad.GetAsync<List<ZammadWebhook>>(ZammadResources.Webhooks, cancellationToken))
            .SingleOrDefault(w => w.Name == ZammadDirectory.WebhookName);

        var desired = new
        {
            Name = ZammadDirectory.WebhookName, Endpoint = settings.WebhookTarget.ToString(), SignatureToken = secret, SslVerify = false,
            CustomizedPayload = true, CustomPayload = ZammadDirectory.WebhookPayload, Active = true,
            Note = "Assistente ticket (POC): nuovi articoli del cliente verso l'Agent"
        };

        if (webhook is null)
        {
            webhook = await zammad.CreateAsync<ZammadWebhook>(ZammadResources.Webhooks, desired, cancellationToken);
            Changed($"webhook '{webhook.Name}' creato verso {desired.Endpoint}");
        }
        // Zammad restituisce il segreto mascherato (solo asterischi): si confrontano indirizzo e payload. Il segreto si genera
        // una volta sola, e la Fase 6 lo verifica davvero sulla firma delle consegne.
        else if (webhook.Endpoint != desired.Endpoint || webhook.CustomPayload != desired.CustomPayload || !IsMasked(webhook.SignatureToken) && webhook.SignatureToken != secret)
        {
            webhook = await zammad.UpdateAsync<ZammadWebhook>(ZammadResources.Webhooks, webhook.Id, desired, cancellationToken);
            Changed($"webhook '{webhook.Name}' aggiornato");
        }
        else
        {
            Present($"webhook '{webhook.Name}'");
        }

        await EnsureTriggerAsync(webhook.Id, groups, cancellationToken);
    }

    private static bool IsMasked(string? token) => token is { Length: > 0 } && token.All(c => c == '*');

    private string CreateWebhookSecret()
    {
        string secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        secrets.Set(new Dictionary<string, string> { [UserSecretsStore.WebhookSecretKey] = secret });
        Changed($"segreto del webhook generato e salvato in {UserSecretsStore.WebhookSecretKey}");

        return secret;
    }

    private async Task EnsureTriggerAsync(long webhookId, IReadOnlyDictionary<string, long> groups, CancellationToken cancellationToken)
    {
        ZammadTrigger? trigger = (await zammad.GetAsync<List<ZammadTrigger>>(ZammadResources.Triggers, cancellationToken))
            .SingleOrDefault(t => t.Name == ZammadDirectory.TriggerName);

        if (trigger is not null && PerformsWebhook(trigger, webhookId))
        {
            Present($"trigger '{trigger.Name}' ({(trigger.Active ? "attivo" : "disattivo")})");
            return;
        }

        var desired = new
        {
            Name = ZammadDirectory.TriggerName,
            Activator = "action",
            ExecutionConditionMode = "selective",
            Condition = new Dictionary<string, object>
            {
                ["article.action"] = new { Operator = "is", Value = "create" },
                ["article.sender_id"] = new { Operator = "is", Value = new[] { ZammadDirectory.CustomerSenderId } },
                ["ticket.group_id"] = new { Operator = "is", Value = groups.Values.ToArray() },
            },
            Perform = new Dictionary<string, object> { ["notification.webhook"] = new { WebhookId = webhookId } },
            // Lo attiva la Fase 6, quando l'Agent pubblica /webhooks/zammad: fino ad allora le consegne fallirebbero.
            Active = trigger?.Active ?? false,
            Note = "Assistente ticket (POC): solo articoli del cliente, per evitare cicli con le scritture dell'agente"
        };

        if (trigger is null)
        {
            await zammad.CreateAsync<ZammadTrigger>(ZammadResources.Triggers, desired, cancellationToken);
            Changed($"trigger '{ZammadDirectory.TriggerName}' creato (disattivo fino alla Fase 6)");
        }
        else
        {
            await zammad.UpdateAsync<ZammadTrigger>(ZammadResources.Triggers, trigger.Id, desired, cancellationToken);
            Changed($"trigger '{ZammadDirectory.TriggerName}' aggiornato");
        }
    }

    private static bool PerformsWebhook(ZammadTrigger trigger, long webhookId) =>
        trigger.Perform.ValueKind == JsonValueKind.Object
        && trigger.Perform.TryGetProperty("notification.webhook", out JsonElement action)
        && action.TryGetProperty("webhook_id", out JsonElement id)
        && id.ToString() == webhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Dopo le migrazioni Zammad riavvia il railsserver: si aspetta che l'API cada (se succede) e che torni.</summary>
    private async Task WaitForRestartAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset downDeadline = time.GetUtcNow() + settings.RestartDownWindow;

        while (time.GetUtcNow() < downDeadline && await IsUpAsync(cancellationToken))
        {
            await Task.Delay(settings.PollInterval, time, cancellationToken);
        }

        DateTimeOffset upDeadline = time.GetUtcNow() + settings.RestartUpTimeout;

        while (!await IsUpAsync(cancellationToken))
        {
            if (time.GetUtcNow() > upDeadline)
            {
                throw new TimeoutException($"Zammad non è tornato disponibile entro {settings.RestartUpTimeout.TotalSeconds:0} s dalla migrazione dei campi.");
            }

            await Task.Delay(settings.PollInterval, time, cancellationToken);
        }
    }

    private async Task<bool> IsUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await zammad.GetCurrentUserAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is ZammadApiException or HttpRequestException)
        {
            return false;
        }
    }

    private void Changed(string message)
    {
        _report.Changed.Add(message);
        output.WriteLine($"  + {message}");
    }

    private void Present(string message)
    {
        _report.Present.Add(message);
        output.WriteLine($"  = {message}");
    }
}
