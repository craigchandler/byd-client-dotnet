using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Xunit;

namespace Byd.Client.Tests;

public class AdvancedReferenceTests
{
    private static JsonNode Oracle => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pybyd-0.0.79.json")))!;
    [Fact]
    public void All_sixteen_regions_match_independent_python_login_and_token_signing()
    {
        const string timestamp = "1700000000000", random = "0123456789ABCDEF0123456789ABCDEF";
        var regions = Oracle["regions"]!.AsArray(); Assert.Equal(16, regions.Count);
        foreach (var row in regions)
        {
            var market = BydMarkets.ForCountry(row!["countryCode"]!.ToString());
            var options = new BydClientOptions { Market = market, TimeZone = "UTC" };
            var session = new Session("synthetic-user", Protocol.Md5("synthetic-sign"), Protocol.Md5("synthetic-encryption"), Protocol.Md5("synthetic@example.invalid"), DateTimeOffset.UtcNow) { CountryCode = market.CountryCode, Language = market.Language };
            var inner = Protocol.Inner(session.DeviceId, timestamp, random, "SYNTHETIC000000001");
            Assert.True(JsonNode.DeepEquals(row["login"], JsonNode.Parse(Protocol.Json(Protocol.Login("synthetic@example.invalid", "Synthetic-Only-Password", options, timestamp, random)))), market.CountryCode);
            Assert.True(JsonNode.DeepEquals(row["token"], JsonNode.Parse(Protocol.Json(Protocol.Token(session, inner, timestamp)))), market.CountryCode);
            var energy = inner.ToDictionary(p => p.Key, p => (object?)p.Value); energy["powerType"] = "0"; energy["requestType"] = 0; energy["autoModelNameOut"] = "Synthetic Atto 3";
            Assert.True(JsonNode.DeepEquals(row["energyToken"], JsonNode.Parse(Protocol.Json(Protocol.Token(session, energy, timestamp)))), market.CountryCode);
        }
    }
    [Fact]
    public void Numeric_energy_request_matches_independent_python_encryption_and_signature()
    {
        const string timestamp = "1700000000000", random = "0123456789ABCDEF0123456789ABCDEF";
        var session = new Session("synthetic-user", Protocol.Md5("synthetic-sign"), Protocol.Md5("synthetic-encryption"), Protocol.Md5("synthetic@example.invalid"), DateTimeOffset.UtcNow);
        var inner = Protocol.Inner(session.DeviceId, timestamp, random, "SYNTHETIC000000001").ToDictionary(p => p.Key, p => (object?)p.Value);
        inner["powerType"] = "0"; inner["requestType"] = 0; inner["autoModelNameOut"] = "Synthetic Atto 3";
        Assert.True(JsonNode.DeepEquals(Oracle["energyTokenInner"], JsonNode.Parse(Protocol.Json(inner))));
        Assert.True(JsonNode.DeepEquals(Oracle["energyToken"], JsonNode.Parse(Protocol.Json(Protocol.Token(session, inner, timestamp)))));
    }
    [Fact]
    public void Climate_booking_payload_matches_python_with_64_bit_booking_identity()
    {
        var settings = new CommandSettings { TemperatureC = 22, DurationMinutes = 20, BookingOperation = BookingOperation.Update, BookingId = 1234567890123456789L, Departure = DateTimeOffset.FromUnixTimeSeconds(1900000000) };
        Assert.True(JsonNode.DeepEquals(Oracle["climateBooking"], JsonNode.Parse(BydClient.ClimateBookingParameters(settings))));
        Assert.True(JsonNode.DeepEquals(Oracle["climateBookingRemove"], JsonNode.Parse(BydClient.ClimateBookingParameters(new() { BookingOperation = BookingOperation.Remove, BookingId = settings.BookingId }))));
    }
    [Fact]
    public void Seat_payload_matches_python_preserving_existing_levels_and_steering_heat()
    {
        var state = ClimateSnapshot.Parse(JsonNode.Parse("{\"mainSeatHeatState\":1,\"mainSeatVentilationState\":3,\"copilotSeatHeatState\":2,\"copilotSeatVentilationState\":1,\"steeringWheelHeatState\":-1}")!);
        var capabilities = new VehicleCapabilities(["10030001", "10030002", "10030004", "10030005", "10030010"], false);
        Assert.True(JsonNode.DeepEquals(Oracle["seatClimate"], JsonNode.Parse(BydClient.SeatParameters(new() { Seat = SeatFeature.DriverHeating, Level = ComfortLevel.High }, state, capabilities))));
    }
}
