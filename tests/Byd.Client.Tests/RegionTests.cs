using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    public static IEnumerable<object[]> AllMarkets => BydMarkets.All.Select(m => new object[] { m.CountryCode });
    [Theory]
    [MemberData(nameof(AllMarkets))]
    public async Task Every_listed_market_uses_its_own_endpoint_and_signed_country_and_language(string country)
    {
        var market = BydMarkets.ForCountry(country); var server = new Server { ExpectedMarket = market };
        server.ReplyJson["/vehicleInfo/vehicle/getEnergyConsumption"] = "{}";
        using var client = new BydClient(new BydClientOptions { Market = market, EnablePushTelemetry = false }, server);
        await Connect(client); await client.GetEnergyAsync(Vin);
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/vehicleInfo/vehicle/getEnergyConsumption" }, server.Paths);
    }
    [Fact]
    public void Region_catalog_is_complete_allow_listed_and_does_not_guess_unknown_countries()
    {
        Assert.Equal(101, BydMarkets.All.Count); Assert.Equal(16, BydMarkets.All.Select(m => m.ApiBaseUrl).Distinct().Count());
        Assert.Equal("Australia", BydMarkets.ForCountry(" nz ").ServerRegion);
        Assert.Equal("Europe", BydMarkets.ForCountry("NO").ServerRegion);
        Assert.Equal("Middle East/Africa", BydMarkets.ForCountry("AE").ServerRegion);
        Assert.Equal("Singapore/APAC", BydMarkets.ForCountry("PH").ServerRegion);
        Assert.Throws<ArgumentException>(() => BydMarkets.ForCountry("CN")); Assert.Throws<ArgumentException>(() => BydMarkets.ForCountry("https://example.invalid"));
        Assert.Empty(typeof(BydMarket).GetConstructors()); // Public callers cannot provide an arbitrary credential destination.
    }
}
