using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Shared.Demo;

namespace Dusiburg.AI.Ticket.Cli.Tests;

/// <summary>Nomi derivati (email, gruppi) e scrittura degli user-secrets senza perdere le altre chiavi.</summary>
public class ZammadDirectoryTests
{
    [Test]
    public void PersonEmail_NameWithAccentsAndApostrophes_IsPlainAscii()
    {
        //SUT
        Assert.That(ZammadDirectory.OperatorEmail("Marta Ferrari"), Is.EqualTo("marta.ferrari@dusiburg-software.example.it"));
        Assert.That(ZammadDirectory.PersonEmail("Niccolò D'Angelo", "cliente.example.it"), Is.EqualTo("niccolo.dangelo@cliente.example.it"));
    }

    [Test]
    public void GroupFor_EachProduct_HasItsGroup()
    {
        //SUT
        Assert.That(ZammadDirectory.GroupFor(DemoCatalog.FattureFlow), Is.EqualTo(ZammadDirectory.InvoicingGroup));
        Assert.That(ZammadDirectory.GroupFor(DemoCatalog.DocuArchive), Is.EqualTo(ZammadDirectory.DocumentsGroup));
    }

    [Test]
    public void UserSecretsStore_Set_KeepsTheOtherKeys()
    {
        //SETUP
        string path = Path.Combine(Path.GetTempPath(), $"ticket-secrets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "ConnectionStrings:sql": "Server=x", "AppHost:OtlpApiKey": "k" }""");
        var store = new UserSecretsStore(path);

        try
        {
            //SUT
            store.Set(new Dictionary<string, string> { [UserSecretsStore.ApiTokenKey] = "nuovo" });

            Assert.That((store.Get("ConnectionStrings:sql"), store.Get("AppHost:OtlpApiKey"), store.Get(UserSecretsStore.ApiTokenKey)),
                Is.EqualTo(("Server=x", "k", "nuovo")));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void UserSecretsStore_MissingFile_ReadsNothingAndCreatesItOnWrite()
    {
        //SETUP
        string path = Path.Combine(Path.GetTempPath(), $"ticket-secrets-{Guid.NewGuid():N}", "secrets.json");
        var store = new UserSecretsStore(path);

        try
        {
            //SUT
            Assert.That(store.Get(UserSecretsStore.ApiTokenKey), Is.Null);
            store.Set(new Dictionary<string, string> { [UserSecretsStore.WebhookSecretKey] = "s" });

            Assert.That(store.Get(UserSecretsStore.WebhookSecretKey), Is.EqualTo("s"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
