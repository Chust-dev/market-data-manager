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
HistoricalData cache catchup  [--instrument EURUSD | --symbols all] [--window 60]
HistoricalData cache discover [--instrument EURUSD | --symbols all] [--since 2000-01-01]
HistoricalData cache audit    [--instrument EURUSD] [--by-year] [--by-month | --no-by-month]
HistoricalData cache size     [--instrument EURUSD] [--by-year] [--sort size|symbol|year|files]
HistoricalData cache add-symbol     --instrument EURGBP --digits 5 [--force] [--no-verify-source]
HistoricalData cache remove-symbol  --instrument BTCUSD [--no-prompt]
HistoricalData cache verify   [--instrument EURUSD] [--start ISO] [--end ISO]
                              [--remote [--size-only] [--parallel N]] [--quiet]
HistoricalData cache repair   [--instrument EURUSD] [--dry-run] [--trust-existing] [--quiet]
HistoricalData cache cleanup  [--instrument EURUSD] [--dry-run] [--quiet]
HistoricalData export bars    --instrument EURUSD --start ... --end ... --timeframe m1
HistoricalData export ticks   --instrument EURUSD --start ... --end ...
```

`cache update` fills or refreshes the `.bi5` pool (network calls). `cache
catchup` is a thin wrapper around `cache update` that refreshes the
rolling last-N days (default 60) — designed for weekly / scheduled
maintenance. `cache discover` binary-searches Dukascopy to find the
earliest available date per symbol and records it in
`instruments.json` so other commands can use it as a sensible lower
bound. `cache audit` reports on the pool's shape (no network).
`cache verify` recomputes SHA-256 on every cached file and flags drift
versus the sidecar metadata written at download time (no network);
add `--remote` to additionally probe Dukascopy and detect source-side drift.
`cache repair` consumes verify's output and surgically re-downloads
problem files (or regenerates missing sidecars). `cache cleanup` is the
destructive last resort — removes zero-byte downloads, orphan sidecars,
and leftover `.tmp` files; **deletes by default, preview with `--dry-run`**.
`export bars` and `export ticks` are offline operations that read
from the pool and produce MT5-compatible files; they never reach
Dukascopy. Run `--help` for the full subcommand reference.

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
│   Download    │     │     Manage     │    │     Export      │
│   (network)   │     │  (inspect/fix) │    │  (read + write) │
│   fills cache │     │ inspects cache │    │ projects cache  │
└───────────────┘     └────────────────┘    └─────────────────┘
   `cache update`       `cache audit`         `export bars`
                        `cache verify`        `export ticks`
                        `cache discover`
```

**Download** is the only pillar that reaches the network for *bulk*
data. It fills or extends the pool, optionally running gap-repair and
validation passes that fetch additional files. Lives in
`src/ConsoleApp/Download/`.

**Manage** inspects, audits, and (eventually) repairs the cache —
counts files per symbol, computes coverage rate, flags zero-byte
downloads, recomputes SHA-256 to catch silent corruption, and
binary-searches Dukascopy for symbol availability metadata. Mostly
read-only; `cache discover` is the one Manage subcommand that issues
network probes (no bulk downloads). Lives in `src/ConsoleApp/Manage/`.

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
--no-prompt
--verbose                   (subcommand mode; opt-in per-URL trace)
--quiet                     (silences progress bar, banners, URL trace, summaries)
--window 60                 (cache catchup: rolling refresh window in days)
--since 2000-01-01          (cache discover: lower bound for binary search)
--parallel 4                (cache discover: per-symbol concurrency)
--refresh                   (cache discover: re-probe symbols already in instruments.json)
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

[src/ConsoleApp/Config/instruments.json](src/ConsoleApp/Config/instruments.json) holds per-symbol metadata:

- `digits` — Dukascopy price scale per symbol (5 for typical FX, 3 for JPY pairs and XAUUSD). Used by exporters and bar aggregation.
- `earliest` — first UTC hour Dukascopy has data for, populated by `cache discover`. `null` means the symbol was probed and isn't on Dukascopy.
- `latest` — most recent UTC hour available on the source. Reserved for future use by the discoverer.

`earliest` and `latest` are optional and additive — files containing only `digits` continue to load without modification.

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
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache audit
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

### Coverage by year and by month

