using System.Text.Json.Nodes;

namespace Byd.Client;

/// <summary>One reported GPS fix, retained in memory only. It is not continuous tracking.</summary>
/// <param name="Latitude">Latitude in decimal degrees. Sensitive location data; do not log or persist implicitly.</param>
/// <param name="Longitude">Longitude in decimal degrees. Sensitive location data; do not log or persist implicitly.</param>
/// <param name="RetrievedAt">UTC-compatible instant when the client retrieved/created the snapshot; not the vehicle measurement time.</param>
/// <param name="FixTime">Provider GPS timestamp, or null. Inspect IsStale before displaying a fix as current.</param>
public sealed record VehicleLocation(double Latitude, double Longitude, DateTimeOffset RetrievedAt, DateTimeOffset? FixTime)
{
    /// <summary>Returns true when the vehicle/fix timestamp is missing, over thirty minutes old or over five minutes in the future.</summary>
    public bool IsStale(DateTimeOffset now) => FixTime is null || now - FixTime > TimeSpan.FromMinutes(30) || FixTime > now + TimeSpan.FromMinutes(5);
    /// <inheritdoc/>
    public override string ToString() => "Vehicle location (coordinates redacted)";
    internal static VehicleLocation? Parse(JsonNode node, DateTimeOffset now)
    {
        var values = node["data"] is JsonObject nested ? nested : node;
        var latitude = VehicleStatus.Number(values, "latitude");
        var longitude = VehicleStatus.Number(values, "longitude");
        if (latitude is not { } lat || longitude is not { } lon || lat is < -90 or > 90 || lon is < -180 or > 180 || Math.Abs(lat) < 0.1 && Math.Abs(lon) < 0.1) return null;
        var time = VehicleStatus.Timestamp(values, "gpsTimeStamp") ?? VehicleStatus.Timestamp(values, "timestamp")
            ?? VehicleStatus.Timestamp(node, "gpsTimeStamp");
        return new(lat, lon, now, time);
    }
}
