# Backlog

Tasks are grouped by **session**, then by **status**. Sessions are
roughly 1–3 days of focused work. The release plan is to cut **v0.1.0**
after Sessions A–D land, then move to per-version cadence.

## Session A — weekly workflow ready (complete)

Goal: a tool that can be left running on a schedule and keep the cache
fresh for weekly trading research.

| # | Task | Status |
|---|------|--------|
| 8  | `cache catchup` subcommand (last N days) | done |
| 9  | Cross-pillar `ProgressBar` with ETA — utility + Downloader integration | done |
| 10 | Crash-recovery hardening: clean stale `.tmp` files at startup | done |
| 48 | Distinguish HTTP timeout from real cancellation; clean batch summary | done |
| 49 | `cache discover` — find first-available date per symbol | done |
| 50 | Treat HTTP timeouts as missing data, not instrument failures | done |

## Session B — Manage pillar build-out (next)

Goal: make the Manage pillar useful as a standalone diagnostic +
maintenance toolkit, so the cache can be inspected and repaired without
re-downloading everything.

| # | Task | Status | Notes |
|---|------|--------|-------|
| 12 | `cache verify` — local checksum verification | done | SHA-256 vs `.bi5.meta.json` sidecar; per-symbol report; ProgressBar wired |
| 13 | `cache verify --remote` — drift verification against Dukascopy | done | byte-exact by default; `--size-only` for fast Content-Length compare; locally-bad files skipped |
| 14 | Coverage report per month / per year | done | `--by-year` and `--by-month` flags on cache audit; month grid auto-included for single-symbol audits |
| 20 | `cache repair` — auto-fix corrupt files | done | refetch via DukascopyClient, or regenerate sidecar with `--trust-existing`; `--dry-run` previews |
| 21 | `cache cleanup` — remove zero-byte / orphan files | done | deletes by default; `--dry-run` previews; prunes empty parent dirs; preserves symbol root |
| 22 | `cache add-symbol` / `cache remove-symbol` | pending | manage `instruments.json.digits` from CLI |
| 23 | `cache size` — enhanced size breakdown (per-year, sortable) | pending | output formats: text table, csv |
| 52 | Filter non-symbol subfolders out of cache audit (`Exports` leaking in) | done | structural check: subdir must contain at least one 4-digit-year child |
| 53 | Remove vestigial cache-only mode after Manage pillar lands | pending | cleanup |
| 54 | Discover: distinguish transient error from clean not-available | pending | Session A papercut, see Known Issues |
| 55 | Wire ProgressBar into PoolAuditor and Exporters | deferred | Manage pillar deferred — `cache audit` runs in seconds and `cache discover`'s per-symbol streaming output is already the right progress UI; a bar would conflict with it. Exporters may be revisited as a separate concern later. |

## Session C — trading-relevant (later)

Goal: features that matter when the cache is actually being used for
backtesting against a real broker setup.

| # | Task | Status | Notes |
|---|------|--------|-------|
| 15 | DST-aware broker offset (`--broker ic-markets`) | pending | replaces static `--offset` for live-broker alignment |
| 43 | Configurable spread aggregation method (`--spread-method`) | pending | last / median / mean / min |
| 44 | Companion spread-detail CSV (per-bar spread analytics) | pending | for spread modelling in backtests |

## Session D — public-tool polish (later)

Goal: prepare the tool for users who aren't us. Removes legacy cruft,
adds discoverability, and cleans up cosmetic papercuts.

| # | Task | Status | Notes |
|---|------|--------|-------|
| 18 | Weekly catch-up wrapper script (`update.ps1`) | pending | double-clickable; calls `cache catchup --symbols all --no-prompt --quiet` |
| 19 | Dry-run / size estimate (`--dry-run`) | pending | for `cache update`; predicts download volume |
| 38 | Remove legacy flat-flag CLI; subcommands become the only path | pending | breaking change; locks in v0.1.0 |
| 39 | Package as dotnet tool for cross-platform install | pending | `dotnet tool install -g HistoricalData.cli` |
| 51 | Differentiated output for single-symbol vs multi-symbol runs | pending | suppress per-symbol headers when only one |
| 56 | Fix duplicate ProgressBar render on Ctrl+C cancel | pending | cosmetic, ~15 lines |
| 57 | Redesign `cache audit --by-year` layout for wide pools | pending | 23 years × 29 symbols at ~7 chars per cell is ~200 cols wide and wraps awkwardly. Options on the table: compact unicode heatmap (1 char/year), chunked 8-year pages, per-symbol gap summary, CSV output. User deferred decision. |

## Release

| # | Task | Status |
|---|------|--------|
| 46 | Cut v0.1.0 release on GitHub (after Sessions A–D land) | pending |
| 47 | Per-version release cadence (post-v0.1.0) | pending |

## Parked — Download

Out of scope for the v0.1.0 push but not abandoned.

| # | Task | Reason parked |
|---|------|---------------|
| 11 | Parallel symbol downloads (`--parallelism N`) | Real-life single-symbol downloads are already saturating the upstream; multi-symbol parallel needs careful design |
| 24 | Bandwidth throttling (`--max-mbps N`) | Not currently a problem |
| 25 | Per-symbol log files for multi-symbol runs | Wait for #11 first |
| 26 | Configurable retry policy (`--max-retries`, `--backoff-base`) | Default policy works fine |
| 40 | Darwinex as a second tick data source | Major architectural addition; speculative |

## Parked — Manage

| # | Task | Reason parked |
|---|------|---------------|
| 27 | `cache migrate` — pool layout migration | No layout change planned |

## Parked — Export

| # | Task | Reason parked |
|---|------|---------------|
| 16 | Per-month parallelism for tick CSV writer | Not a bottleneck yet |
| 17 | Additional output formats (Parquet, JSON) | Tickle when someone needs them |
| 28 | Multi-timeframe export in one pass | CLI complexity vs run-twice tradeoff |
| 29 | Bid / Ask / Mid bar source selection | BID is fine for now |
| 30 | Session-window filter (`--session london\|ny\|asia`) | Backtest-specific, not core |
| 31 | Holiday calendar (exclude bars on specified dates) | Backtest-specific |
| 32 | Compressed CSV output (`--compress gzip`) | Disk is cheap |
| 33 | Direct MT5 install (`--mt5-target PATH`) | Manual copy works fine |
| 41 | Configurable tick CSV split (`--tick-split month\|quarter\|year\|none`) | Monthly is good enough |
| 42 | Generate MQL5 import script alongside tick CSVs | Speculative |
| 45 | Fixed broker-spread override (`--spread SYM=N` or `spreads.json`) | #43 is the cleaner path |

## Parked — Cross-cutting

| # | Task | Reason parked |
|---|------|---------------|
| 34 | Configuration profiles (`--profile NAME`) | Premature abstraction |
| 35 | Notification on completion (`--notify discord:url`) | Wrapper script can do this |
| 36 | Logging to file with rotation | Output redirection works fine |
| 37 | Self-update check on startup | Not a daily-use tool yet |

## Known issues (currently shipped)

Documented in `CHANGELOG.md` under "Known issues (parked from Session A)".

- **#54** — `cache discover` writes `null` for both clean-not-available
  and transient HTTP errors; idempotent skip then prevents retry.
  Workaround: `--refresh`.
- **#56** — duplicate ProgressBar render on Ctrl+C. Cosmetic.
- ~~**#52** — `Exports` folder appears as a symbol in cache audit output~~
  → **fixed** in Session B (`LooksLikeSymbolDir` structural filter).
