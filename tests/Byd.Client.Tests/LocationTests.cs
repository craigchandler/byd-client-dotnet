using System.Text;
using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Byd.Client.Telemetry;
using MQTTnet;
using Xunit;

namespace Byd.Client.Tests;

public class LocationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"latitude\":0,\"longitude\":0}")]
    [InlineData("{\"latitude\":91,\"longitude\":151}")]
    [InlineData("{\"latitude\":-33,\"longitude\":181}")]
    [InlineData("{\"latitude\":-33}")]
    public void Invalid_or_placeholder_coordinates_do_not_become_a_marker(string json) => Assert.Null(VehicleLocation.Parse(JsonNode.Parse(json)!, DateTimeOffset.UtcNow));

    [Fact]
    public void Nested_string_coordinates_and_fix_timestamp_are_parsed_and_age_is_honest()
    {
        var now = DateTimeOffset.Parse("2026-11-11T02:00:00Z");
        var fix = VehicleLocation.Parse(JsonNode.Parse("""{"data":{"latitude":"-33.8568","longitude":"151.2153","gpsTimeStamp":"2026-11-11T01:59:00Z"}}""")!, now)!;
        Assert.Equal(-33.8568, fix.Latitude); Assert.Equal(151.2153, fix.Longitude); Assert.False(fix.IsStale(now));
        Assert.True((fix with { FixTime = null }).IsStale(now)); Assert.True(fix.IsStale(now.AddHours(1)));
        Assert.True((fix with { FixTime = now.AddHours(1) }).IsStale(now));
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Encrypted_location_push_requires_selected_vehicle_and_consistent_serial(bool wrongVehicle, bool wrongSerial, bool rejected)
    {
        const string key = "0123456789ABCDEF0123456789ABCDEF", vin = "SYNTHETIC000000001";
        var envelope = new
        {
            @event = "gpsInfo",
            vin = wrongVehicle ? "OTHER-SYNTHETIC" : vin,
            data = new { code = "0", uuid = "gps-1", respondData = new { requestSerial = wrongSerial ? "wrong" : "gps-1", data = new { latitude = -33.8568, longitude = 151.2153 } } }
        };
        var encoded = Encoding.ASCII.GetBytes(Protocol.Encrypt(Protocol.Json(envelope), key));
        var location = MqttStatusFeed.DecodeLocation(encoded, key, vin, DateTimeOffset.UtcNow);
        if (rejected) Assert.Null(location); else { Assert.NotNull(location); Assert.Equal("gps-1", location.Value.Serial); Assert.Equal(-33.8568, location.Value.Location.Latitude); }
        Assert.Null(MqttStatusFeed.Decode(encoded, key, vin, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task Location_subscription_does_not_route_coordinates_into_the_status_channel()
    {
        const string key = "0123456789ABCDEF0123456789ABCDEF", vin = "SYNTHETIC000000001", topic = "oversea/res/synthetic-user";
        using var receiver = new MqttStatusFeed(new MqttClientFactory().CreateMqttClient(), topic, vin, key, locationOnly: true);
        var envelope = new { @event = "gpsInfo", vin, data = new { uuid = "gps-1", respondData = new { latitude = -33.8568, longitude = 151.2153 } } };
        receiver.Receive(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(Protocol.Encrypt(Protocol.Json(envelope), key)).Build());
        Assert.Null(await receiver.WaitAsync("gps-1", TimeSpan.FromMilliseconds(10), CancellationToken.None));
        var location = await receiver.WaitLocationAsync("gps-1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(location); Assert.Equal(-33.8568, location.Latitude);
        receiver.Dispose(); Assert.Null(await receiver.WaitLocationAsync("gps-1", TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
