# Contributing to PST Builder

Thanks for your interest in improving PST Builder. This document covers how to build, test, and submit
changes.

## Prerequisites

- **.NET SDK 9.0+** (the SDK builds the `netstandard2.0` and `net8.0` library targets and the `net9.0`
  test/sample projects). Older SDKs may work but CI runs on 9.0.
- No other tooling is required — the core library has no third-party dependencies; the `.eml` adapter
  pulls in MimeKit via NuGet automatically.

## Build and test

```bash
dotnet build -c Release
dotnet test  -c Release      # 70 tests should pass
```

To produce NuGet packages locally:

```bash
dotnet pack src/PstBuilder/PstBuilder.csproj -c Release -o ./artifacts
dotnet pack src/PstBuilder.Eml/PstBuilder.Eml.csproj -c Release -o ./artifacts
dotnet pack src/PstBuilder.Pim/PstBuilder.Pim.csproj -c Release -o ./artifacts
```

### Validating a generated PST

Set `PST_VALIDATE_FILE` to a `.pst` path and run the opt-in validator, which walks the file with the
strict round-trip reader (header CRCs, NBT/BBT signatures + CRCs, every block, every AMap region):

```bash
PST_VALIDATE_FILE=/path/to/file.pst dotnet test --filter FullyQualifiedName~ValidateExternalFileTests
```

The gold standard is still opening the file in real Microsoft Outlook — `scanpst` is stricter than
Outlook's own loader and reports cosmetic recompute advisories even on files Outlook mounts cleanly (see
the README's "Known cosmetic notes").

## Project layout

```
src/PstBuilder            core library        (netstandard2.0 + net8.0)
src/PstBuilder.Eml        .eml adapter        (MimeKit)
src/PstBuilder.Pim        .vcf/.ics adapter   (no deps)
tests/PstBuilder.Tests    xUnit tests
samples/*                 runnable console demos
docs/ARCHITECTURE.md      plain-English tour of every layer
```

The core is organized bottom-up by layer: `Foundation` → `Ndb` → `Ltp` → `Messaging`. Read
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first — it explains the whole design without assuming you
know the PST format.

## Coding conventions

- **Match the surrounding code.** Naming, spacing, and comment density should be indistinguishable from
  the file you're editing.
- **Every public type opens with an "In plain words:" lead** — a one-line, jargon-free explanation of
  what it is, before the technical summary. This is a hard rule for the public surface.
- **Cite the spec.** When implementing a structure, reference the relevant `[MS-PST]` (or `[MS-OXOCAL]`,
  etc.) section in a comment. Magic numbers should be named constants or carry an explaining comment.
- **Keep the core dependency-free.** MIME/vCard/iCal parsing lives in the adapter projects so the core
  stays format-agnostic. Do not add third-party PST-format code — the implementation is written from
  scratch against the public Microsoft spec and stays MIT-clean.
- **Docs are part of "done."** Update `docs/ARCHITECTURE.md` when you change a layer, and the README when
  you change the public surface.

## Tests

Every change to the write path should come with a round-trip test. The suite includes a strict re-reader
(`NdbRoundTripReader` + `LtpValidator`) that re-parses generated files and asserts structural
invariants; prefer adding assertions there over ad-hoc checks. New public API needs at least one test
that exercises it end-to-end (build → re-read → assert).

## Submitting changes

1. Branch off `main`.
2. Keep commits focused; write clear messages explaining the *why*.
3. Ensure `dotnet build` and `dotnet test` are green (CI runs both on every push and PR).
4. Open a pull request describing the change and how you validated it.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
