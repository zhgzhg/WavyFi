# WifiOptimizer

A lightweight inSSIDer-style scanner for Windows, built with C# / WPF
(no third-party dependencies). Its goal is not raw detail but actionable
advice: see what's around you (WiFi networks + WiFi Direct devices) and
get a recommendation for the optimal settings of your own network.

## Features

- **Nearby networks** — SSID, signal (%, dBm), channel, frequency, band
  (2.4/5/6 GHz), security, BSSID, last-seen age. Your connected network is
  highlighted. Networks persist across scans: entries that drop out fade
  (stale) and are removed after 2 minutes. Auto-refreshes every 5 seconds
  via the Native WiFi API (`wlanapi.dll`).
- **Security detail** — auth (WPA2/WPA3...), cipher (AES-CCMP, TKIP...),
  WPS version parsed from the WPS information element ("-" when absent),
  and supported rates as CSV. Right-click the table header to choose
  visible columns.
- **Vendor identification** — each BSSID (and WiFi Direct peer) is resolved
  to its manufacturer via an embedded IEEE OUI snapshot (see below).
- **Filtering** — search box (SSID/BSSID/vendor), band selector, and minimum
  signal slider; the table and graphs both respect the active filter.
- **Signal over time** — select one or more rows to plot their RSSI
  history (5-minute sliding window, colors match the channel graphs).
- **Channel graphs** — inSSIDer-style occupancy curves per band
  (custom-drawn, no chart library): each network is a bell curve spanning
  its real bonded width (20 up to 320 MHz, from the HT/VHT/HE/EHT
  Operation elements), peaking at its RSSI, colored by BSSID, faded when stale.
  Selected table rows are emphasized. Channel 14 is placed at its true
  2484 MHz position.
- **WiFi Direct devices** — live discovery of advertising P2P peers
  (phones, TVs, Miracast receivers, printers) via `Windows.Devices.WiFiDirect`,
  shown in a compact sortable grid: name, RSSI (when reported), device type,
  pairing state, vendor, MAC, last seen. The search box and minimum-signal
  slider filter this grid too. Peers advertise in bursts, so they are kept
  for the whole session: stale ones fade and their age keeps counting.
- **Recommendations** — congestion-scored best channel for 2.4 GHz
  (among 1/6/11) and 5 GHz (non-DFS, with a DFS hint when quieter),
  overlap analysis for your own network, and security upgrade advice.

## Build & run

Requires the .NET 10 SDK on Windows 10 19041+.

```
dotnet build
dotnet run
```

If the network list is empty, enable Location access in
*Windows Settings > Privacy & security > Location* — Windows gates
WiFi scan results behind it.

## Usage

- **Scanning toggle** — starts/stops the 5-second scan loop. Turning it
  on clears previously accumulated data (fresh session). While it's on,
  the adapter dropdown is locked; pause to switch adapters.
- **Adapter dropdown** — lists all WLAN adapters; re-enumerated each time
  it opens, so USB adapters plugged in later appear. If the active
  adapter disappears, scanning falls back to the next available one.
- **Table** — click a row to select (plain click on a selected row
  unselects; empty space clears the selection). Selected networks are
  plotted in the signal-over-time graph and emphasized in the channel
  graphs. Right-click the header for the column chooser; hover a header
  for a description of the column. Right-click a cell for copy options
  (cell / rows / whole filtered table as CSV); Ctrl+C copies selected rows.
- **Filters** — search matches name, BSSID, and vendor; band and minimum
  signal narrow the table and graphs (recommendations always use all data).
- **Persistence** — networks that drop out of a scan fade (stale) and are
  removed after 2 minutes unseen; "Last seen" shows the age. WiFi Direct
  peers fade too but are kept for the whole session (their advertising is
  bursty, so absence rarely means the device left).
- **WiFi Direct grid** — sortable like the network table (RSSI sorts
  numerically); right-click a row for copy options; header right-click
  opens its own column chooser. Peers with no signal reading are hidden
  only while the minimum-signal slider is raised.

## Known limitations

- One adapter is scanned at a time (the dropdown selection); WiFi Direct
  discovery uses whatever radio Windows chooses for it.
- No monitor-mode capture — everything comes from standard scans, which
  is all the advice engine needs.
- Scan sweeps take the radio 2-5 s (DFS/6 GHz channels are listened to
  passively). The app refreshes as soon as the driver signals scan
  completion (`WlanRegisterNotification`), in addition to the 5 s timer.
- WiFi 7 (802.11be) widths are parsed from the EHT Operation element;
  320 MHz shows correctly only when the AP advertises it there.
- "Your own network" grouping (excluding your router's other radios from
  congestion advice) uses a MAC heuristic — mesh nodes with unrelated
  MACs still count as neighbors.

## OUI vendor database

The Vendor column (and WiFi Direct device details) resolve MAC prefixes
against an embedded copy of the **Wireshark `manuf` file** — the IEEE
Registration Authority's OUI registry as curated and published by the
Wireshark project:

- Canonical source: <https://www.wireshark.org/download/automated/data/manuf>
- Credit: OUI data © IEEE Registration Authority; compiled and maintained
  by the Wireshark project. Upstream regenerates it weekly — per their
  request, do not fetch it more often than that.

The snapshot lives at `Resources/manuf.gz` (embedded resource, last
updated 2026-07-21). To refresh it:

```sh
curl -sL https://www.wireshark.org/download/automated/data/manuf -o Resources/manuf
gzip -9 -f Resources/manuf   # produces Resources/manuf.gz
dotnet build                 # re-embeds the resource
```

Only plain 24-bit OUI rows are used (the rarer /28 and /36 sub-allocations
are skipped). Locally administered MACs — randomized or virtual BSSIDs —
never appear in the registry and are shown as "(unknown vendor)".

## Layout

| Path | Purpose |
|---|---|
| `Wlan/` | P/Invoke bindings and scanner over the Native WiFi API |
| `WifiDirect/` | `DeviceWatcher`-based WiFi Direct peer discovery |
| `Analysis/` | Channel congestion scoring and recommendation text |
| `Models/` | Data models and the persistent scan-to-scan network store |
| `Controls/` | Custom-drawn channel occupancy and signal-over-time graphs |
| `Data/` | Loader for the embedded OUI (MAC prefix → vendor) database |
| `Resources/` | Embedded assets (`manuf.gz` OUI snapshot) |
| `tools/ScanTest/` | Console harness for testing the scanner without the UI |
