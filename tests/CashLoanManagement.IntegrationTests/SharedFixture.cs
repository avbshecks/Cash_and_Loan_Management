using Xunit;

namespace CashLoanManagement.IntegrationTests;

[CollectionDefinition("CashLoanApi")]
public class SharedFixtureCollection : ICollectionFixture<CashLoanApiFactory>
{
    // Marker class only — xUnit wires up the shared CashLoanApiFactory instance
    // (one DB drop/recreate/migrate for the whole test run, not per test class).
}
