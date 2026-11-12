using Byd.Client;

if (args is not ["--live"])
{
    foreach (var region in BydDiscovery.GetRegions())
        Console.WriteLine($"{region.Name}: {region.Countries.Count} account countries");
    Console.WriteLine("Offline catalog only. Use --live to sign in locally and request one vehicle status.");
    return;
}

if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("Use an interactive terminal for local sign-in; do not pipe credentials.");
    return;
}
Console.Write("BYD account country (e.g. AU): ");
var market = BydMarkets.ForCountry(Console.ReadLine() ?? "");
Console.Write("IANA timezone (e.g. Australia/Adelaide): ");
var zone = Console.ReadLine();
Console.Write("BYD email: ");
var email = Console.ReadLine() ?? "";
Console.Write("BYD password: ");
var password = "";
while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
{
    if (key.Key == ConsoleKey.Backspace)
        password = password.Length == 0 ? "" : password[..^1];
    else if (!char.IsControl(key.KeyChar)) password += key.KeyChar;
}
Console.WriteLine();
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
using var client = new BydClient(new()
{
    Market = market,
    TimeZone = string.IsNullOrWhiteSpace(zone) ? "UTC" : zone,
    EnableRemoteControls = false
});
try
{
    await client.LoginAsync(email, password, stop.Token);
    password = ""; // Drops this reference; managed strings cannot be securely erased.
    var vehicles = await client.GetVehiclesAsync(stop.Token);
    if (vehicles.Count == 0) { Console.WriteLine("No vehicles reported."); return; }
    for (var i = 0; i < vehicles.Count; i++)
        Console.WriteLine($"{i + 1}: {vehicles[i].DisplayName}"); // Local UI only; contains a VIN suffix.
    Console.Write("Select vehicle number: ");
    if (!int.TryParse(Console.ReadLine(), out var number) || number < 1 || number > vehicles.Count)
        return;
    var status = await client.GetStatusAsync(vehicles[number - 1].Vin, stop.Token);
    Console.WriteLine($"Battery: {status.BatteryPercent?.ToString("0.#") ?? "Unknown"}% | {status.ChargingState} | Locks: {status.LockState}");
    Console.WriteLine(status.IsStale(DateTimeOffset.UtcNow) ? "Stale or unverified reading" : "Recent vehicle timestamp");
}
catch (BydException error) { Console.Error.WriteLine(error.Message); }
catch (OperationCanceledException) { Console.Error.WriteLine("Read cancelled."); }
finally { password = ""; }
