// Protocol algorithms and field layout derived from pyBYD 0.0.79 (MIT).
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Byd.Client.Crypto;

internal static class Protocol
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    internal static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string LoginKey(string password) => Md5(Md5(password));
    internal static string MixedSha1(string text)
    {
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        var mixed = string.Concat(digest.Select((b, i) => b.ToString(i % 2 == 0 ? "X2" : "x2")));
        return string.Concat(mixed.Where((c, i) => c != '0' || i % 2 != 0));
    }
    internal static string Sign(IReadOnlyDictionary<string, string> fields, string key) =>
        MixedSha1(string.Join("&", fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) + $"&password={key}");
    internal static string CheckCode(object fields)
    {
        var md5 = Md5(Json(fields)).ToLowerInvariant();
        return md5[24..32] + md5[8..16] + md5[16..24] + md5[..8];
    }
    internal static string Encrypt(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(key);
        return Convert.ToHexString(aes.EncryptCbc(Encoding.UTF8.GetBytes(text), new byte[16], PaddingMode.PKCS7));
    }
    internal static string Decrypt(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(key);
        var bytes = aes.DecryptCbc(Convert.FromHexString(text), new byte[16], PaddingMode.PKCS7);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void Device(Dictionary<string, string> fields)
    {
        fields["ostype"] = "and"; fields["imei"] = "BANGCLE01234";
        fields["mac"] = "00:00:00:00:00:00"; fields["model"] = "POCO F1";
        fields["sdk"] = "35"; fields["mod"] = "Xiaomi";
    }
    internal static Dictionary<string, string> Login(string username, string password, BydClientOptions options, string timestamp, string random)
    {
        var inner = new Dictionary<string, string>
        {
            ["agreeStatus"] = "0",
            ["agreementType"] = "[1,2]",
            ["appInnerVersion"] = "351",
            ["appVersion"] = "3.5.1",
            ["deviceName"] = "XIAOMIPOCO F1",
            ["deviceType"] = "0",
            ["imeiMD5"] = Md5(username),
            ["isAuto"] = "1",
            ["mobileBrand"] = "XIAOMI",
            ["mobileModel"] = "POCO F1",
            ["networkType"] = "wifi",
            ["osType"] = "15",
            ["osVersion"] = "35",
            ["random"] = random,
            ["softType"] = "0",
            ["timeStamp"] = timestamp,
            ["timeZone"] = options.TimeZone
        };
        var signed = new Dictionary<string, string>(inner)
        {
            ["appName"] = "pyBYD+0.0.79",
            ["countryCode"] = options.Market.CountryCode,
            ["functionType"] = "pwdLogin",
            ["identifier"] = username,
            ["identifierType"] = "0",
            ["language"] = options.Market.Language,
            ["reqTimestamp"] = timestamp
        };
        var outer = new Dictionary<string, string>
        {
            ["appName"] = "pyBYD+0.0.79",
            ["countryCode"] = options.Market.CountryCode,
            ["encryData"] = Encrypt(Json(inner), LoginKey(password)),
            ["functionType"] = "pwdLogin",
            ["identifier"] = username,
            ["identifierType"] = "0",
            ["imeiMD5"] = Md5(username),
            ["isAuto"] = "1",
            ["language"] = options.Market.Language,
            ["reqTimestamp"] = timestamp,
            ["sign"] = Sign(signed, Md5(password)),
            ["signKey"] = password
        };
        Device(outer); outer["serviceTime"] = timestamp; outer["checkcode"] = CheckCode(outer);
        return outer;
    }
    internal static Dictionary<string, string> Inner(string deviceId, string timestamp, string random, string? vin = null, string? serial = null)
    {
        var inner = new Dictionary<string, string> { ["deviceType"] = "0", ["imeiMD5"] = deviceId, ["networkType"] = "wifi", ["random"] = random, ["timeStamp"] = timestamp, ["version"] = "351" };
        if (vin != null) inner["vin"] = vin;
        if (serial != null) inner["requestSerial"] = serial;
        return inner;
    }
    internal static Dictionary<string, string> Token(Session session, Dictionary<string, string> inner, string timestamp)
    {
        var signed = new Dictionary<string, string>(inner) { ["countryCode"] = session.CountryCode, ["identifier"] = session.UserId, ["imeiMD5"] = session.DeviceId, ["language"] = session.Language, ["reqTimestamp"] = timestamp };
        var outer = new Dictionary<string, string> { ["countryCode"] = session.CountryCode, ["encryData"] = Encrypt(Json(inner), session.ContentKey), ["identifier"] = session.UserId, ["imeiMD5"] = session.DeviceId, ["language"] = session.Language, ["reqTimestamp"] = timestamp, ["sign"] = Sign(signed, session.SignKey) };
        Device(outer); outer["serviceTime"] = timestamp; outer["checkcode"] = CheckCode(outer);
        return outer;
    }
    internal static Dictionary<string, string> Token(Session session, Dictionary<string, object?> inner, string timestamp)
    {
        // Some endpoints require JSON numbers (not numeric strings), e.g. energy requestType.
        var signed = inner.ToDictionary(p => p.Key, p => p.Value is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : p.Value?.ToString() ?? "", StringComparer.Ordinal);
        signed["countryCode"] = session.CountryCode; signed["identifier"] = session.UserId; signed["imeiMD5"] = session.DeviceId; signed["language"] = session.Language; signed["reqTimestamp"] = timestamp;
        var outer = new Dictionary<string, string> { ["countryCode"] = session.CountryCode, ["encryData"] = Encrypt(Json(inner), session.ContentKey), ["identifier"] = session.UserId, ["imeiMD5"] = session.DeviceId, ["language"] = session.Language, ["reqTimestamp"] = timestamp, ["sign"] = Sign(signed, session.SignKey) };
        Device(outer); outer["serviceTime"] = timestamp; outer["checkcode"] = CheckCode(outer); return outer;
    }
}

internal sealed record Session(string UserId, string SignKey, string ContentKey, string DeviceId, DateTimeOffset Created)
{
    internal string CountryCode { get; init; } = "AU";
    internal string Language { get; init; } = "en";
    internal string RawSignToken { get; init; } = "";
    public override string ToString() => "BYD session (redacted)";
}
