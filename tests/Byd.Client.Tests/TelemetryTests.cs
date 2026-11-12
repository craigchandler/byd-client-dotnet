using System.Text;
using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Byd.Client.Telemetry;
using MQTTnet;
using Xunit;

namespace Byd.Client.Tests;

public class TelemetryTests
{
    [Theory]
    [InlineData(100, 1, 0, "Charging")]
    [InlineData(100, 0, 1, "Not charging")]
    [InlineData(100, 15, 0, "Not charging")]
    [InlineData(80, 15, 1, "Not charging")]
    [InlineData(100, null, 15, "Unknown")]
    [InlineData(100, -1, 0, "Unknown")]
    [InlineData(100, null, 0, "Unknown")]
    public void Full_battery_does_not_determine_charging_and_only_the_authoritative_field_is_used(int battery, int? primary, int secondary, string expected)
    {
        var payload = new JsonObject { ["elecPercent"] = battery, ["chargingState"] = secondary };
        if (primary != null) payload["chargeState"] = primary.Value;
        var status = VehicleStatus.Parse(payload, DateTimeOffset.UtcNow);
        Assert.Equal(expected, status.Charging);
    }
    [Fact]
    public void Dedicated_charging_read_can_report_idle_without_claiming_a_connected_cable()
    {
        var reading = ChargingSnapshot.Parse(JsonNode.Parse("{\"soc\":100,\"chargingState\":15,\"connectState\":-1}")!);
        Assert.False(reading.Charging); Assert.Null(reading.Connected);
        Assert.Null(ChargingSnapshot.Parse(JsonNode.Parse("{\"soc\":100}")!).Charging);
    }
    private const string Vin = "SYNTHETIC000000001";
    private static readonly JsonNode Oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/pybyd-0.0.79.json")))!["mqtt"]!;
    [Fact]
    public void Mqtt_authentication_and_decryption_match_independent_python_vectors()
    {
        var session = new Session("synthetic-user", "", Oracle["key"]!.ToString(), Protocol.Md5("synthetic@example.invalid"), DateTimeOffset.UtcNow) { RawSignToken = "synthetic-sign" };
        var options = MqttStatusFeed.ConnectionOptions("broker.example.invalid:8883", session, 1700000000);
        Assert.Equal(Oracle["clientId"]!.ToString(), options.ClientId);
        Assert.Equal(Oracle["password"]!.ToString(), Encoding.UTF8.GetString(options.Credentials.GetPassword(options)));
        Assert.Equal("synthetic-user", options.Credentials.GetUserName(options));
        var tcp = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        Assert.True(tcp.TlsOptions.UseTls); Assert.False(tcp.TlsOptions.AllowUntrustedCertificates);
        Assert.False(tcp.TlsOptions.IgnoreCertificateChainErrors); Assert.False(tcp.TlsOptions.IgnoreCertificateRevocationErrors);
        Assert.Null(tcp.TlsOptions.CertificateValidationHandler);
        var reading = MqttStatusFeed.Decode(Encoding.ASCII.GetBytes(Oracle["encrypted"]!.ToString()), Oracle["key"]!.ToString(), Vin, DateTimeOffset.UtcNow);
        Assert.NotNull(reading); Assert.Equal("status-1", reading.Value.Serial);
        Assert.Equal(78, reading.Value.Status.BatteryPercent); Assert.Equal(325, reading.Value.Status.RangeKm);
        Assert.Equal("Not charging", reading.Value.Status.Charging); Assert.Equal("Locked", reading.Value.Status.Locks);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), reading.Value.Status.VehicleTime);
        Assert.DoesNotContain("synthetic-sign", session.ToString());
    }
    [Theory]
    [InlineData("wrong-vehicle")]
    [InlineData("wrong-event")]
    [InlineData("missing-serial")]
    [InlineData("conflicting-serial")]
    [InlineData("rejected")]
    [InlineData("placeholder")]
    public void Unrelated_or_placeholder_push_cannot_become_vehicle_status(string variant)
    {
        var envelope = Oracle["event"]!.DeepClone(); var data = envelope["data"]!;
        switch (variant)
        {
            case "wrong-vehicle": envelope["vin"] = "OTHER-SYNTHETIC"; break;
            case "wrong-event": envelope["event"] = "gpsInfo"; break;
            case "missing-serial": data.AsObject().Remove("uuid"); break;
            case "conflicting-serial": data["respondData"]!["requestSerial"] = "other-request"; break;
            case "rejected": data["code"] = "6051"; break;
            case "placeholder": data["respondData"] = JsonNode.Parse("""{"elecPercent":0,"enduranceMileage":0,"chargingState":0}"""); break;
        }
        var encoded = Protocol.Encrypt(envelope.ToJsonString(), Oracle["key"]!.ToString());
        Assert.Null(MqttStatusFeed.Decode(Encoding.ASCII.GetBytes(encoded), Oracle["key"]!.ToString(), Vin, DateTimeOffset.UtcNow));
    }
    [Fact]
    public void Placeholder_detection_preserves_zero_when_other_vehicle_fields_support_it()
    {
        var placeholder = VehicleStatus.Parse(JsonNode.Parse("""{"elecPercent":0,"enduranceMileage":0,"chargingState":0}""")!, DateTimeOffset.UtcNow);
        Assert.False(placeholder.HasUsableData); Assert.Equal("Unknown", placeholder.Charging);
        var emptyBattery = VehicleStatus.Parse(JsonNode.Parse("""{"elecPercent":0,"enduranceMileage":0,"leftFrontDoorLock":1,"chargeState":0}""")!, DateTimeOffset.UtcNow);
        Assert.True(emptyBattery.HasUsableData); Assert.Equal(0, emptyBattery.BatteryPercent); Assert.Equal(0, emptyBattery.RangeKm);
        var offline = emptyBattery with { Online = false }; Assert.False(offline.HasUsableData);
    }
    [Theory]
    [InlineData("http://broker.example.invalid:8883")]
    [InlineData("mqtts://user:secret@broker.example.invalid")]
    [InlineData("mqtts://broker.example.invalid/path")]
    [InlineData("127.0.0.1:8883")]
    [InlineData("192.168.1.1:8883")]
    public void Invalid_broker_addresses_fail_without_echoing_address(string address)
    {
        var error = Assert.Throws<BydException>(() => MqttStatusFeed.BrokerAddress(address));
        Assert.DoesNotContain(address, error.ToString());
    }
    [Fact]
    public void Alternative_valid_fields_and_iso_timestamps_are_used_when_primary_is_unavailable()
    {
        var status = VehicleStatus.Parse(JsonNode.Parse("""{"elecPercent":-1,"powerBattery":78,"enduranceMileage":-1,"enduranceMileageV2":325,"timestamp":"2026-11-11T01:30:00Z","chargeState":1,"chargingState":0}""")!, DateTimeOffset.UtcNow);
        Assert.Equal(78, status.BatteryPercent); Assert.Equal(325, status.RangeKm); Assert.Equal("Charging", status.Charging);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T01:30:00Z"), status.VehicleTime);
    }

    [Fact]
    public async Task Receiver_rejects_wrong_topic_retained_and_wrong_serial_then_accepts_matching_push()
    {
        var topic = "oversea/res/synthetic-user";
        using var receiver = new MqttStatusFeed(new MqttClientFactory().CreateMqttClient(), topic, Vin, Oracle["key"]!.ToString());
        MqttApplicationMessage Message(string forTopic, bool retained = false, string serial = "status-1")
        {
            var envelope = Oracle["event"]!.DeepClone(); envelope["data"]!["uuid"] = serial;
            return new MqttApplicationMessageBuilder().WithTopic(forTopic).WithRetainFlag(retained)
                .WithPayload(Protocol.Encrypt(envelope.ToJsonString(), Oracle["key"]!.ToString())).Build();
        }
        receiver.Receive(Message("oversea/res/other-account"));
        receiver.Receive(Message(topic, true));
        receiver.Receive(Message(topic, serial: "earlier-request"));
        receiver.Receive(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("not valid encrypted data").Build());
        Assert.Null(await receiver.WaitAsync("status-1", TimeSpan.FromMilliseconds(10), CancellationToken.None));
        receiver.Receive(Message(topic));
        Assert.Equal(78, (await receiver.WaitAsync("status-1", TimeSpan.FromSeconds(1), CancellationToken.None))!.BatteryPercent);
    }
    [Fact]
    public async Task Receiver_cancellation_and_disposal_finish_without_background_retries()
    {
        using var receiver = new MqttStatusFeed(new MqttClientFactory().CreateMqttClient(), "oversea/res/synthetic-user", Vin, Oracle["key"]!.ToString());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.WaitAsync("status-1", TimeSpan.FromSeconds(1), cancelled.Token));
        receiver.Dispose();
        Assert.Null(await receiver.WaitAsync("status-1", TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
