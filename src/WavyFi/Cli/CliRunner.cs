using System.Globalization;
using System.Runtime.InteropServices;
using WavyFi.Analysis;
using WavyFi.Models;
using WavyFi.WifiDirect;
using WavyFi.Wlan;

namespace WavyFi.Cli;

/// <summary>
/// Console mode: WavyFi.exe with any arguments scans from the terminal
/// instead of opening the window. The exe is a GUI-subsystem binary, so it
/// attaches to the parent terminal's console for output (redirection to a
/// file or pipe works via the inherited standard handles).
/// </summary>
internal static class CliRunner
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll")]
    private static extern bool WriteConsoleInput(
        IntPtr hConsoleInput, InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    private const int AttachParentProcess = -1;
    private const int StdInputHandle = -10;
    private const uint FileTypeChar = 2;

    private static bool _attachedToParent;

    private enum Mode { Scan, P2p }

    private sealed class Options
    {
        public Mode Mode = Mode.Scan;
        public string Adapters = "";      // "", "all", or "0,1"
        public bool ListAdapters;
        public string? Band;              // "2.4", "5", "6"
        public string Search = "";
        public int? MinSignal;
        public bool Csv;
        public bool Advise;
        public int? WatchSeconds;
        public int? TimeoutSeconds;
        public bool Help;
        public bool Version;
        public List<(string Column, bool Desc)> SortSpecs = new();
    }

    /// <summary>Sortable network columns. String keys sort case-insensitively;
    /// band sorts by frequency so 2.4 &lt; 5 &lt; 6.</summary>
    private static readonly Dictionary<string, Func<WifiNetwork, IComparable>> NetworkSortKeys = new()
    {
        ["ssid"] = n => (string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid).ToUpperInvariant(),
        ["bssid"] = n => n.Bssid,
        ["adapter"] = n => n.AdapterIndex,
        ["rssi"] = n => n.Rssi,
        ["signal"] = n => n.SignalPercent,
        ["channel"] = n => n.Channel,
        ["ch"] = n => n.Channel,
        ["width"] = n => n.ChannelWidthMhz,
        ["freq"] = n => n.FrequencyMhz,
        ["band"] = n => n.FrequencyMhz,
        ["standards"] = n => n.Standards,
        ["802.11"] = n => n.Standards,
        ["maxrate"] = n => n.MaxRateMbps,
        ["rate"] = n => n.MaxRateMbps,
        ["security"] = n => n.Security,
        ["cipher"] = n => n.Cipher,
        ["wps"] = n => n.WpsVersion,
        ["vendor"] = n => n.Vendor.ToUpperInvariant(),
        ["connected"] = n => n.IsConnected,
    };

    private static readonly Dictionary<string, Func<WifiDirectPeer, IComparable>> PeerSortKeys = new()
    {
        ["name"] = p => p.Name.ToUpperInvariant(),
        ["rssi"] = p => p.SignalDbm ?? int.MinValue,
        ["type"] = p => p.DeviceType,
        ["paired"] = p => p.IsPaired,
        ["vendor"] = p => p.Vendor.ToUpperInvariant(),
        ["mac"] = p => p.Address,
    };

    public static int Run(string[] args)
    {
        _attachedToParent = AttachConsole(AttachParentProcess);
        if (!_attachedToParent)
            AllocConsole();
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8; // SSIDs can be non-ASCII
        }
        catch { /* redirected or legacy console — keep its default */ }
        Console.WriteLine();

        try
        {
            return RunCore(args);
        }
        finally
        {
            ReleaseParentPrompt();
        }
    }

    private static int RunCore(string[] args)
    {
        Options opts;
        try
        {
            opts = Parse(args);
            Validate(opts);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"WavyFi: {ex.Message}");
            Console.Error.WriteLine("Run 'WavyFi.exe --help' for usage.");
            return 2;
        }

        if (opts.Help)
        {
            PrintUsage();
            return 0;
        }

        if (opts.Version)
        {
            Console.WriteLine($"WavyFi v{AppVersion()} - (c) 2026 zhgzhg - https://github.com/zhgzhg/WavyFi");
            return 0;
        }

        try
        {
            return opts.Mode == Mode.P2p ? RunP2p(opts) : RunScan(opts);
        }
        catch (InvalidOperationException ex)
        {
            // Curated scanner failures (Location denied, adapter gone).
            Console.Error.WriteLine($"WavyFi: {ex.Message}");
            return 1;
        }
    }

    // ----- networks ------------------------------------------------------

    private static int RunScan(Options opts)
    {
        using var scanner = new WifiScanner();
        var adapters = scanner.EnumerateAdapters();
        if (adapters.Count == 0)
        {
            Console.Error.WriteLine("WavyFi: no WiFi adapters found.");
            return 1;
        }

        if (opts.ListAdapters)
        {
            foreach (var a in adapters)
                Console.WriteLine($"[{a.Index}] {a.Description}");
            return 0;
        }

        List<WlanAdapter> selected;
        if (opts.Adapters is "" or "first")
        {
            selected = new List<WlanAdapter> { adapters[0] };
        }
        else if (opts.Adapters == "all")
        {
            selected = adapters.ToList();
        }
        else
        {
            selected = new List<WlanAdapter>();
            foreach (var part in opts.Adapters.Split(','))
            {
                if (!int.TryParse(part.Trim(), out var index) ||
                    adapters.All(a => a.Index != index))
                {
                    Console.Error.WriteLine($"WavyFi: unknown adapter index '{part.Trim()}' — see --list-adapters");
                    return 2;
                }
                selected.Add(adapters.First(a => a.Index == index));
            }
        }
        scanner.SelectAdapters(selected);

        if (!opts.Csv)
        {
            foreach (var a in selected)
                Console.WriteLine($"Scanning on [{a.Index}] {a.Description}");
        }

        do
        {
            var networks = Sweep(scanner, selected.Count, opts.TimeoutSeconds ?? 6);

            var off = scanner.PoweredOffAdapters;
            if (off.Count > 0 && off.Count == scanner.SelectedAdapters.Count)
            {
                Console.Error.WriteLine(
                    "WavyFi: WiFi is turned off — turn it back on in quick settings or Settings > Network & internet.");
                if (opts.WatchSeconds is null) return 1;
            }
            else if (off.Count > 0)
            {
                Console.Error.WriteLine(
                    $"WavyFi: radio off on {string.Join(", ", off)} — results are from the remaining adapters.");
            }

            var filtered = FilterNetworks(networks, opts);

            if (opts.Csv)
                PrintNetworksCsv(filtered);
            else
                PrintNetworksTable(filtered, multiAdapter: selected.Count > 1);

            if (opts.Advise)
            {
                var now = DateTime.Now;
                Console.WriteLine();
                Console.WriteLine(ChannelAdvisor.BuildReport(
                    networks.Select(n => new NetworkEntry(n, now)).ToList()));
            }

            if (opts.WatchSeconds is int interval)
            {
                Console.WriteLine();
                Thread.Sleep(TimeSpan.FromSeconds(interval));
            }
        } while (opts.WatchSeconds is not null);

        return 0;
    }

    /// <summary>Triggers a sweep and waits until every selected adapter
    /// reports scan completion, or the timeout passes.</summary>
    private static IReadOnlyList<WifiNetwork> Sweep(WifiScanner scanner, int adapterCount, int timeoutSeconds)
    {
        var completed = new HashSet<Guid>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(Guid guid)
        {
            lock (completed)
            {
                completed.Add(guid);
                if (completed.Count >= adapterCount)
                    allDone.TrySetResult();
            }
        }

        scanner.ScanCompleted += OnCompleted;
        try
        {
            scanner.TriggerScan();
            allDone.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds));
        }
        finally
        {
            scanner.ScanCompleted -= OnCompleted;
        }
        return scanner.GetNetworks();
    }

    private static List<WifiNetwork> FilterNetworks(IReadOnlyList<WifiNetwork> networks, Options opts)
    {
        var result = networks.Where(n =>
            (opts.Band is null || n.Band.StartsWith(opts.Band, StringComparison.Ordinal)) &&
            (opts.MinSignal is not int min || n.Rssi >= min) &&
            (opts.Search.Length == 0 ||
             (string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid).Contains(opts.Search, StringComparison.OrdinalIgnoreCase) ||
             n.Bssid.Contains(opts.Search, StringComparison.OrdinalIgnoreCase) ||
             n.Vendor.Contains(opts.Search, StringComparison.OrdinalIgnoreCase)));

        if (opts.SortSpecs.Count == 0)
            return result
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.Rssi)
                .ToList();

        IOrderedEnumerable<WifiNetwork>? ordered = null;
        foreach (var (column, desc) in opts.SortSpecs)
        {
            var key = NetworkSortKeys[column];
            ordered = ordered is null
                ? (desc ? result.OrderByDescending(key) : result.OrderBy(key))
                : (desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
        }
        return ordered!.ToList();
    }

    private static void PrintNetworksTable(List<WifiNetwork> networks, bool multiAdapter)
    {
        Console.WriteLine();
        if (networks.Count == 0)
        {
            Console.WriteLine("No networks matched.");
            return;
        }

        string adapterHeader = multiAdapter ? "Ad " : "";
        Console.WriteLine($"{adapterHeader}{"(E)SSID",-28} {"BSSID",-17} {"RSSI",5} {"Ch",4} {"MHz",4} {"Band",-4} {"802.11",-12} {"Max Mbps",9} {"Security",-16} Vendor");

        foreach (var n in networks)
        {
            string adapter = multiAdapter ? $"[{n.AdapterIndex}]" : "";
            string ssid = string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid;
            if (ssid.Length > 28) ssid = ssid[..27] + "…";
            string band = n.Band.Replace(" GHz", "");
            string maxRate = n.MaxRateMbps.ToString("0.#", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{adapter}{ssid,-28} {n.Bssid,-17} {n.Rssi,5} {n.Channel,4} {n.ChannelWidthMhz,4} {band,-4} {n.Standards,-12} {maxRate,9} {n.Security,-16} {n.Vendor}{(n.IsConnected ? "  [connected]" : "")}");
        }
        Console.WriteLine($"\n{networks.Count} network(s), {DateTime.Now:HH:mm:ss}");
    }

    private static void PrintNetworksCsv(List<WifiNetwork> networks)
    {
        Console.WriteLine("Adapter,SSID,BSSID,RSSI (dBm),Channel,Width (MHz),Center,Freq (MHz),Band (GHz),802.11,Max rate (Mbps),Security,Cipher,WPS,Vendor,Connected");
        foreach (var n in networks)
        {
            Console.WriteLine(string.Join(",",
                n.AdapterIndex,
                Csv(string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid),
                n.Bssid, n.Rssi, n.Channel, n.ChannelWidthMhz, n.CenterChannel,
                n.FrequencyMhz, Csv(n.Band.Replace(" GHz", "")), Csv(n.Standards),
                n.MaxRateMbps.ToString("0.#", CultureInfo.InvariantCulture),
                Csv(n.Security), Csv(n.Cipher), Csv(n.WpsVersion), Csv(n.Vendor),
                n.IsConnected ? 1 : 0));
        }
    }

    // ----- WiFi Direct peers ---------------------------------------------

    private static int RunP2p(Options opts)
    {
        using var watcher = new WifiDirectWatcher();
        var sweepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SweepCompleted += () => sweepDone.TrySetResult();

        if (!opts.Csv)
            Console.WriteLine("Discovering WiFi Direct peers...");

        watcher.Start();

        do
        {
            // First iteration waits for a full discovery sweep (or timeout);
            // in watch mode later iterations just snapshot the live watcher.
            sweepDone.Task.Wait(TimeSpan.FromSeconds(opts.TimeoutSeconds ?? 10));

            var peers = FilterPeers(watcher.GetPeers(), opts);
            if (opts.Csv)
                PrintPeersCsv(peers);
            else
                PrintPeersTable(peers);

            if (opts.WatchSeconds is int interval)
            {
                Console.WriteLine();
                Thread.Sleep(TimeSpan.FromSeconds(interval));
            }
        } while (opts.WatchSeconds is not null);

        return 0;
    }

    private static List<WifiDirectPeer> FilterPeers(IReadOnlyList<WifiDirectPeer> peers, Options opts)
    {
        var result = peers.Where(p =>
            (opts.MinSignal is not int min || (p.SignalDbm is int s && s >= min)) &&
            (opts.Search.Length == 0 ||
             p.Name.Contains(opts.Search, StringComparison.OrdinalIgnoreCase) ||
             p.Address.Contains(opts.Search, StringComparison.OrdinalIgnoreCase) ||
             p.Vendor.Contains(opts.Search, StringComparison.OrdinalIgnoreCase) ||
             p.DeviceType.Contains(opts.Search, StringComparison.OrdinalIgnoreCase)));

        if (opts.SortSpecs.Count == 0)
            return result
                .OrderByDescending(p => p.SignalDbm ?? int.MinValue)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        IOrderedEnumerable<WifiDirectPeer>? ordered = null;
        foreach (var (column, desc) in opts.SortSpecs)
        {
            var key = PeerSortKeys[column];
            ordered = ordered is null
                ? (desc ? result.OrderByDescending(key) : result.OrderBy(key))
                : (desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
        }
        return ordered!.ToList();
    }

    private static void PrintPeersTable(List<WifiDirectPeer> peers)
    {
        Console.WriteLine();
        if (peers.Count == 0)
        {
            Console.WriteLine("No WiFi Direct peers found (they only appear while advertising).");
            return;
        }

        Console.WriteLine($"{"Name",-26} {"RSSI",5} {"Type",-18} {"Paired",-6} {"Vendor",-30} MAC");
        foreach (var p in peers)
        {
            string name = p.Name.Length > 26 ? p.Name[..25] + "…" : p.Name;
            string vendor = p.Vendor.Length > 30 ? p.Vendor[..29] + "…" : p.Vendor;
            Console.WriteLine(
                $"{name,-26} {(p.SignalDbm?.ToString() ?? ""),5} {p.DeviceType,-18} {(p.IsPaired ? "yes" : ""),-6} {vendor,-30} {p.Address}");
        }
        Console.WriteLine($"\n{peers.Count} peer(s), {DateTime.Now:HH:mm:ss}");
    }

    private static void PrintPeersCsv(List<WifiDirectPeer> peers)
    {
        Console.WriteLine("Name,RSSI (dBm),Type,Paired,Vendor,MAC");
        foreach (var p in peers)
        {
            Console.WriteLine(string.Join(",",
                Csv(p.Name), p.SignalDbm?.ToString() ?? "", Csv(p.DeviceType),
                p.IsPaired ? 1 : 0, Csv(p.Vendor), p.Address));
        }
    }

    // ----- shared ---------------------------------------------------------

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static Options Parse(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value");

            switch (args[i].ToLowerInvariant())
            {
                case "scan" or "--scan":
                    opts.Mode = Mode.Scan;
                    break;
                case "p2p" or "--p2p" or "wifidirect" or "--wifidirect":
                    opts.Mode = Mode.P2p;
                    break;
                case "--adapters":
                    opts.Adapters = Next("--adapters").ToLowerInvariant();
                    break;
                case "--list-adapters":
                    opts.ListAdapters = true;
                    break;
                case "--band":
                    var band = Next("--band").ToLowerInvariant().Replace("ghz", "").Trim();
                    if (band is not ("2.4" or "5" or "6"))
                        throw new ArgumentException("--band must be 2.4, 5 or 6");
                    opts.Band = band;
                    break;
                case "--search":
                    opts.Search = Next("--search");
                    break;
                case "--min-signal":
                    if (!int.TryParse(Next("--min-signal"), out var signal))
                        throw new ArgumentException("--min-signal needs a dBm number, e.g. -75");
                    opts.MinSignal = signal;
                    break;
                case "--csv":
                    opts.Csv = true;
                    break;
                case "--sort":
                    foreach (var spec in Next("--sort").Split(','))
                    {
                        var pieces = spec.Trim().Split(':');
                        bool desc = pieces.Length > 1 && pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
                        if (pieces.Length > 1 && !desc && !pieces[1].Equals("asc", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException($"--sort direction must be asc or desc, not '{pieces[1]}'");
                        opts.SortSpecs.Add((pieces[0].ToLowerInvariant(), desc));
                    }
                    break;
                case "--advise":
                    opts.Advise = true;
                    break;
                case "--watch":
                    opts.WatchSeconds = 5;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var interval) && interval > 0)
                    {
                        opts.WatchSeconds = interval;
                        i++;
                    }
                    break;
                case "--timeout":
                    if (!int.TryParse(Next("--timeout"), out var timeout) || timeout <= 0)
                        throw new ArgumentException("--timeout needs a positive number of seconds");
                    opts.TimeoutSeconds = timeout;
                    break;
                case "-h" or "--help" or "-?" or "/?":
                    opts.Help = true;
                    break;
                case "-v" or "--version":
                    opts.Version = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        return opts;
    }

    private static void Validate(Options opts)
    {
        if (opts.Help || opts.Version) return;

        if (opts.Mode == Mode.P2p)
        {
            if (opts.Band is not null)
                throw new ArgumentException("--band does not apply to p2p mode");
            if (opts.Adapters.Length > 0 || opts.ListAdapters)
                throw new ArgumentException("adapter selection does not apply to p2p mode (Windows manages the radio)");
            if (opts.Advise)
                throw new ArgumentException("--advise does not apply to p2p mode");
        }

        var sortKeys = opts.Mode == Mode.P2p
            ? PeerSortKeys.Keys
            : NetworkSortKeys.Keys.AsEnumerable();
        foreach (var (column, _) in opts.SortSpecs)
        {
            if (!sortKeys.Contains(column))
                throw new ArgumentException(
                    $"unknown sort column '{column}' for {(opts.Mode == Mode.P2p ? "p2p" : "scan")} — use one of: {string.Join(", ", sortKeys)}");
        }
    }

    /// <summary>cmd/PowerShell do not wait for GUI-subsystem binaries, so
    /// their prompt is printed before our output — inject one Enter into the
    /// interactive console so the shell redraws it. Never done when stdin is
    /// redirected (scripts/pipes stay clean).</summary>
    private static void ReleaseParentPrompt()
    {
        if (!_attachedToParent) return;
        var stdin = GetStdHandle(StdInputHandle);
        if (stdin == IntPtr.Zero || GetFileType(stdin) != FileTypeChar) return;

        var events = new[]
        {
            new InputRecord
            {
                EventType = 1, // KEY_EVENT
                KeyEvent = new KeyEventRecord
                {
                    KeyDown = 1, RepeatCount = 1,
                    VirtualKeyCode = 0x0D, UnicodeChar = '\r',
                },
            },
            new InputRecord
            {
                EventType = 1,
                KeyEvent = new KeyEventRecord
                {
                    KeyDown = 0, RepeatCount = 1,
                    VirtualKeyCode = 0x0D, UnicodeChar = '\r',
                },
            },
        };
        WriteConsoleInput(stdin, events, (uint)events.Length, out _);
    }

    private static string AppVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        // Strip a possible "+commit" suffix the SDK appends.
        if (informational is not null)
            return informational.Split('+')[0];
        var v = assembly.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            WavyFi v{AppVersion()} - scan nearby WiFi networks from the terminal.

            Usage: WavyFi.exe scan [options]      scan for WiFi networks (default)
                   WavyFi.exe p2p  [options]      list advertising WiFi Direct peers
                   (no arguments starts the GUI)

            Options (scan):
              --adapters all|N[,M...]  adapters to scan (default: the first one)
              --list-adapters          list adapters with their indexes and exit
              --band 2.4|5|6           only show this band
              --advise                 append channel recommendations
              --sort COL[:asc|desc][,COL...]
                                       ssid, bssid, adapter, rssi, signal, channel,
                                       width, freq, band, standards, maxrate,
                                       security, cipher, wps, vendor, connected

            Options (p2p):
              --sort COL[:asc|desc][,COL...]
                                       name, rssi, type, paired, vendor, mac

            Options (both):
              --search TEXT            name/BSSID/MAC/vendor substring filter
              --min-signal DBM         hide entries weaker than this (e.g. -75)
              --csv                    CSV output instead of a table
              --watch [SECONDS]        keep scanning every SECONDS (default 5), Ctrl+C stops
              --timeout SECONDS        max wait per sweep (default: 6 scan, 10 p2p)
              -v, --version            print the program version
              -h, --help               this help

            Examples:
              WavyFi.exe scan --band 2.4 --min-signal -80 --advise
              WavyFi.exe scan --adapters all --csv > networks.csv
              WavyFi.exe scan --sort channel:asc,rssi:desc
              WavyFi.exe p2p --sort type --watch 10
            """);
    }
}
