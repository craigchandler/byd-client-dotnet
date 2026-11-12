// Bootstrap and encrypted event format derived from pyBYD 0.0.79 (MIT).
using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Byd.Client.Crypto;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Byd.Client.Telemetry;

internal interface IStatusFeed : IDisposable
{
    Task<VehicleStatus?> WaitAsync(string serial, TimeSpan timeout, CancellationToken cancellation);
    Task<VehicleLocation?> WaitLocationAsync(string serial, TimeSpan timeout, CancellationToken cancellation) => Task.FromResult<VehicleLocation?>(null);
}
internal interface IStatusFeedConnector
{
    Task<IStatusFeed> ConnectAsync(string broker, Session session, string vin, CancellationToken cancellation, bool locationOnly = false);
}

internal sealed class MqttStatusFeed : IStatusFeed
{
    private readonly IMqttClient client;
    private readonly string topic, vin, key;
    private readonly bool locationOnly;
    private readonly Channel<(string Serial, VehicleLocation Location)> locations = Channel.CreateBounded<(string, VehicleLocation)>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly Channel<(string Serial, VehicleStatus Status)> readings = Channel.CreateBounded<(string, VehicleStatus)>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    internal MqttStatusFeed(IMqttClient client, string topic, string vin, string key, bool locationOnly = false)
    {
        this.client = client;
        this.topic = topic; this.vin = vin; this.key = key;
        this.locationOnly = locationOnly;
        client.ApplicationMessageReceivedAsync += args =>
        {
            Receive(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += _ => { readings.Writer.TryComplete(); locations.Writer.TryComplete(); return Task.CompletedTask; };
    }

    internal void Receive(MqttApplicationMessage message)
    {
        if (message.Topic != topic || message.Retain || message.Payload.Length is 0 or > 524288) return;
        try
        {
            if (locationOnly)
            {
                var location = DecodeLocation(message.Payload.ToArray(), key, vin, DateTimeOffset.UtcNow);
                if (location is { } fix) locations.Writer.TryWrite(fix);
                return;
            }
            var reading = Decode(message.Payload.ToArray(), key, vin, DateTimeOffset.UtcNow);
            if (reading is { } value) readings.Writer.TryWrite(value);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException or InvalidOperationException)
        { /* Unrelated or malformed pushes must not expose payloads or initiate another sign-in. */ }
    }

    internal static MqttClientOptions ConnectionOptions(string broker, Session session, long seconds)
    {
        var (host, port) = BrokerAddress(broker);
        var clientId = "oversea_" + session.DeviceId;
        var stamp = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var password = stamp + Protocol.Md5(session.RawSignToken + clientId + session.UserId + stamp);
        return new MqttClientOptionsBuilder().WithClientId(clientId)
            .WithTcpServer(host, port).WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCredentials(session.UserId, password).WithTlsOptions(tls => tls.UseTls())
            .WithCleanSession().WithSessionExpiryInterval(0).WithMaximumPacketSize(1048576)
            .WithTimeout(TimeSpan.FromSeconds(10)).WithKeepAlivePeriod(TimeSpan.FromSeconds(30)).Build();
    }

    internal static (string Host, int Port) BrokerAddress(string broker)
    {
        var address = broker.Contains("://", StringComparison.Ordinal) ? broker : "mqtts://" + broker;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("mqtts" or "ssl" or "tls" or "tcp" or "mqtt")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/")
            || Uri.CheckHostName(uri.DnsSafeHost) == UriHostNameType.Unknown || uri.IsLoopback || !string.IsNullOrEmpty(uri.Host) && uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new BydException(BydError.InvalidResponse);
        // The authenticated BYD endpoint selects the broker. TLS and host-name verification are always used, including for bare addresses.
        if (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || ip.IsIPv6Multicast || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (ip.GetAddressBytes()[0] is 0 or 10 or >= 224
                || ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254
                || ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] is >= 16 and <= 31
                || ip.GetAddressBytes()[0] == 192 && ip.GetAddressBytes()[1] == 168)))
            throw new BydException(BydError.InvalidResponse);
        var port = uri.Port == -1 ? 8883 : uri.Port;
        if (port is < 1 or > 65535) throw new BydException(BydError.InvalidResponse);
        return (uri.DnsSafeHost, port);
    }

