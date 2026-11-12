using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Byd.Client.Crypto;
using Byd.Client.Telemetry;
using Xunit;

namespace Byd.Client.Tests;

public partial class ClientSafetyTests
{
    private const string Vin = "SYNTHETIC000000001", Password = "Synthetic-Only-Password";
    private sealed class Server : HttpMessageHandler
    {
        private readonly Bangcle codec = new();
        internal BydMarket ExpectedMarket = BydMarkets.ForCountry("AU");
        internal readonly List<string> Paths = [];
        internal readonly List<JsonNode> InnerRequests = [];
        internal bool LoseCommand, WrongSerial, RejectLogin, EmptyStatus, Expire;
        internal JsonNode? TriggerStatus;
        internal readonly Queue<JsonNode> PolledStatuses = [];
        internal bool CurrentStatusTime;
        internal Func<CancellationToken, Task>? OnPoll;
        internal Func<string, CancellationToken, Task>? OnRequest;
        internal string? LoginReplyJson;
        internal bool GpsUnavailable;
        internal readonly Queue<JsonNode> GpsResponses = [];
        internal readonly Dictionary<string, string> ReplyJson = [];
        internal readonly Dictionary<string, string> ReplyCode = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal(new Uri(ExpectedMarket.ApiBaseUrl).Host, request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            var path = request.RequestUri.AbsolutePath; Paths.Add(path);
            if (OnRequest is not null) await OnRequest(path, cancellation);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;
            var envelope = JsonNode.Parse(codec.Decode(body["request"]!.ToString()))!;
            Assert.Equal(ExpectedMarket.CountryCode, envelope["countryCode"]!.ToString());
            Assert.Equal(ExpectedMarket.Language, envelope["language"]!.ToString());
            if (path == "/app/account/login")
            {
                if (LoginReplyJson is not null) return DataText(LoginReplyJson, Protocol.LoginKey(Password));
                if (RejectLogin) return Wrapped(new { code = "9", message = Password + " synthetic@example.invalid private-token" });
                Assert.Equal(Password, envelope["signKey"]!.ToString());
                return Data(new { token = new { userId = "synthetic-user", signToken = "synthetic-sign", encryToken = "synthetic-encryption" } }, Protocol.LoginKey(Password));
            }
            if (Expire) return Wrapped(new { code = "1005", message = "private-token" });
            var inner = JsonNode.Parse(Protocol.Decrypt(envelope["encryData"]!.ToString(), Protocol.Md5("synthetic-encryption")))!;
            InnerRequests.Add(inner);
            if (ReplyCode.TryGetValue(path, out var code)) return Wrapped(new { code, message = Password + " private-token " + Vin });
            if (ReplyJson.TryGetValue(path, out var json)) return DataText(json);
            if (path.EndsWith("vehicleRealTimeResult") && OnPoll != null) await OnPoll(cancellation);
            return path switch
            {
                "/app/account/getAllListByUserId" => Data(new[] { new { vin = Vin, modelName = "Synthetic Atto 3", energyType = "0" } }),
                "/app/emqAuth/getEmqBrokerIp" => Data(new { emqBorker = "mqtt.synthetic.example.invalid:8883" }),
                "/control/getGpsInfo" => Data(new { requestSerial = "gps-1" }),
                "/control/getGpsInfoResult" => GpsUnavailable ? Wrapped(new { code = "6051", message = "private-gps-diagnostic" }) : GpsResponses.TryDequeue(out var gps) ? Data(gps) : Data(new { requestSerial = "gps-1", data = new { latitude = -33.8568, longitude = 151.2153, gpsTimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } }),
                "/vehicleInfo/vehicle/vehicleRealTimeRequest" => Data((object?)TriggerStatus ?? new { requestSerial = "status-1" }),
                "/vehicleInfo/vehicle/vehicleRealTimeResult" => PolledStatuses.TryDequeue(out var polled) ? Data(polled) : EmptyStatus ? Data(new { requestSerial = "status-1" }) : Data(new { requestSerial = "status-1", elecPercent = 0, enduranceMileage = 0, time = CurrentStatusTime ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 1700000000000, onlineState = 1, leftFrontDoorLock = 2, rightFrontDoorLock = 2, leftRearDoorLock = 2, rightRearDoorLock = 2 }),
                "/vehicle/vehicleswitch/getLatestConfig" => Data(new Dictionary<string, object> { { Vin, new { cfFixedList = new[] { new { functionNo = "1005" } } } } }),
                "/vehicle/vehicleswitch/verifyControlPassword" => Data(new { result = 0 }),
                "/control/remoteControl" => LoseCommand ? throw new HttpRequestException("private-token " + Password) : Data(new { requestSerial = "command-1", res = 1 }),
                "/control/remoteControlResult" => Data(new { requestSerial = WrongSerial ? "wrong-command" : "command-1", res = 2 }),
                _ => throw new InvalidOperationException("Unexpected test endpoint")
            };
        }
        private HttpResponseMessage Data(object value, string? key = null) => DataText(Protocol.Json(value), key);
        private HttpResponseMessage DataText(string json, string? key = null) => Wrapped(new { code = "0", respondData = Protocol.Encrypt(json, key ?? Protocol.Md5("synthetic-encryption")) });
        private HttpResponseMessage Wrapped(object value) => new(HttpStatusCode.OK) { Content = new StringContent(Protocol.Json(new { response = codec.Encode(Protocol.Json(value)) }), Encoding.UTF8, "application/json") };
    }
    private static BydClient Make(Server server, bool controls = false) => new(new BydClientOptions { EnableRemoteControls = controls, PollAttempts = 2, PollInterval = TimeSpan.Zero }, server);
    private static async Task Connect(BydClient client) { await client.LoginAsync("synthetic@example.invalid", Password); await client.GetVehiclesAsync(); }

