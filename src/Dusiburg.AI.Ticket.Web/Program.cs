using System.Text.Encodings.Web;
using System.Text.Unicode;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Agent tramite service discovery (AppHost: WithReference(agent)); l'indirizzo si può sovrascrivere, ad esempio nei test.
builder.Services.AddHttpClient<AgentApiClient>((services, client) =>
    client.BaseAddress = new Uri(
        services.GetRequiredService<IConfiguration>()[AgentApiClient.BaseAddressConfigurationKey] ?? AgentApiClient.DefaultBaseAddress));

// Link verso Zammad e verso il dashboard di Aspire, passati dall'AppHost.
builder.Services.Configure<ConsoleLinks>(links =>
{
    links.ZammadUrl = builder.Configuration[ConsoleLinks.ZammadUrlKey];
    links.DashboardUrl = builder.Configuration[ConsoleLinks.DashboardUrlKey];
});

// Testi in italiano: nelle pagine le lettere accentate restano leggibili invece di diventare entità numeriche.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();

app.UseAuthorization();

app.MapDefaultEndpoints();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

/// <summary>Tipo di riferimento dell'assembly per <c>WebApplicationFactory</c> nei test.</summary>
public sealed class WebEntryPoint;
