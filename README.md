# HistoricalData

[![CI](https://github.com/sammirzagharcheh/-DukascopyHistoricalTickDownloader-/actions/workflows/ci.yml/badge.svg)](https://github.com/sammirzagharcheh/-DukascopyHistoricalTickDownloader-/actions/workflows/ci.yml)

C# console app that downloads Dukascopy historical tick data (.bi5 LZMA), converts it to MT5 bars, and exports CSV + HST (MT5 build 5430 compatible layout). Uses a local data pool to cache raw Dukascopy files for incremental updates.

## Requirements

- .NET 10 SDK

## CI

CI runs `dotnet test` on Windows, macOS, and Linux for pull requests and pushes to `main`.

## Usage

The CLI is organized as **subcommands** that separate cache maintenance from
data export. The legacy flat-flag interface continues to work for existing
scripts.

```text
HistoricalData cache update   --instrument EURUSD --start ... --end ...
HistoricalData cache audit    [--instrument EURUSD]
HistoricalData export bars    --instrument EURUSD --start ... --end ... --timeframe m1
HistoricalData export ticks   --instrument EURUSD --start ... --end ...
```

`cache update` fills or refreshes the `.bi5` pool (network calls). `cache
audit` reports on the pool's shape (no network). `export bars` and
`export ticks` are offline operations that read from the pool and produce
MT5-compatible files; they never reach Dukascopy. Run `--help` for the full
subcommand reference.

## Architecture

The tool is organized around the **`.bi5` data pool as the central asset**,
with three independent pillars rotating around it:

```text
                    ┌──────────────────────┐
                    │   .bi5 data pool     │
                    │  (D:\MarketData)     │
                    └──────────┬───────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        │                      │                      │
        ▼                      ▼                      ▼
┌───────────────┐     ┌────────────────┐    ┌─────────────────┐
│   Download    │     │    Display     │    │     Export      │
│   (network)   │     │   (read-only)  │    │  (read + write) │
│   fills cache │     │ inspects cache │    │ projects cache  │
└───────────────┘     └────────────────┘    └─────────────────┘
   `cache update`       `cache audit`         `export bars`
                                              `export ticks`
```

**Download** is the only pillar that reaches the network. It fills or
extends the pool, optionally running gap-repair and validation passes
that fetch additional files. Lives in `src/ConsoleApp/Download/`.

**Display** reads the cache directory tree without modifying or
decompressing anything — counts files per symbol, computes coverage
rate, flags zero-byte downloads. Lives in `src/ConsoleApp/Audit/`.

**Export** reads the cache (no network) and projects it into derivative
artifacts: M1/higher-timeframe bars as MT5 CSV/HST, raw ticks as
per-month CSV in MT5 Symbol Editor import format. Future formats
(Parquet, JSON, broker-specific binary) drop in as additional
exporter classes alongside `BarExporter` and `TickExporter` in
`src/ConsoleApp/Export/`.

The three pillars are independent: you can run any of them without the
others, in any order. Cache update doesn't touch exports; exports never
trigger downloads. Each subcommand calls exactly one pillar.

The legacy flat-flag CLI (`--instrument X --start Y ...`) still works
and continues to use a unified engine that combines all three pillars'
work in one run. New scripts should use the subcommand syntax above.

## End User Guide (Setup and Run)

### Option A: Download a Release (recommended)

1. Go to the GitHub Releases page (latest stable: [releases/latest](../../releases/latest)).
2. Download the zip for your platform from the latest release:
   - `HistoricalData-win-x64.zip`
   - `HistoricalData-linux-x64.zip`
   - `HistoricalData-osx-x64.zip`
3. Extract the zip to a folder.
4. Run the app:
   - Windows: double-click `HistoricalData.exe` or run it from Command Prompt.
   - Linux/macOS: run `./HistoricalData` from a terminal.

### Option B: Build from Source

1. Install the .NET 10 SDK.
2. Open a terminal in the project folder.
3. Run:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj
```

### Quick Start Example

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- --instrument EURUSD --start 2025-01-01T00:00:00Z --end 2025-01-01T01:00:00Z --timeframe m15 --mode ticks --format csv --offset +00:00 --pool ./DataPool --output ./output --no-prompt
```

### Output Files

After a successful run, output files are written to the `output` folder:

- `SYMBOL_TIMEFRAME.csv`
- `SYMBOL_TIMEFRAME.hst` (if `--format csv+hst` was selected)

### Common Options

- `--instrument` Currency pair (example: `EURUSD`)
- `--symbols` Comma-separated symbols or `all` (example: `EURUSD,XAUUSD`)
- `--digits` Force digits for all requested symbols in this run (example: `3`)
- `--digits-map` Per-symbol digits override (example: `XAUUSD=3,USDJPY=3`)
- `--start` Start time in ISO 8601
- `--end` End time in ISO 8601
- `--timeframe` `m1|m5|m15|m30|h1|h4|h6|d1|w1|mn1|m<minutes>`
- `--mode` `ticks` or `direct`
- `--format` `csv` or `csv+hst`
- `--offset` UTC offset (example: `+02:00`)
- `--pool` Data pool cache folder
- `--output` Output folder
- `--recent-refresh-days` Refresh recent data window in days (default 30)
- `--verify-checksum` Verify cached file checksums (default ON)
- `--no-verify-checksum` Disable checksum validation
- `--repair-gaps` Fill missing minutes using direct M1 bars (default ON)
- `--no-repair-gaps` Disable gap repair
- `--validate-m1` Validate tick-derived M1 vs direct M1 (default ON)
- `--no-validate-m1` Disable M1 validation
- `--validation-tolerance-points` Allowed OHLC delta in points (default 1)
- `--no-refresh` Use cached pool files (default refresh is ON)
- `--no-dedupe` Disable strict tick de-duplication (default is ON)
- `--allow-fallback-overlap` Allow fallback M1 bars to merge with tick minutes
- `--use-session-calendar` Enable session calendar filtering
- `--no-session-calendar` Disable session calendar filtering
- `--session-config` Path to session calendar config
- `--export-ticks` Also write per-month tick CSVs (MT5 import format). Requires `--mode ticks`.
- `--audit` Inspect the data pool and print a coverage summary; no download is performed.

### Tips

- If you get a Windows SmartScreen warning, click “More info” → “Run anyway.”
- Large date ranges take time; start with a short range to verify settings.
- Use `--help` to list all options.

### Interactive

Run without arguments to be prompted for:

- Instrument (default EURUSD)
- Start / End (ISO 8601)
- Timeframe (m1, m5, m15, m30, h1, h4, h6, d1, w1, mn1, or m<minutes>)
- Download mode (default Tick->M1)
- Output format (default CSV+HST)
- UTC offset (default +00:00)
- Data pool path (default /DataPool)
- Output path (default ./output)

### CLI arguments

```text
--instrument EURUSD
--symbols EURUSD,XAUUSD|all
--digits 5
--digits-map EURUSD=5,XAUUSD=3
--start 2025-01-01T00:00:00Z
--end 2025-01-03T00:00:00Z
--timeframe m1|m5|m15|m30|h1|h4|h6|d1|w1|mn1|m<minutes>
--mode direct|ticks
--format csv|csv+hst
--offset +02:00
--pool /DataPool
--output ./output
--instruments ./src/ConsoleApp/Config/instruments.json
--http ./src/ConsoleApp/Config/http.json
--no-refresh
--recent-refresh-days 30
--verify-checksum
--no-verify-checksum
--no-dedupe
--skip-fallback-overlap
--allow-fallback-overlap
--repair-gaps
--no-repair-gaps
--validate-m1
--no-validate-m1
--validation-tolerance-points 1
--use-session-calendar
--no-session-calendar
--session-config ./src/ConsoleApp/Config/sessions.json
--export-ticks
--audit
--no-prompt
--quiet
--help
```

### Sample run

```text
dotnet run --project c:\sampleApp\HistoricalData\src\ConsoleApp\HistoricalData.csproj -- --instrument EURUSD --start 2025-01-01T00:00:00Z --end 2025-01-03T00:00:00Z --timeframe m15 --mode ticks --format csv+hst --offset +00:00 --pool /DataPool --output ./output --no-prompt
```

### Build new timeframes from cached ticks

Yes. If tick files already exist in the data pool, you can generate a new timeframe without re-downloading by adding `--no-refresh`.

Example (build M15 from cached ticks):

```text
dotnet run --project c:\sampleApp\HistoricalData\src\ConsoleApp\HistoricalData.csproj -- --instrument EURUSD --start 2025-01-01T00:00:00Z --end 2025-01-03T00:00:00Z --timeframe m15 --mode ticks --format csv+hst --offset +00:00 --pool /DataPool --output ./output --no-prompt --no-refresh
```

## Config

### Instruments

[src/ConsoleApp/Config/instruments.json](src/ConsoleApp/Config/instruments.json) maps symbol to digits.

### HTTP

[src/ConsoleApp/Config/http.json](src/ConsoleApp/Config/http.json) configures base URLs, retry, and timeout.

### Sessions

[src/ConsoleApp/Config/sessions.json](src/ConsoleApp/Config/sessions.json) defines trading sessions and optional holidays.

## Notes

- Tick files use .bi5 LZMA compression.
- Direct M1 mode downloads daily `BID_candles_min_1.bi5` files.
- Explicit symbols are validated against Dukascopy at runtime; they are not hard-limited to `instruments.json`.
- `instruments.json` remains the curated source for `--symbols all` and default digit hints.
- M1 fallback is used if tick download fails.
- Weekend bars are filtered.
- UTC offset is applied to output alignment.
- Ctrl+C cancels the run gracefully.
- Cache refresh is ON by default; use `--no-refresh` to reuse existing pool files.
- Recent refresh window defaults to 30 days; use `--recent-refresh-days` to change it.
- Checksum verification is ON by default; use `--no-verify-checksum` to skip.
- Tick de-duplication is ON by default; use `--no-dedupe` to disable.
- Tick minutes override fallback M1 by default; use `--allow-fallback-overlap` to merge.
- Gap repair (missing minutes) is ON by default; use `--no-repair-gaps` to disable.
- M1 validation is ON by default; use `--no-validate-m1` to disable.
- Session calendar filtering is OFF by default; use `--use-session-calendar` to enable.

## Direct vs Ticks

- `--mode ticks` downloads hourly tick files (`HHh_ticks.bi5`) and aggregates to M1 before resampling to the requested timeframe.
- `--mode direct` downloads daily M1 bars (`BID_candles_min_1.bi5`) and resamples to the requested timeframe.
- If tick download fails and fallback is enabled, the app uses daily M1 bars for that day.

## Auditing the data pool

After downloads have populated `D:\MarketData` (or whatever `--pool` you used),
you can inspect what's there without re-downloading:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- --audit
```

This walks the pool directory, counts cached `.bi5` files per symbol, computes
the date range of cached hourly tick files, flags any zero-byte (corrupt)
files, and reports total disk usage. Filter to a specific symbol or set with
`--instrument` or `--symbols`. Example output:

```text
Pool: D:\MarketData
Symbols cached: 3
Disk usage:     7.42 GB
Hour tick files:    188,432
Daily M1 files:       8,176
Empty files:              2  (zero-byte; consider deleting)

Per-symbol coverage:
  Symbol     FirstHour            LastHour              HourFiles  DayFiles   Coverage       Disk  Empty
  EURUSD     2003-05-04 00:00     2026-05-03 23:00         147,221    8,176     97.2%    5.84 GB      0
  GBPUSD     2010-01-04 22:00     2026-05-03 23:00          41,211        0     94.8%    1.58 GB      2
  XAUUSD     2010-01-04 22:00     2026-05-03 23:00              -        0       -      0 B          0
```

`Coverage` is the ratio of cached hourly tick files to the expected number of
weekday hours between `FirstHour` and `LastHour`. ~95–100% is healthy for a
recently completed download; anything significantly lower suggests gaps to
re-fetch (a re-run with the same `--start`/`--end` will fill them). Audit is
fast (seconds) because it does not decompress the `.bi5` files.

## Data Pool Structure

Downloaded files are cached under the data pool path:

```text
<pool>/<instrument>/<year>/<month>/<day>/
```

Examples:

```text
DataPool/EURUSD/2025/00/01/00h_ticks.bi5
DataPool/EURUSD/2025/00/01/BID_candles_min_1.bi5
```

## Output

- CSV: SYMBOL_TIMEFRAME.csv
- HST: SYMBOL_TIMEFRAME.hst
- Tick CSV (only with `--export-ticks`): SYMBOL_ticks_yyyy-MM.csv, one file per calendar month

## Output formats

## Release

- See [CHANGELOG.md](CHANGELOG.md) for release notes.
- See [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) for the release process.

### CSV (MT5-compatible)

Each row represents one bar with these columns in order:

1. Date in yyyy.MM.dd
2. Time in HH:mm
3. Open
4. High
5. Low
6. Close
7. Tick volume
8. Spread (in points)
9. Real volume

Times are aligned using the configured UTC offset, and weekend bars are filtered.

### HST (MT5 build 5430)

The HST file uses version 501 with this layout:

- Header: version, copyright, symbol, timeframe (minutes), digits,
  last sync time, last bar time, and reserved fields.
- Records: time (Unix seconds), open, high, low, close, volume,
  spread, real volume.

Times are aligned using the configured UTC offset, and prices are rounded
to the symbol digits from src/ConsoleApp/Config/instruments.json.

### Tick CSV (MT5 import format)

Enabled with `--export-ticks` (requires `--mode ticks`). After the bar pipeline finishes,
the app makes a sequential pass over the cached `.bi5` tick files and writes one CSV per
calendar month so individual files stay manageable. Long-history exports for a single
symbol can otherwise grow to hundreds of GB in one file.

File naming: `SYMBOL_ticks_yyyy-MM.csv` (e.g. `EURUSD_ticks_2025-01.csv`).

Each row represents one Dukascopy tick:

1. Date in yyyy.MM.dd
2. Time in HH:mm:ss.fff (millisecond precision)
3. Bid
4. Ask
5. Last (always 0; Dukascopy doesn't publish last-trade prices for FX)
6. Volume (always 0; Dukascopy bid/ask volumes are not real-trade volumes)
7. Flags = 6 (TICK_FLAG_BID | TICK_FLAG_ASK)

Times are aligned using the configured UTC offset.

To import into MetaTrader 5: open Symbol Editor (right-click a symbol → Specification → Bars
or Ticks tab), choose the per-month CSV, set the separator to comma, and the time format to
`yyyy.MM.dd HH:mm:ss.fff`. Import months in chronological order so MT5's tick database stays
contiguous.

## Release Builds (GitHub Actions)

When a GitHub Release is published, a workflow builds self-contained binaries for:

- win-x64
- linux-x64
- osx-x64

Each publish folder is zipped and attached to the release as:

- `HistoricalData-win-x64.zip`
- `HistoricalData-linux-x64.zip`
- `HistoricalData-osx-x64.zip`

### Optional Signing

Windows `.exe` signing uses a PFX certificate. To enable signing, add these GitHub Secrets:

- `SIGNING_PFX`: base64-encoded PFX file
- `SIGNING_PFX_PASSWORD`: PFX password

If the secrets are not set, the workflow skips signing.

To generate the base64 value locally (PowerShell):

```text
[Convert]::ToBase64String([IO.File]::ReadAllBytes("path\\to\\certificate.pfx"))
```

## Tests

```text
dotnet test
```
