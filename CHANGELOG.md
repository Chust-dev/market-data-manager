# Changelog

## Unreleased

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
