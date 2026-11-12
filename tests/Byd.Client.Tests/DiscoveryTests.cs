using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    [Fact]
    public void Region_and_resource_discovery_need_no_client_or_sign_in_and_have_stable_unique_ids()
    {
        var regions = BydDiscovery.GetRegions(); var resources = BydDiscovery.GetResources();
        Assert.Equal(16, regions.Count); Assert.Equal(101, regions.Sum(r => r.Countries.Count)); Assert.Equal(regions.Count, regions.Select(r => r.Id).Distinct().Count());
        Assert.Equal(9, resources.Count); Assert.Equal(resources.Count, resources.Select(r => r.Id).Distinct().Count());
        Assert.Contains(regions, r => r.Id == "au" && r.Countries.Any(c => c.CountryCode == "AU") && r.Countries.Any(c => c.CountryCode == "NZ"));
        Assert.All(regions, r => { Assert.Equal(resources, r.ListableResources); Assert.All(r.Countries, c => Assert.Equal(r.ApiBaseUrl, c.ApiBaseUrl)); });
        Assert.All(resources, r => Assert.True(r.RequiresSignIn));
        Assert.False(Assert.Single(resources, r => r.Resource == BydResource.Vehicles).RequiresVehicleSelection);
        Assert.True(Assert.Single(resources, r => r.Resource == BydResource.Energy).RequiresVehicleSelection);
        Assert.DoesNotContain(resources, r => r.ClientMethod.Contains("Execute", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Vehicle_discovery_reads_configuration_only_and_does_not_claim_unreported_data_is_absent()
    {
        var server = AdvancedServer(); using var client = Make(server); await Connect(client);
        var catalog = await client.GetVehicleFeaturesAsync(Vin);
        Assert.Equal("/vehicle/vehicleswitch/getLatestConfig", server.Paths.Last()); Assert.Equal(3, server.Paths.Count);
        Assert.Equal(BydFeatureAvailability.Unknown, Assert.Single(catalog.Features, f => f.Resource == BydResource.Details).Availability);
        Assert.Equal(BydFeatureAvailability.NotReported, Assert.Single(catalog.Features, f => f.Resource == BydResource.Energy).Availability);
        Assert.Equal(BydFeatureAvailability.ReportedAvailable, Assert.Single(catalog.Features, f => f.Resource == BydResource.Charging).Availability);
        Assert.Equal(BydFeatureAvailability.NotReported, Assert.Single(catalog.Features, f => f.Command == VehicleCommand.VentWindows).Availability);
        var controls = catalog.Features.Where(f => f.Kind == BydFeatureKind.Control).ToArray(); Assert.NotEmpty(controls);
        Assert.All(controls, f => { Assert.True(f.RequiresSignIn); Assert.True(f.RequiresControlOptIn); Assert.True(f.RequiresConfirmation); Assert.True(f.RequiresPin); });
        Assert.Contains(controls, f => f.Seat == SeatFeature.DriverHeating); Assert.DoesNotContain(controls, f => f.Id.Contains("Find", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("verifyControlPassword") || p == "/control/remoteControl");
    }
}
