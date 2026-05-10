# Changelog

## Unreleased

### Session B — Manage pillar build-out

- Added `cache verify` subcommand (BACKLOG #12): recomputes SHA-256 on every cached `.bi5` file and compares against the `<file>.bi5.meta.json` sidecar written at download time. Reports per-symbol and aggregate counts of Ok / NoMetadata / SizeMismatch / HashMismatch / IoError outcomes, plus a capped detail list of the first 100 problematic files. Pure offline — no Dukascopy traffic. Honours `--instrument` / `--symbols` filters and `--quiet`. Wired into the cross-pillar `ProgressBar` (two-phase API: `PlanFiles` enumerates upfront so the bar knows the total, `RunPlan` does the SHA-256 work while polling the verifier's `FilesProcessed` counter). Exit code 0 if every cached file verifies clean, 1 otherwise — suitable for scheduled drift detection. `Ctrl+C` cancels mid-run.

### Known issues (parked from Session A)

- `cache discover` conflates clean "not available" responses with transient HTTP errors and writes both as `null` in `instruments.json`. The idempotent skip filter then permanently treats the symbol as unavailable until `--refresh` is passed. Workaround: re-run with `--refresh`. Fix planned post-Session-A.
- A duplicate progress bar line can appear on Ctrl+C when cancelling mid-run. Cosmetic only; the underlying cancellation path is correct.

### Session A — weekly workflow ready

- Added `cache discover` subcommand: binary-searches Dukascopy to find the earliest available date per symbol and records it in `instruments.json`. Supports single-symbol, explicit list, and `--symbols all` modes; results merge into the existing config without disturbing the `digits` map. Idempotent (already-discovered symbols are skipped on re-run); pass `--refresh` to re-probe. Configurable via `--since YYYY-MM-DD` (default 2000-01-01) and `--parallel N` (default 4).
- Added `cache catchup` subcommand: refreshes the rolling N-day window of the `.bi5` cache. Forces `RefreshCache=true` and `RecentRefreshDays = window + 1` so the entire window is re-fetched from Dukascopy each run, capturing Dukascopy's amendments to recent ticks. Defaults to 60 days; override with `--window N`. Designed for scheduled / weekly maintenance: `cache catchup --symbols all --no-prompt` is cron-friendly.
- Extended `instruments.json` schema with `earliest` and `latest` sections (additive, backward-compatible — existing digits-only files load unchanged). The `Save` method writes atomically (`.tmp` + rename) so a crash mid-write can't corrupt the file.
- Added cross-pillar `ProgressBar` utility (`src/ConsoleApp/Utils/ProgressBar.cs`): in-place updating bar with percent, current/total counts, and computed ETA. Generic over a `Func<long>` counter source so any pillar can plug in its own current-value supplier. Falls back to milestone lines when output is redirected (non-TTY); silenced entirely with `--quiet`. Wired into the Download pillar's main download loop, gap repair, and M1 validation passes.
- `--verbose` flipped to opt-in (default OFF). Previous behaviour printed every "Downloading ..." URL by default and interleaved with the progress bar; opt-in keeps the bar clean for normal use. Pass `--verbose` to restore the old per-URL trace output for debugging.
- Added `--quiet` flag: silences progress bars, banners, URL traces, and per-instrument summaries. Useful for scheduled tasks that just need the exit code. Independent from `--verbose`; `--quiet` wins if both are passed.
- Crash-recovery hardening: any `.tmp` files older than 1 hour are cleaned up at the start of each `cache update` run. Scoped to ONLY the symbols in the current request — concurrent runs on different symbol sets stay safe. These are leftovers from previous interrupted downloads that are never recovered or used.
- HTTP timeout vs cancellation: `TaskCanceledException` from `HttpClient` (request timeout) is now distinguished from real user cancellation (Ctrl+C) using exception filters (`when (cancellationToken.IsCancellationRequested)`). Timeouts are treated as transient failures the retry loop handles; only Ctrl+C aborts the run. Batch summary now reports a separate `Cancelled` count, replacing the misleading "Failed: 0, Succeeded: 0" output that used to appear on Ctrl+C mid-run.

### Refactor (pre-Session A)

- **Refactor (Layer 3)**: Internals reorganized around three pillars matching the cache-centric architecture. The Download pillar (`src/ConsoleApp/Download/Downloader.cs`) is now the only place network calls originate — it fills the `.bi5` cache. The Display pillar (`PoolAuditor`, unchanged) reads the cache without modification. The Export pillar (`src/ConsoleApp/Export/BarExporter.cs`, `TickExporter.cs`) reads the cache and projects it into MT5-compatible files. Each subcommand class now calls exactly one pillar directly, taking its typed options record without translating through `AppOptions`. The legacy flat-flag CLI continues to use the pre-refactor unified engine.
- **Refactor (Layer 2)**: Each subcommand now has its own typed options record (`CacheAuditOptions`, `CacheUpdateOptions`, `BarExportOptions`, `TickExportOptions`) parsed via shared `CommonParsingHelpers`. The four commands no longer share one "god record" of every possible option. Legacy `AppOptions` remains as a transitional bridge to the engine; Layer 3 will replace it with command-specific entry points.
- **Refactor (Layer 1)**: Introduced subcommand CLI surface — `cache update`, `cache audit`, `export bars`, `export ticks`. Each subcommand is a dedicated class implementing `ICommand`. The legacy flat-flag invocation (`--instrument X --start Y ...`) continues to work as a compatibility shim. Internal pipeline extracted into `Program.RunDownloadFlow` for reuse.
- Added `OutputFormat.None` so `cache update` can run the engine without producing any bar export files.
- Added `--audit` flag: prints a fast (no-decompression) summary of cached `.bi5` files in the pool — per-symbol date range, file counts, coverage rate, disk usage, and empty-file detection.
- Added `--export-ticks` flag: writes per-month tick CSVs alongside the M1 CSV/HST output, in MT5 Symbol Editor import format. Requires `--mode ticks`.
- Expanded `instruments.json` to 29 symbols (G10 majors, crosses, and XAUUSD).
- `--pool` and `--output` defaults changed to `D:\MarketData` and `D:\MarketData\Exports` for the local development setup.

## v1.5.0 - 2026-02-11

- Added support for h4, h6, d1, w1, mn1, and custom m<minutes> timeframes.
- Calendar-aligned resampling for day/week/month buckets; fixed-minute alignment for multi-hour frames.
- CI runs tests on Windows, macOS, and Linux; release builds are gated on tests.
