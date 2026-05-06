# Release Checklist

## Before release

- Ensure CI is green on main.
- Run `dotnet test` locally if needed.
- Update CHANGELOG.md with the release version/date.

## Create release

- Create a GitHub Release (draft or publish) with a tag like vX.Y.Z.
- Wait for the Release workflow to finish and upload artifacts.

## Verify artifacts

- Download artifacts for win-x64, linux-x64, osx-x64.
- Run a quick sanity check (e.g., `--help` and a short download range).
