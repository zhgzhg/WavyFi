using WavyFi.Analysis;
using WavyFi.Models;
using WavyFi.Wlan;

using var scanner = new WifiScanner();
var all = scanner.EnumerateAdapters();
scanner.SelectAdapters(all); // harness exercises every adapter
foreach (var a in all)
    Console.WriteLine($"Adapter [{a.Index}]: {a.Description}");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
// RunContinuationsAsynchronously is required: otherwise the rest of Main
// (including scanner.Dispose) runs on the native wlanapi callback thread,
// and WlanCloseHandle deadlocks waiting for that callback to return.
var scanDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
scanner.ScanCompleted += () =>
{
    Console.WriteLine($"Scan-complete notification after {stopwatch.ElapsedMilliseconds} ms");
    scanDone.TrySetResult();
};

scanner.TriggerScan();
if (await Task.WhenAny(scanDone.Task, Task.Delay(8000)) != scanDone.Task)
    Console.WriteLine("No scan-complete notification within 8 s (reading cache anyway)");

var networks = scanner.GetNetworks();
Console.WriteLine($"Found {networks.Count} BSS entries:\n");
foreach (var n in networks)
    Console.WriteLine($"[{n.AdapterIndex}] {n.DisplayName,-28} {n.Bssid}  {n.Rssi,4} dBm  ch {n.Channel,3} {n.ChannelWidthMhz,3}MHz@{n.CenterChannel,-3}  {n.Band,-7} {n.Standards,-12} max {n.MaxRateMbps,6:0.#} Mbps  {n.Vendor}{(n.IsConnected ? " [connected]" : "")}");

Console.WriteLine("\n--- Recommendations ---");
var entries = networks.Select(n => new NetworkEntry(n, DateTime.Now)).ToList();
Console.WriteLine(ChannelAdvisor.BuildReport(entries));
