using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Xunit;

namespace Byd.Client.Tests;

public class ProtocolTests
{
    private static readonly JsonNode Oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/pybyd-0.0.79.json")))!;
    [Fact]
    public void Bangcle_matches_python_in_both_directions()
    {
        var codec = new Bangcle();
        foreach (var row in Oracle["envelopes"]!.AsArray())
        {
            var text = row!["text"]!.ToString(); var encoded = row["encoded"]!.ToString();
            Assert.Equal(encoded, codec.Encode(text)); Assert.Equal(text, codec.Decode(encoded));
        }
    }
    [Fact]
    public void Hashes_and_inner_aes_match_python()
    {
        foreach (var row in Oracle["hashes"]!.AsArray())
        {
            var text = row!["text"]!.ToString();
            Assert.Equal(row["md5"]!.ToString(), Protocol.Md5(text));
            Assert.Equal(row["loginKey"]!.ToString(), Protocol.LoginKey(text));
            Assert.Equal(row["sha1"]!.ToString(), Protocol.MixedSha1(text));
        }
        foreach (var row in Oracle["aes"]!.AsArray())
        {
            var text = row!["text"]!.ToString(); var key = row["key"]!.ToString(); var encrypted = row["encrypted"]!.ToString();
            Assert.Equal(encrypted, Protocol.Encrypt(text, key)); Assert.Equal(text, Protocol.Decrypt(encrypted, key));
        }
    }
    [Fact]
    public void Login_and_authenticated_request_fields_match_python()
    {
        const string timestamp = "1700000000000", random = "0123456789ABCDEF0123456789ABCDEF";
        var login = Protocol.Login("synthetic@example.invalid", "Synthetic-Only-Password", new(), timestamp, random);
        Assert.True(JsonNode.DeepEquals(Oracle["login"], JsonNode.Parse(Protocol.Json(login))));
        var session = new Session("synthetic-user", Protocol.Md5("synthetic-sign"), Protocol.Md5("synthetic-encryption"), Protocol.Md5("synthetic@example.invalid"), DateTimeOffset.UtcNow);
        var inner = Protocol.Inner(session.DeviceId, timestamp, random, "SYNTHETIC000000001");
        Assert.True(JsonNode.DeepEquals(Oracle["tokenInner"], JsonNode.Parse(Protocol.Json(inner))));
        Assert.True(JsonNode.DeepEquals(Oracle["token"], JsonNode.Parse(Protocol.Json(Protocol.Token(session, inner, timestamp)))));
        Assert.True(JsonNode.DeepEquals(Oracle["climate"], JsonNode.Parse(BydClient.ClimateParameters(21, 10))));
    }
    [Fact]
    public void Unknown_data_is_not_displayed_as_zero_or_locked()
    {
        var status = VehicleStatus.Parse(JsonNode.Parse("{\"elecPercent\":-1,\"enduranceMileage\":-1,\"chargingState\":15,\"leftFrontDoorLock\":2}")!, DateTimeOffset.UtcNow);
        Assert.Null(status.BatteryPercent); Assert.Null(status.RangeKm); Assert.Equal("Unknown", status.Locks); Assert.Equal("Unknown", status.Charging); Assert.True(status.IsStale(DateTimeOffset.UtcNow));
        var emptyBattery = VehicleStatus.Parse(JsonNode.Parse("{\"elecPercent\":0,\"enduranceMileage\":0}")!, DateTimeOffset.UtcNow);
        Assert.Equal(0, emptyBattery.BatteryPercent); Assert.Equal(0, emptyBattery.RangeKm);
    }
    [Theory]
    [InlineData("{\"res\":1}", CommandOutcome.Unknown)]
    [InlineData("{\"res\":2}", CommandOutcome.Confirmed)]
    [InlineData("{\"controlState\":2}", CommandOutcome.Failed)]
    [InlineData("{\"success\":true}", CommandOutcome.Unknown)]
    [InlineData("{}", CommandOutcome.Unknown)]
    public void Accepted_is_not_confirmed(string json, CommandOutcome expected) => Assert.Equal(expected, BydClient.ParseOutcome(JsonNode.Parse(json)!));
}