    internal static (string Serial, VehicleStatus Status)? Decode(byte[] encrypted, string key, string vin, DateTimeOffset now)
    {
        var text = Encoding.ASCII.GetString(encrypted);
        var envelope = JsonNode.Parse(Protocol.Decrypt(string.Concat(text.Where(c => !char.IsWhiteSpace(c))), key));
        if (envelope is not JsonObject || envelope["event"]?.ToString() != "vehicleInfo" || envelope["vin"]?.ToString() != vin) return null;
        var data = envelope["data"];
        if (data is not JsonObject || data["code"] is { } code && code.ToString() != "0" || data["respondData"] is not JsonObject values) return null;
        var serial = data["uuid"]?.ToString();
        if (string.IsNullOrWhiteSpace(serial) || values["requestSerial"] is { } echoed && echoed.ToString() != serial) return null;
        var status = VehicleStatus.Parse(values, now);
        return status.HasUsableData ? (serial, status) : null;
    }

    internal static (string Serial, VehicleLocation Location)? DecodeLocation(byte[] encrypted, string key, string vin, DateTimeOffset now)
    {
        var text = Encoding.ASCII.GetString(encrypted);
        var envelope = JsonNode.Parse(Protocol.Decrypt(string.Concat(text.Where(c => !char.IsWhiteSpace(c))), key));
        if (envelope is not JsonObject || envelope["vin"]?.ToString() != vin || string.IsNullOrEmpty(envelope["event"]?.ToString())) return null;
        var data = envelope["data"];
        if (data is not JsonObject || data["code"] is { } code && code.ToString() != "0" || data["respondData"] is not JsonObject values) return null;
        var serial = data["uuid"]?.ToString();
        if (string.IsNullOrWhiteSpace(serial) || values["requestSerial"] is { } echoed && echoed.ToString() != serial) return null;
        var location = VehicleLocation.Parse(values, now);
        return location == null ? null : (serial, location);
    }

    internal static async Task<IStatusFeed> Connect(string broker, Session session, string vin, CancellationToken cancellation, bool locationOnly = false)
    {
        var options = ConnectionOptions(broker, session, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var client = new MqttClientFactory().CreateMqttClient();
        var topic = "oversea/res/" + session.UserId;
        var feed = new MqttStatusFeed(client, topic, vin, session.ContentKey, locationOnly);
        try
        {
            var connection = await client.ConnectAsync(options, cancellation).ConfigureAwait(false);
            if (connection.ResultCode != MqttClientConnectResultCode.Success) throw new BydException(BydError.LiveTelemetryUnavailable);
            var result = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), cancellation).ConfigureAwait(false);
            if (result.Items.Count != 1 || result.Items.First().ResultCode > MqttClientSubscribeResultCode.GrantedQoS2) throw new BydException(BydError.LiveTelemetryUnavailable);
            return feed;
        }
        catch { feed.Dispose(); throw new BydException(BydError.LiveTelemetryUnavailable); }
    }

    public async Task<VehicleStatus?> WaitAsync(string serial, TimeSpan timeout, CancellationToken cancellation)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(timeout);
        try
        {
            while (await readings.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                while (readings.Reader.TryRead(out var reading))
                    if (reading.Serial == serial) return reading.Status;
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
    }

    public async Task<VehicleLocation?> WaitLocationAsync(string serial, TimeSpan timeout, CancellationToken cancellation)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        wait.CancelAfter(timeout);
        try
        {
            while (await locations.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                while (locations.Reader.TryRead(out var location))
                    if (location.Serial == serial) return location.Location;
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
    }

    public void Dispose()
    {
        client.Dispose(); readings.Writer.TryComplete(); locations.Writer.TryComplete();
        while (readings.Reader.TryRead(out _)) { }
        while (locations.Reader.TryRead(out _)) { }
    }
}

internal sealed class MqttStatusFeedConnector : IStatusFeedConnector
{
    internal static readonly MqttStatusFeedConnector Instance = new();
    public Task<IStatusFeed> ConnectAsync(string broker, Session session, string vin, CancellationToken cancellation, bool locationOnly = false) => MqttStatusFeed.Connect(broker, session, vin, cancellation, locationOnly);
}
