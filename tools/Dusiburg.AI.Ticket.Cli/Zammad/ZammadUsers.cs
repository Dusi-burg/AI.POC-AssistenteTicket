using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Cli.Zammad;

/// <summary>
/// Utenti di Zammad letti una volta e creati quando mancano: i referenti dei clienti (ruolo Customer, nell'organizzazione del
/// dominio) e gli operatori creati da <c>zammad bootstrap</c>.
/// </summary>
internal sealed class ZammadUsers
{
    private readonly ZammadClient _zammad;
    private readonly Dictionary<string, long> _byEmail;
    private readonly Dictionary<string, long> _organizationsByDomain;
    private readonly long _customerRole;

    private ZammadUsers(ZammadClient zammad, Dictionary<string, long> byEmail, Dictionary<string, long> organizationsByDomain, long customerRole)
    {
        _zammad = zammad;
        _byEmail = byEmail;
        _organizationsByDomain = organizationsByDomain;
        _customerRole = customerRole;
    }

    public int CustomersCreated { get; private set; }

    public static async Task<ZammadUsers> LoadAsync(ZammadClient zammad, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZammadUserRecord> users = await zammad.ListAllAsync<ZammadUserRecord>(ZammadResources.Users, cancellationToken);
        IReadOnlyList<ZammadOrganization> organizations = await zammad.ListAllAsync<ZammadOrganization>(ZammadResources.Organizations, cancellationToken);
        long customerRole = (await zammad.ListAllAsync<ZammadRole>(ZammadResources.Roles, cancellationToken)).Single(r => r.Name == ZammadDirectory.CustomerRole).Id;

        return new ZammadUsers(
            zammad,
            users.Where(u => !string.IsNullOrEmpty(u.Email)).ToDictionary(u => u.Email!.ToLowerInvariant(), u => u.Id),
            organizations.Where(o => !string.IsNullOrEmpty(o.Domain)).ToDictionary(o => o.Domain!.ToLowerInvariant(), o => o.Id),
            customerRole);
    }

    /// <summary>Operatore creato dal bootstrap; se manca, il seed non può attribuirgli le risposte.</summary>
    public long OperatorId(string fullName) =>
        _byEmail.TryGetValue(ZammadDirectory.OperatorEmail(fullName), out long id)
            ? id
            : throw new InvalidOperationException($"Operatore '{fullName}' assente in Zammad: lanciare prima 'zammad bootstrap'.");

    public long? Find(string email) => _byEmail.TryGetValue(email.ToLowerInvariant(), out long id) ? id : null;

    public async Task<long> EnsureCustomerAsync(string fullName, string email, string domain, CancellationToken cancellationToken)
    {
        if (Find(email) is { } existing)
        {
            return existing;
        }

        (string first, string last) = ZammadDirectory.SplitName(fullName);
        ZammadUserRecord created = await _zammad.CreateAsync<ZammadUserRecord>(ZammadResources.Users, new
        {
            Login = email,
            Email = email,
            Firstname = first,
            Lastname = last,
            OrganizationId = _organizationsByDomain.TryGetValue(domain.ToLowerInvariant(), out long organization) ? organization : (long?)null,
            RoleIds = new[] { _customerRole },
            Active = true
        }, cancellationToken);

        _byEmail[email.ToLowerInvariant()] = created.Id;
        CustomersCreated++;

        return created.Id;
    }
}