    [Fact]
    public async Task Read_only_flow_never_calls_control_or_pin_endpoints()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        var status = await client.GetStatusAsync(Vin);
        Assert.Equal(0, status.BatteryPercent);
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/vehicleInfo/vehicle/vehicleRealTimeRequest", "/vehicleInfo/vehicle/vehicleRealTimeResult" }, server.Paths);
        Assert.Equal(BydError.ControlsDisabled, Assert.Throws<BydException>(() => client.ConfirmCommand(Vin, VehicleCommand.Lock)).Error);
    }
    [Fact]
    public async Task A_command_needs_opt_in_and_consumes_confirmation_once()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        var approval = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(approval, "123456"));
        var count = server.Paths.Count;
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456"))).Error);
        Assert.Equal(count, server.Paths.Count);
        Assert.Equal(1, server.Paths.Count(x => x == "/control/remoteControl"));
        Assert.DoesNotContain(server.InnerRequests, n => n.ToJsonString().Contains("123456"));
    }
    [Fact]
    public async Task Lost_reply_never_replays_and_cannot_leak_raw_exception()
    {
        var server = new Server { LoseCommand = true }; using var client = Make(server, true); await Connect(client);
        var error = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(BydError.OutcomeUnknown, error.Error); Assert.Null(error.InnerException);
        Assert.DoesNotContain(Password, error.ToString()); Assert.DoesNotContain("private-token", error.ToString());
        Assert.Equal(1, server.Paths.Count(x => x == "/control/remoteControl"));
    }
    [Fact]
    public async Task Wrong_serial_cannot_confirm_a_command()
    {
        var server = new Server { WrongSerial = true }; using var client = Make(server, true); await Connect(client);
        Assert.Equal(CommandOutcome.Unknown, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(1, server.Paths.Count(x => x == "/control/remoteControl"));
    }
    [Theory]
    [InlineData("{\"res\":2}", CommandOutcome.Confirmed)]
    [InlineData("{\"controlState\":1}", CommandOutcome.Confirmed)]
    [InlineData("{\"res\":3}", CommandOutcome.Failed)]
    [InlineData("{\"res\":1}", CommandOutcome.Unknown)]
    [InlineData("{\"res\":2,\"requestSerial\":\"different-command\"}", CommandOutcome.Unknown)]
    [InlineData("{\"res\":2,\"requestSerial\":\"command-1\",\"vin\":\"SYNTHETIC000000002\"}", CommandOutcome.Unknown)]
    [InlineData("{\"res\":2,\"requestSerial\":\"command-1\",\"commandType\":\"OPENDOOR\"}", CommandOutcome.Unknown)]
    public async Task Http_result_lookup_binds_an_omitted_serial_but_rejects_conflicting_identity(string reply, CommandOutcome expected)
    {
        // Pinned pyBYD _fetch_control_endpoint falls back to the requested serial for HTTP result replies.
        // This does not apply to MQTT messages or a trigger reply with no serial/terminal result.
        var server = new Server(); server.ReplyJson["/control/remoteControlResult"] = reply;
        using var client = Make(server, true); await Connect(client);
        Assert.Equal(expected, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
        var polls = server.InnerRequests.Where(n => n["commandType"]?.ToString() == "LOCKDOOR" && n["commandPwd"]?.ToString() == "").ToArray();
        Assert.NotEmpty(polls);
        Assert.All(polls, p => { Assert.Equal("command-1", p["requestSerial"]?.ToString()); Assert.Equal(Vin, p["vin"]?.ToString()); });
    }
    [Fact]
    public async Task Pending_trigger_without_serial_cannot_borrow_a_serial_or_poll_another_command()
    {
        var server = new Server(); server.ReplyJson["/control/remoteControl"] = "{\"res\":1}";
        using var client = Make(server, true); await Connect(client);
        Assert.Equal(CommandOutcome.Unknown, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
        Assert.DoesNotContain("/control/remoteControlResult", server.Paths);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("{}")]
    public async Task Successful_empty_pin_reply_is_valid_and_setup_check_cannot_dispatch(string reply)
    {
        // pyBYD _api/_common.py decode_respond_data accepts decrypted empty/whitespace as {}.
        var server = new Server(); server.ReplyJson["/vehicle/vehicleswitch/verifyControlPassword"] = reply;
        using var client = Make(server, true); await Connect(client);
        await client.CheckCommandAccessAsync(Vin, VehicleCommand.Lock, "123456");
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/vehicle/vehicleswitch/getLatestConfig", "/vehicle/vehicleswitch/verifyControlPassword" }, server.Paths);
        var verify = server.InnerRequests.Last();
        Assert.Equal(Protocol.Md5("123456"), verify["commandPwd"]!.ToString());
        Assert.Equal("remoteControl", verify["functionType"]!.ToString());
        Assert.DoesNotContain("123456", verify.ToJsonString());
        Assert.Null(verify["commandType"]);
    }
    [Theory]
    [InlineData("5005")]
    [InlineData("5006")]
    [InlineData("5011")]
    public async Task Rejected_pin_is_identified_and_cannot_dispatch(string code)
    {
        var server = new Server(); server.ReplyCode["/vehicle/vehicleswitch/verifyControlPassword"] = code;
        using var client = Make(server, true); await Connect(client);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(BydError.PinRejected, e.Error); Assert.Equal(CommandStage.PinVerification, e.CommandStage); Assert.Equal(code, e.ApiCode);
        Assert.Contains("No vehicle command was sent", e.Message);
        Assert.DoesNotContain("/control/remoteControl", server.Paths);
        Assert.DoesNotContain(Password, e.ToString()); Assert.DoesNotContain(Vin, e.ToString()); Assert.DoesNotContain("private-token", e.ToString()); Assert.Null(e.InnerException);
    }
    [Theory]
    [InlineData("{\"ok\":false}", BydError.PinRejected)]
    [InlineData("{\"ok\":\"false\"}", BydError.InvalidResponse)]
    [InlineData("[]", BydError.InvalidResponse)]
    [InlineData("null", BydError.InvalidResponse)]
    [InlineData("not-json-private-token", BydError.InvalidResponse)]
    public async Task Negative_or_malformed_pin_reply_fails_closed(string reply, BydError expected)
    {
        var server = new Server(); server.ReplyJson["/vehicle/vehicleswitch/verifyControlPassword"] = reply;
        using var client = Make(server, true); await Connect(client);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(expected, e.Error); Assert.Equal(CommandStage.PinVerification, e.CommandStage);
        Assert.Contains("No vehicle command was sent", e.Message); Assert.DoesNotContain("private-token", e.ToString());
        Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SYNTHETIC000000001\":{\"cfFixedList\":[null]}}")]
    public async Task Malformed_capabilities_report_the_stage_and_never_verify_pin_or_dispatch(string reply)
    {
        var server = new Server(); server.ReplyJson["/vehicle/vehicleswitch/getLatestConfig"] = reply;
        using var client = Make(server, true); await Connect(client);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(BydError.InvalidResponse, e.Error); Assert.Equal(CommandStage.CapabilityCheck, e.CommandStage);
        Assert.Contains("No vehicle command was sent", e.Message);
        Assert.DoesNotContain("/vehicle/vehicleswitch/verifyControlPassword", server.Paths); Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }
    [Fact]
    public async Task Nested_capabilities_are_read_without_bypassing_unsupported_gate()
    {
        var server = new Server(); server.ReplyJson["/vehicle/vehicleswitch/getLatestConfig"] = "{\"SYNTHETIC000000001\":{\"cfFixedList\":[{\"functionNo\":\"1002\",\"cfFixedSecondLevelList\":[{\"functionNo\":\"1005\"}]}]}}";
        using var client = Make(server, true); await Connect(client);
        await client.CheckCommandAccessAsync(Vin, VehicleCommand.Lock, "123456");
        var e = await Assert.ThrowsAsync<BydException>(() => client.CheckCommandAccessAsync(Vin, VehicleCommand.Unlock, "123456"));
        Assert.Equal(BydError.Unsupported, e.Error); Assert.Equal(CommandStage.CapabilityCheck, e.CommandStage);
        Assert.Equal(1, server.Paths.Count(p => p.EndsWith("verifyControlPassword"))); Assert.DoesNotContain("/control/remoteControl", server.Paths);
    }
    [Theory]
    [InlineData("/control/remoteControl", "[]", CommandStage.Dispatch)]
    [InlineData("/control/remoteControl", "not-json-private-token", CommandStage.Dispatch)]
    [InlineData("/control/remoteControlResult", "[]", CommandStage.Result)]
    [InlineData("/control/remoteControlResult", "null", CommandStage.Result)]
    public async Task Malformed_reply_after_dispatch_is_unknown_never_not_sent_and_never_replayed(string endpoint, string reply, CommandStage stage)
    {
        var server = new Server(); server.ReplyJson[endpoint] = reply;
        using var client = Make(server, true); await Connect(client);
        var approval = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        var e = await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456"));
        Assert.Equal(BydError.OutcomeUnknown, e.Error); Assert.Equal(stage, e.CommandStage);
        Assert.DoesNotContain("No vehicle command was sent", e.Message); Assert.DoesNotContain("private-token", e.ToString()); Assert.Null(e.InnerException);
        Assert.Equal(1, server.Paths.Count(p => p == "/control/remoteControl"));
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(approval, "123456"))).Error);
    }
    [Fact]
    public async Task Setup_check_needs_opt_in_and_never_creates_a_command_approval()
    {
        var server = new Server(); using var readOnly = Make(server); await Connect(readOnly);
        Assert.Equal(BydError.ControlsDisabled, (await Assert.ThrowsAsync<BydException>(() => readOnly.CheckCommandAccessAsync(Vin, VehicleCommand.Lock, "123456"))).Error);
        Assert.Equal(2, server.Paths.Count);
        var enabled = new Server(); using var client = Make(enabled, true); await Connect(client);
        await client.CheckCommandAccessAsync(Vin, VehicleCommand.Lock, "123456");
        Assert.DoesNotContain("/control/remoteControl", enabled.Paths);
        Assert.Equal(CommandOutcome.Confirmed, await client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"));
        Assert.Equal(2, enabled.Paths.Count(p => p.EndsWith("verifyControlPassword")));
        Assert.Equal(1, enabled.Paths.Count(p => p == "/control/remoteControl"));
    }
    [Fact]
    public async Task Invalid_pin_and_other_clients_confirmation_cannot_send()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        var before = server.Paths.Count;
        Assert.Equal(BydError.InvalidPin, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "bad"))).Error);
        Assert.Equal(before, server.Paths.Count);
        var other = new Server(); using var otherClient = Make(other, true); await Connect(otherClient);
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => otherClient.ExecuteAsync(client.ConfirmCommand(Vin, VehicleCommand.Lock), "123456"))).Error);
        Assert.Equal(2, other.Paths.Count);
    }
    [Fact]
    public async Task Expired_session_needs_user_sign_in_and_no_replay()
    {
        var server = new Server(); using var client = Make(server); await Connect(client); server.Expire = true;
        Assert.Equal(BydError.AuthenticationRequired, (await Assert.ThrowsAsync<BydException>(() => client.GetStatusAsync(Vin))).Error);
        Assert.Equal(1, server.Paths.Count(x => x == "/app/account/login"));
    }
    [Fact]
    public async Task Signing_in_again_invalidates_previous_confirmations()
    {
        var server = new Server(); using var client = Make(server, true); await Connect(client);
        var old = client.ConfirmCommand(Vin, VehicleCommand.Lock);
        await Connect(client);
        var count = server.Paths.Count;
        Assert.Equal(BydError.ApprovalRequired, (await Assert.ThrowsAsync<BydException>(() => client.ExecuteAsync(old, "123456"))).Error);
        Assert.Equal(count, server.Paths.Count);
    }
    [Fact]
    public async Task Empty_status_and_login_error_do_not_fabricate_data_or_expose_secrets()
    {
        var server = new Server { RejectLogin = true }; using var client = Make(server);
        var error = await Assert.ThrowsAsync<BydException>(() => Connect(client));
        Assert.DoesNotContain(Password, error.ToString()); Assert.DoesNotContain("private-token", error.ToString());
        server.RejectLogin = false; server.EmptyStatus = true; await Connect(client);
        Assert.Equal(BydError.NoData, (await Assert.ThrowsAsync<BydException>(() => client.GetStatusAsync(Vin))).Error);
    }

    private sealed class FakeFeed : IStatusFeed, IStatusFeedConnector
    {
        internal VehicleStatus? Reading;
        internal bool Connected, Disposed, FailConnection;
        internal string? RequestedSerial;
        internal Func<CancellationToken, Task<VehicleStatus?>>? OnWait;
        internal VehicleLocation? Location;
        internal bool LocationOnly;
        public Task<IStatusFeed> ConnectAsync(string broker, Session session, string vin, CancellationToken cancellation, bool locationOnly = false)
        {
            Assert.Equal("mqtt.synthetic.example.invalid:8883", broker);
            Assert.Equal("synthetic-sign", session.RawSignToken);
            Assert.Equal(Vin, vin);
            if (FailConnection) throw new InvalidOperationException("private-token");
            LocationOnly = locationOnly;
            Connected = true; return Task.FromResult<IStatusFeed>(this);
        }
        public Task<VehicleStatus?> WaitAsync(string serial, TimeSpan timeout, CancellationToken cancellation)
        { Assert.True(Connected); RequestedSerial = serial; return OnWait?.Invoke(cancellation) ?? Task.FromResult(Reading); }
        public void Dispose() => Disposed = true;
        public Task<VehicleLocation?> WaitLocationAsync(string serial, TimeSpan timeout, CancellationToken cancellation)
        { Assert.True(LocationOnly); RequestedSerial = serial; return Task.FromResult(Location); }
    }
    private static JsonNode Placeholder() => JsonNode.Parse("""{"requestSerial":"status-1","onlineState":1,"elecPercent":0,"enduranceMileage":0,"chargingState":0,"leftFrontDoorLock":0,"rightFrontDoorLock":0,"leftRearDoorLock":0,"rightRearDoorLock":0,"time":0}""")!;

    [Fact]
    public async Task Push_is_connected_before_trigger_and_zero_template_cannot_hide_real_readings()
    {
        var server = new Server { TriggerStatus = Placeholder() };
        var feed = new FakeFeed { Reading = new(78, 325, "Not charging", "Locked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true) };
        using var client = new BydClient(new(), server, feed); await Connect(client);
        var status = await client.GetStatusAsync(Vin);
        Assert.Equal(78, status.BatteryPercent); Assert.Equal("status-1", feed.RequestedSerial); Assert.True(feed.Disposed);
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/app/emqAuth/getEmqBrokerIp", "/vehicleInfo/vehicle/vehicleRealTimeRequest" }, server.Paths);
    }
    [Fact]
    public async Task Zero_templates_never_become_success_and_status_trigger_is_not_repeated()
    {
        var server = new Server { TriggerStatus = Placeholder() };
        server.PolledStatuses.Enqueue(Placeholder()); server.PolledStatuses.Enqueue(Placeholder());
        using var client = Make(server); await Connect(client);
        Assert.Equal(BydError.PlaceholderData, (await Assert.ThrowsAsync<BydException>(() => client.GetStatusAsync(Vin))).Error);
        Assert.Equal(1, server.Paths.Count(p => p.EndsWith("vehicleRealTimeRequest")));
        Assert.Equal(2, server.Paths.Count(p => p.EndsWith("vehicleRealTimeResult")));
    }
    [Fact]
    public async Task Failed_push_can_fall_back_to_real_http_data_and_never_exposes_transport_error()
    {
        var server = new Server(); var feed = new FakeFeed { FailConnection = true };
        using var client = new BydClient(new() { PollAttempts = 2, PollInterval = TimeSpan.Zero }, server, feed); await Connect(client);
        Assert.Equal("Locked", (await client.GetStatusAsync(Vin)).Locks);
        server.PolledStatuses.Enqueue(Placeholder()); server.PolledStatuses.Enqueue(Placeholder());
        var error = await Assert.ThrowsAsync<BydException>(() => client.GetStatusAsync(Vin));
        Assert.Equal(BydError.LiveTelemetryUnavailable, error.Error); Assert.DoesNotContain("private-token", error.ToString());
    }

    [Fact]
    public async Task Fresh_http_does_not_wait_for_push_timeout_and_cancels_the_unused_reader()
    {
        var server = new Server { CurrentStatusTime = true };
        var cancelled = false;
        var feed = new FakeFeed { OnWait = async token => { try { await Task.Delay(Timeout.Infinite, token); return null; } finally { cancelled = true; } } };
        using var client = new BydClient(new() { PollInterval = TimeSpan.Zero }, server, feed); await Connect(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var status = await client.GetStatusAsync(Vin, deadline.Token);
        Assert.Equal(TelemetrySource.Http, status.Source); Assert.True(cancelled); Assert.True(feed.Disposed);
        Assert.Equal(1, server.Paths.Count(p => p.EndsWith("vehicleRealTimeRequest")));
        Assert.Equal(1, server.Paths.Count(p => p.EndsWith("vehicleRealTimeResult")));
    }
    [Fact]
    public async Task Push_can_finish_while_http_is_in_flight_and_cancels_that_poll_without_resending()
    {
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var server = new Server { OnPoll = async token => { polling.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { cancelled = true; } } };
        var feed = new FakeFeed { OnWait = async token => { await polling.Task.WaitAsync(token); return new(78, 325, "Not charging", "Locked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true); } };
        using var client = new BydClient(new() { PollInterval = TimeSpan.Zero }, server, feed); await Connect(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var status = await client.GetStatusAsync(Vin, deadline.Token);
        Assert.Equal(TelemetrySource.LivePush, status.Source); Assert.Equal(78, status.BatteryPercent);
        Assert.True(cancelled); Assert.True(feed.Disposed);
        Assert.Equal(1, server.Paths.Count(p => p.EndsWith("vehicleRealTimeRequest")));
    }
    [Fact]
    public async Task Old_http_snapshot_cannot_outrun_the_matching_live_push()
    {
        var polledTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        var server = new Server { OnPoll = _ => { if (++polls == 2) polledTwice.TrySetResult(); return Task.CompletedTask; } };
        var feed = new FakeFeed { OnWait = async token => { await polledTwice.Task.WaitAsync(token); return new(78, 325, "Not charging", "Locked", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true); } };
        using var client = new BydClient(new() { PollInterval = TimeSpan.Zero, PollAttempts = 2 }, server, feed); await Connect(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var status = await client.GetStatusAsync(Vin, deadline.Token);
        Assert.Equal(TelemetrySource.LivePush, status.Source); Assert.Equal(78, status.BatteryPercent);
        Assert.True(feed.Disposed); Assert.Equal(2, polls);
    }
    [Fact]
    public async Task Location_is_read_only_and_does_not_need_control_opt_in_or_a_pin()
    {
        var server = new Server(); using var client = Make(server); await Connect(client);
        var location = await client.GetLocationAsync(Vin);
        Assert.Equal(-33.8568, location.Latitude); Assert.Equal(151.2153, location.Longitude);
        Assert.Equal(new[] { "/app/account/login", "/app/account/getAllListByUserId", "/control/getGpsInfo", "/control/getGpsInfoResult" }, server.Paths);
        Assert.DoesNotContain("-33.8568", location.ToString()); Assert.DoesNotContain("151.2153", location.ToString());
    }
    [Fact]
    public async Task Location_rejects_wrong_serials_and_null_island_without_repeating_trigger()
    {
        var server = new Server();
        server.GpsResponses.Enqueue(JsonNode.Parse("""{"requestSerial":"gps-1","data":{"requestSerial":"wrong","latitude":-33.8568,"longitude":151.2153}}""")!);
        server.GpsResponses.Enqueue(JsonNode.Parse("""{"requestSerial":"gps-1","data":{"latitude":0,"longitude":0}}""")!);
        using var client = Make(server); await Connect(client);
        Assert.Equal(BydError.LocationUnavailable, (await Assert.ThrowsAsync<BydException>(() => client.GetLocationAsync(Vin))).Error);
        Assert.Equal(1, server.Paths.Count(p => p == "/control/getGpsInfo"));
    }
    [Fact]
    public async Task Missing_gps_fix_is_a_safe_error_and_does_not_start_authentication_or_control_retries()
    {
        var server = new Server { GpsUnavailable = true }; using var client = Make(server); await Connect(client);
        var error = await Assert.ThrowsAsync<BydException>(() => client.GetLocationAsync(Vin));
        Assert.Equal(BydError.LocationUnavailable, error.Error); Assert.DoesNotContain("private-gps-diagnostic", error.ToString());
        Assert.Equal(1, server.Paths.Count(p => p == "/app/account/login")); Assert.Equal(1, server.Paths.Count(p => p == "/control/getGpsInfoResult"));
    }
    [Fact]
    public async Task Location_push_uses_its_own_scoped_reader_and_disconnects_after_one_fix()
    {
        var server = new Server();
        var feed = new FakeFeed { Location = new(-33.8568, 151.2153, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        using var client = new BydClient(new(), server, feed); await Connect(client);
        var location = await client.GetLocationAsync(Vin);
        Assert.Equal(151.2153, location.Longitude); Assert.Equal("gps-1", feed.RequestedSerial);
        Assert.True(feed.LocationOnly); Assert.True(feed.Disposed);
        Assert.DoesNotContain("/control/getGpsInfoResult", server.Paths);
    }
}
