using System.Text.Json;
using System.Text.RegularExpressions;

namespace Byd.Client;

/// <summary>An immutable, allow-listed international account country and its regional server.</summary>
public sealed class BydMarket
{
    /// <summary>Human-readable name from the bundled catalog.</summary>
    public string Name { get; }
    /// <summary>ISO-style two-letter account-country code used by this protocol.</summary>
    public string CountryCode { get; }
    /// <summary>Protocol language code from the bundled catalog.</summary>
    public string Language { get; }
    /// <summary>Human-readable regional server name from the bundled catalog.</summary>
    public string ServerRegion { get; }
    /// <summary>Allow-listed BYD HTTPS endpoint. This property cannot be supplied by consumers.</summary>
    public string ApiBaseUrl { get; }
    internal BydMarket(string name, string countryCode, string language, string serverRegion, string apiBaseUrl)
    { Name = name; CountryCode = countryCode; Language = language; ServerRegion = serverRegion; ApiBaseUrl = apiBaseUrl; }
    /// <inheritdoc/>
    public override string ToString() => Name == ServerRegion ? Name : $"{Name} · {ServerRegion}";
}
/// <summary>Bundled international account-country catalog; listing entries does not probe or verify servers.</summary>
public static class BydMarkets
{
    /// <summary>Immutable bundled country catalog; listing entries never makes network requests.</summary>
    public static IReadOnlyList<BydMarket> All { get; } = Load();
    /// <summary>Selects a bundled account country, ignoring case and outer whitespace. Throws ArgumentException for unsupported input; never guesses or probes a server.</summary>
    public static BydMarket ForCountry(string code) => All.FirstOrDefault(m => string.Equals(m.CountryCode, code?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Choose a supported international BYD account country.", nameof(code));
    private static IReadOnlyList<BydMarket> Load()
    {
        using var stream = typeof(BydMarkets).Assembly.GetManifestResourceStream("Byd.Client.markets.json") ?? throw new InvalidOperationException("Region catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        return Array.AsReadOnly(document.RootElement.EnumerateArray().Select(n =>
        {
            var url = n.GetProperty("apiBaseUrl").GetString()!; var uri = new Uri(url);
            if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/" || !Regex.IsMatch(uri.Host, @"^dilinkappoversea-[a-z]{2}\.byd\.auto$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Invalid regional endpoint in the bundled catalog.");
            return new BydMarket(n.GetProperty("name").GetString()!, n.GetProperty("countryCode").GetString()!, n.GetProperty("language").GetString()!, n.GetProperty("serverRegion").GetString()!, url);
        }).ToArray());
    }
}
