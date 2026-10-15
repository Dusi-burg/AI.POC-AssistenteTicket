namespace Dusiburg.AI.Ticket.Zammad.Api;

/// <summary>Utente di Zammad come lo restituisce <c>GET /api/v1/users/me?expand=true</c>: con <c>expand</c> i ruoli sono nomi.</summary>
public sealed record ZammadUser(long Id, string Login, string? Firstname, string? Lastname, IReadOnlyList<string> Roles)
{
    public const string AdminRole = "Admin";

    public const string AgentRole = "Agent";

    public bool IsAdmin => Roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase);
}