The single-number coverage above answers "is this symbol roughly complete?"
but not "where exactly are the gaps?". Two opt-in flags add finer-grained
grids:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache audit --by-year
```

`--by-year` adds a grid where each row is a symbol and each column is a year
that has at least one cached file. Cells show coverage % for that year (or
`-` if the symbol has no files that year).

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache audit --instrument EURUSD
```

When `--instrument SYM` is passed (single-symbol audit), the **month grid is
included automatically** — output is bounded to one symbol so the extra
detail is almost always wanted. The grid shows each year as a row and
Jan–Dec as columns, with each cell showing coverage % for that month:

```text
EURUSD month-by-month:
       Jan   Feb   Mar   Apr   May   Jun   Jul   Aug   Sep   Oct   Nov   Dec
2003     -     -     -     -   62%   96%   97%   97%   96%   97%   97%   97%
2004    97%   97%   96%   97%   96%   97%   97%   97%   96%   97%   97%   97%
...
```

Cells: `-` for months with zero files (typically the start or end of a
symbol's history); a percentage otherwise. Coverage is `cached_hour_files /
(weekdays_in_month × 24)`. The denominator slightly overestimates expected
hours (forex doesn't trade Friday 22:00 onwards), so a fully-cached month
typically renders as ~98% rather than 100%; the relative comparison between
months is what matters.

To suppress the auto-include for a single-symbol audit, pass `--no-by-month`.
To force the grid for a multi-symbol audit (one block per symbol), pass
`--by-month` explicitly.

## Verifying the cache

`cache audit` is a fast structural check. To prove that no cached file has
silently corrupted (bad sectors, interrupted write, accidental edit), run:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify
```

`cache verify` recomputes the SHA-256 of every `.bi5` file in the pool and
compares against the `<file>.bi5.meta.json` sidecar that was written at
download time (containing the file's recorded SHA-256, byte count, and
download timestamp). It runs entirely offline — no Dukascopy traffic.

Outcomes per file:

- **Ok** — sidecar present, size matches, SHA-256 matches.
- **No metadata** — sidecar `.meta.json` is missing. Most likely: file was
  cached by an older build before sidecars were added, or the sidecar was
  manually deleted. Re-run `cache update` to rewrite both.
- **Size mismatch** — file size disagrees with the sidecar. Strong signal
  of corruption or partial write.
- **Hash mismatch** — size matches but content differs. Disk-level rot or
  in-place edit.
- **I/O error** — file couldn't be read. Permissions or hardware fault.

Filter with `--instrument SYMBOL` or `--symbols SYM1,SYM2`. Use `--quiet`
to silence the progress bar (useful for scheduled jobs that just need the
exit code: 0 = clean, 1 = problems found or run cancelled).

Verification rehashes every file, so it's CPU-bound and slow on full
pools — expect a few minutes per GB on typical hardware. Press `Ctrl+C`
to cancel mid-run; partial results are not saved.

### Detecting source-side drift (`--remote`)

The default local check answers "has my cache rotted on disk?". It cannot
answer "does my cache still match Dukascopy's current bytes?" — Dukascopy
occasionally amends recent ticks, and a pool migrated from another machine
might carry sidecars that disagree with the live source.

`cache verify --remote` adds a per-file probe to Dukascopy:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify --remote --instrument EURUSD
```

For each file that passed the local check, the verifier fetches the same
URL from Dukascopy and compares. **Default is byte-exact**: the body is
streamed and SHA-256-hashed on the fly, then compared against the local
sidecar's recorded hash. This catches all source-side drift, including
content changes that preserve file size.

For a faster, less thorough check, pass `--size-only`:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify --remote --size-only
```

Size-only mode fetches just the response headers (`Content-Length`) and
compares against the local file size. No body download. Catches the common
amendment case (a `.bi5` file usually changes size when Dukascopy edits it,
because LZMA compression is sensitive to content), but won't detect
same-length content changes.

| Mode | Cost | Catches |
|---|---|---|
| `--remote` (default = byte-exact) | re-downloads the pool, ~hours per GB | all drift |
| `--remote --size-only` | one HEAD-equivalent per file, ~minutes per pool | most drift; misses same-size content changes |

Probes are fanned out across **8 concurrent tasks by default**. Override
with `--parallel N`. Sequential probing (`--parallel 1`) is impractical
for full-pool checks — a 50k-file symbol takes hours at concurrency 1.
Raising `--parallel` beyond ~16 risks Dukascopy rate-limiting; if you
start seeing `RemoteUnreachable` cluster in the report, dial it back.

**Scope to a date range with `--start` / `--end`** when you don't need
the whole history checked. Both bounds are ISO 8601 dates (day precision)
and inclusive; either may be omitted to leave that side open:

```text
# Only verify files dated in 2025
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify --instrument EURUSD --start 2025-01-01 --end 2025-12-31

# Everything from 2024 onwards
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify --instrument EURUSD --start 2024-01-01

# Everything up to end of 2023
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache verify --instrument EURUSD --end 2023-12-31
```

The range filter applies to both the local check and (with `--remote`)
the source-side probe, so it's the right lever for scoping a fast drift
check to recent data: probing last quarter is a few thousand files, not
tens of thousands.

Files that fail the local check are **not** probed remotely — there's no
point burning network on a file we already know is bad. Locally-bad files
get `cache repair`'s attention instead.

Remote drift is reported separately from local problems in the output.
Files flagged as `RemoteSizeMismatch` or `RemoteHashMismatch` should be
re-downloaded with `cache repair`. Files marked `RemoteUnreachable` (404
or network failure) are usually transient — re-run later.

## Repairing the cache

`cache repair` consumes the same per-file checks as verify, then auto-fixes
the problems:

```text
dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache repair
```

For each problem file the repair pipeline picks one of two actions:

- **Refetch** — re-download the file from Dukascopy. The fresh bytes
  overwrite the local cache file and a fresh `.meta.json` sidecar is
  written. Default for `SizeMismatch`, `HashMismatch`, and `NoMetadata`.
- **Regenerate sidecar** — trust the local file, compute its current
  SHA-256, and write a `.meta.json` from those bytes. No network. Used
  for `NoMetadata` only when `--trust-existing` is passed.

Files that fail to refetch (Dukascopy returned 404, repeated network
failure) are left untouched and reported. Files with `IoError` are
skipped — they need manual investigation.

Useful flags:

- `--dry-run` — print exactly what would happen without changing anything.
  Always run this once before a real repair on a large pool.
- `--trust-existing` — for users with old caches predating sidecars: skip
  the refetch and just generate sidecars from current bytes. Fast, no
  bandwidth. Only safe if you have reason to believe the local files
  themselves are clean.
- `--instrument` / `--symbols` — narrow the repair to one symbol or a list.
- `--quiet` — silence the progress bar; suitable for scheduled jobs.

Exit code: `0` if the pool was already clean or every problem was
resolved, `1` if any file is still bad after the run (or the run was
cancelled). A successful `cache repair --dry-run` always exits `0` — it's
informational.

## Cleaning up the cache

`cache cleanup` removes files from the pool that are definitively useless:

- **Zero-byte `.bi5` files** — failed/interrupted downloads. `cache update`
  will refetch them next time it sees the missing date in its window.
- **Orphan `.meta.json` sidecars** — sidecar present, matching `.bi5` gone.
  Nothing references them.
- **Leftover `.tmp` files** — partial downloads from interrupted runs.
  `cache update` already cleans these on startup (Session A); cleanup
  catches the ones that accumulated when you ran other subcommands instead.

After deletion, `cache cleanup` prunes empty day / month / year folders so
the tree stays tidy. The symbol root itself is preserved even if empty —
deleting symbol directories is too aggressive a decision for cleanup.

> ⚠️ **Cleanup deletes by default.** Always run with `--dry-run` first
> on an unfamiliar pool:
>
> ```text
> dotnet run --project src/ConsoleApp/HistoricalData.csproj -- cache cleanup --dry-run
> ```
>
> Without `--dry-run`, the listed files are deleted immediately. There is
> no recycle bin and no confirmation prompt.

What `cache cleanup` deliberately does **not** remove:

- Hash-mismatched `.bi5` files. That's `cache repair`'s job (refetch). If
  repair gave up, the file might be wanted next time the source recovers
  — cleanup won't remove it for you.
- Files for symbols not in `instruments.json`. The user might be tracking
  them deliberately.

Filters: `--instrument` / `--symbols` to scope to one symbol or a list.
`--quiet` silences the progress bar. Exit code `0` on a successful run
(including dry-run); `1` if any deletion threw or the run was cancelled.

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
