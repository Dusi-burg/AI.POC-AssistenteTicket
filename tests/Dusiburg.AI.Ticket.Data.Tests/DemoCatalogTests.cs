using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Tests;

/// <summary>
/// Invarianti del catalogo demo: se i valori cambiano, ciò che ogni scenario deve mostrare (PLAN §6.4) deve restare vero.
/// </summary>
public class DemoCatalogTests
{
    [Test]
    public void Catalog_KeysAreUniqueAndReferencesResolve()
    {
        //SUT
        IReadOnlyList<DemoProduct> products = DemoCatalog.Products;

        Assert.That(products.Select(p => p.Code), Is.Unique);
        Assert.That(products, Has.All.Matches<DemoProduct>(p => p.Versions.Select(v => v.Version).Distinct().Count() == p.Versions.Count));
        Assert.That(products, Has.All.Matches<DemoProduct>(p => p.Versions.Count is >= 4 and <= 5), "PLAN §6.1: 4–5 versioni per prodotto");
        Assert.That(DemoCatalog.ErrorCodes.Select(e => e.Code), Is.Unique);
        Assert.That(DemoCatalog.Customers.Select(c => c.EmailDomain), Is.Unique);
        Assert.That(DemoCatalog.Scenarios.Select(s => s.Number), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
        Assert.That(DemoCatalog.Scenarios.Select(s => s.CustomerEmailDomain).Distinct(), Is.SubsetOf(DemoCatalog.Customers.Select(c => c.EmailDomain)));
        Assert.That(DemoCatalog.Scenarios.Select(s => s.ErrorCode).OfType<string>().Distinct(), Is.SubsetOf(DemoCatalog.ErrorCodes.Select(e => e.Code)));
    }

    [Test]
    public void Catalog_VersionsAndErrorCodesBelongToTheirProduct()
    {
        //SUT
        IReadOnlyList<DemoCustomer> customers = DemoCatalog.Customers;

        Assert.That(customers.Where(c => c.Version is not null),
            Has.All.Matches<DemoCustomer>(c => DemoCatalog.Product(c.ProductCode).Versions.Any(v => v.Version == c.Version)));
        Assert.That(DemoCatalog.ErrorCodes, Has.All.Matches<DemoErrorCode>(e => e.Code.StartsWith(e.ProductCode + "-E", StringComparison.Ordinal)));
        Assert.That(DemoCatalog.Products, Has.All.Matches<DemoProduct>(p => IsChronological(p.Versions)));
    }

    [Test]
    public void Catalog_SaasCustomersAreOnTheLatestVersion()
    {
        //SUT
        IEnumerable<DemoCustomer> saas = DemoCatalog.Customers.Where(c => c.DeploymentMode == DeploymentMode.Saas);

        Assert.That(saas, Is.Not.Empty);
        Assert.That(saas, Has.All.Matches<DemoCustomer>(c => c.Version == DemoCatalog.Product(c.ProductCode).Versions[^1].Version));
    }

    [Test]
    public void Catalog_UsesOnlyFictitiousDomains()
    {
        //SUT
        IReadOnlyList<DemoCustomer> customers = DemoCatalog.Customers;

        Assert.That(customers.Select(c => c.EmailDomain), Has.All.EndsWith(".example.it"));
    }

    [Test]
    public void Scenarios1And2_SameErrorOnTheSameProductWithDifferentModes()
    {
        //SUT
        DemoScenario onPrem = Scenario(1);
        DemoScenario saas = Scenario(2);

        Assert.That(onPrem.ErrorCode, Is.Not.Null.And.EqualTo(saas.ErrorCode));
        Assert.That(CustomerOf(onPrem).ProductCode, Is.EqualTo(CustomerOf(saas).ProductCode));
        Assert.That((CustomerOf(onPrem).DeploymentMode, CustomerOf(saas).DeploymentMode), Is.EqualTo((DeploymentMode.OnPrem, DeploymentMode.Saas)));
        Assert.That(DemoCatalog.ErrorCode(onPrem.ErrorCode!).AffectedVersion, Is.Null, "errore d'ambiente, non un bug di versione");
    }

    [Test]
    public void Scenario3_VagueTicketWithoutVersionOrErrorCode()
    {
        //SUT
        DemoScenario scenario = Scenario(3);

        Assert.That(scenario.ErrorCode, Is.Null);
        Assert.That(CustomerOf(scenario).Version, Is.Null);
    }

    [Test]
    public void Scenario4_CustomerOnTheVersionWithTheKnownBugFixedLater()
    {
        //SUT
        DemoScenario scenario = Scenario(4);
        DemoCustomer customer = CustomerOf(scenario);
        DemoErrorCode error = DemoCatalog.ErrorCode(scenario.ErrorCode!);
        DemoProduct product = DemoCatalog.Product(customer.ProductCode);

        Assert.That(error.ProductCode, Is.EqualTo(customer.ProductCode));
        Assert.That(customer.Version, Is.EqualTo(error.AffectedVersion));
        Assert.That(product.SequenceOf(error.FixedInVersion!), Is.GreaterThan(product.SequenceOf(error.AffectedVersion!)));
        Assert.That(customer.DeploymentMode, Is.EqualTo(DeploymentMode.OnPrem), "l'aggiornamento lo fa il cliente");
    }

    [Test]
    public void Scenarios5And6_HaveNoErrorCode()
    {
        //SUT
        DemoScenario noHistory = Scenario(5);
        DemoScenario injection = Scenario(6);

        Assert.That((noHistory.ErrorCode, injection.ErrorCode), Is.EqualTo(((string?)null, (string?)null)));
        Assert.That(CustomerOf(noHistory).Version, Is.Not.Null);
    }

    [Test]
    public void SequenceOf_FirstVersionIsOneAndUnknownVersionThrows()
    {
        //SETUP
        DemoProduct product = DemoCatalog.Product(DemoCatalog.FattureFlow);

        //SUT
        Assert.That(product.SequenceOf("4.8.5"), Is.EqualTo(1));
        Assert.That(() => product.SequenceOf("9.9.9"), Throws.ArgumentException);
    }

    private static DemoScenario Scenario(int number) => DemoCatalog.Scenarios.Single(s => s.Number == number);

    private static DemoCustomer CustomerOf(DemoScenario scenario) => DemoCatalog.Customer(scenario.CustomerEmailDomain);

    private static bool IsChronological(IReadOnlyList<DemoProductVersion> versions) =>
        versions.Zip(versions.Skip(1)).All(pair => pair.First.ReleasedOn < pair.Second.ReleasedOn);
}
