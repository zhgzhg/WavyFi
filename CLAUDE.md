# CLAUDE.md — working on WavyFi

WavyFi is a Windows WiFi scanner/advisor (WPF GUI + CLI over one engine).
What it does and how it's laid out: see README.md. This file is only the
operational knowledge for coding sessions.

## Architecture in one line

`src/WavyFi.Core` is the engine (WLAN interop, beacon IE parser, PHY-rate
math, models/stores, advisor, OUI db — **no UI dependencies**);
`src/WavyFi` is the WPF front-end plus the CLI verbs. New pure logic goes
in Core so it stays unit-testable; view logic stays in the app project.

## Build / run / test

```sh
dotnet build                       # whole solution
dotnet test                        # 70+ unit tests (fast, offline)
dotnet run --project src/WavyFi    # GUI
src/WavyFi/bin/Debug/net10.0-windows10.0.19041.0/WavyFi.exe --help  # CLI
dotnet run --project tools/ScanTest  # LIVE scan harness (real adapter)
```

If `dotnet` is not on PATH in Git Bash, use `"/c/Program Files/dotnet/dotnet"`.

## Verification ladder

- Pure logic (parser, rates, advisor, stores, formats) → `dotnet test`.
- Anything touching `WifiScanner`/interop/IE parsing → **also** run
  `tools/ScanTest`: unit tests cannot see real beacons; the harness
  scans with every attached adapter and prints what the engine decoded.
- UI changes → rebuild and relaunch the exe; there is no UI test suite.

## Windows gotchas (each of these has cost time before)

- A running `WavyFi.exe` locks `bin/` — kill it before rebuilding:
  `taskkill //IM WavyFi.exe //F` (doubled slashes: Git Bash/MSYS mangles
  single-slash flags into paths; same for `reg add ... //v //d //f`).
- The exe is a **GUI-subsystem** binary. CLI mode attaches to the parent
  console; cmd/PowerShell print their prompt without waiting (an Enter
  keystroke is injected on exit to redraw it). bash waits normally.
  Piped/redirected output is clean — test CLI output through pipes.
- Empty scan results usually mean Windows **Location permission** is off
  (Settings > Privacy & security > Location), not a code bug.
- Native callbacks (`WlanRegisterNotification`, `TaskCompletionSource`
  continuations) run on non-UI threads: marshal via Dispatcher in the
  app; in console code create the TCS with
  `RunContinuationsAsynchronously` or `WlanCloseHandle` can deadlock.
- User settings live in `HKCU\Software\WavyFi` (window placement, font
  scale, `Cols.net`/`Cols.p2p` column layouts) — delete values there to
  test first-run behavior.

## Conventions

- Commit only when the user asks; end commit messages with the
  `Co-Authored-By: Claude ...` line established in history.
- Line endings are LF in-repo (`.gitattributes`); MIT licensed.
- **Zero third-party runtime dependencies** — graphs are custom-drawn;
  keep it that way unless the user decides otherwise.
- Product philosophy: advice-first. Features should serve "what should I
  change about my network", not raw-detail completeness; add pro-level
  detail only when asked (this preference is long-established).
- The OUI vendor snapshot (`src/WavyFi.Core/Resources/manuf.gz`) has a
  documented refresh procedure in README.md; don't fetch it more than
  weekly (upstream's request).
