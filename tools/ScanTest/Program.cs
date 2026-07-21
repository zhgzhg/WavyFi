using WifiOptimizer.Analysis;
using WifiOptimizer.Models;
using WifiOptimizer.Wlan;

using var scanner = new WifiScanner();
Console.WriteLine($"Adapter: {scanner.InterfaceDescription}");
scanner.TriggerScan();
await Task.Delay(4000);

var networks = scanner.GetNetworks();
Console.WriteLine($"Found {networks.Count} BSS entries:\n");
foreach (var n in networks)
    Console.WriteLine($"{n.DisplayName,-28} {n.Bssid}  {n.Rssi,4} dBm  ch {n.Channel,3} {n.ChannelWidthMhz,3}MHz@{n.CenterChannel,-3}  {n.Band,-7} {n.Standards,-12} WPS:{n.WpsVersion,-4} {n.Vendor}{(n.IsConnected ? " [connected]" : "")}");

Console.WriteLine("\n--- Recommendations ---");
var entries = networks.Select(n => new NetworkEntry(n, DateTime.Now)).ToList();
Console.WriteLine(ChannelAdvisor.BuildReport(entries));
