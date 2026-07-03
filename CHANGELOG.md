# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **A single-file (`Create(path)` / `Pst.Create`) export no longer fails when it would exceed the ~3.4 GB
  single-file limit** — it now rolls into numbered parts (`name.pst`, `name-002.pst`, …) automatically,
  the same shape as `CreateSplit`. `CreateSplit` thresholds are likewise clamped just below the ceiling.
  Only a caller-owned output stream (`Create(PstOutputStream)`) can still hit the hard limit.

### Fixed
- Corrected the stale over-limit error message (it referenced "~485 MB" and claimed FPMap pages weren't
  emitted; the real limit is ~3.4 GB and FPMap is emitted).

## [1.1.0] - 2026-07-02

### Added
- **Crash/disconnect resilience for `PstExportSession`.** A producer can now pause indefinitely (e.g. a
  source disconnect) and resume — there was never an idle timeout, and it's now covered by a test.
- **Checkpointing & resume.** New `CreateResumable(path)` + `Checkpoint()` / `CheckpointAsync()` seal the
  work so far into a durable, standalone PST part (flushed to disk with a real `fsync`) mid-export, so a
  later crash costs only the items added since the last checkpoint. `Resume(path)` reopens the set after a
  restart, continues in a fresh numbered part, and overwrites any partial trailing file left by the crash.
  `Checkpoint()` also works on `CreateSplit` sessions.

## [1.0.1] - 2026-07-02

### Added
- **`PstBuilder.All`** convenience metapackage — installs the core plus both source adapters
  (`PstBuilder.Eml`, `PstBuilder.Pim`) in one reference.

### Fixed
- Corrected the NuGet `PackageProjectUrl` / `RepositoryUrl` to the real repository
  (`github.com/ml-ls/PST-Builder`); the 1.0.0 packages carried a placeholder URL.

## [1.0.0] - 2026-07-02

First public release. A write-once .NET library that builds a Unicode (64-bit) PST from a mailbox
backup (emails, contacts, calendar, tasks, notes) that **Microsoft Outlook opens directly** — no repair
prompt, no import step. It never reads or mutates an existing PST.

### Added
- **NDB write engine** — block writer/allocator, packed NBT+BBT serializer, the full allocation-map
  hierarchy (AMap/PMap/FMap/FPMap), data-trees (XBLOCK/XXBLOCK), subnode SLBLOCKs, and the header writer,
  all streamed append-only with 64-bit-clean offsets.
- **LTP write engine** — Heap-on-Node (single- and multi-block), multi-level BTH, Property Context (PC),
  and Table Context (TC) with in-heap and row-aligned spilled row matrices.
- **Messaging model** — Store → Folder → Message → {Recipients, Attachments} graph, the store/folder/
  skeleton builders, the Name-to-ID map, and EntryIDs. Produces mail (plain + HTML bodies), contacts,
  calendar (including recurring appointments), tasks, and notes.
- **`PstExportSession`** — a push-based, thread-safe, memory-bounded ingestion pipeline with backpressure
  (bounded queue), a single ordered writer, and optional file **splitting** by size. Sync and `async`
  surfaces over one bounded `Channel<T>`.
- **Streaming attachments** — supply attachment content by value (`byte[]`) or as a stream read on demand
  (`AttachmentItem.FromFile` / `FromStream`), so multi-gigabyte files never sit in memory; produces
  byte-for-byte the same PST as the in-memory path.
- **Progress reporting** — optional `IProgress<ExportProgress>` on every session factory.
- **`Pst` façade** — one-line entry points: `Pst.Write(path, top => …)` for an in-memory tree and
  `Pst.Create` / `Pst.CreateSplit` for streaming sessions.
- **Source adapters** — `PstBuilder.Eml` (RFC822/MIME `.eml`, via MimeKit) and `PstBuilder.Pim`
  (`.vcf` / `.ics`, no third-party dependencies).
- **Packaging** — the three libraries multi-target `netstandard2.0` and `net8.0` and pack to NuGet
  (README + symbols included).

### Validated
- Opens and browses in **real desktop Microsoft Outlook (Microsoft 365, 16.x)** across the full size
  range — from a 3-message file up to a **3 GB single file (past the 2 GB boundary)**.
- 70 automated tests (CRC vs. spec vectors, NBT/BBT/AMap/PMap/FMap/FPMap round-trips, PC/TC/data-tree/
  heap round-trips, streamed-vs-buffered attachment equivalence, full store + EML/PIM wiring, plus a
  strict NDB+LTP re-reader that re-validates generated files).

### Known limitations
- Per-instance recurrence exceptions (e.g. "move just this one occurrence") are not yet serialized;
  recurrence **rules** are.
- The PST format has no whole-file compression (Outlook's own PSTs are the same).
- Two cosmetic `scanpst` advisory notes remain (a `PidTagMessageSize` recompute and a deprecated
  search-folder note); neither prevents Outlook from opening or reading the file.

[1.1.0]: https://github.com/ml-ls/PST-Builder/releases/tag/v1.1.0
[1.0.1]: https://github.com/ml-ls/PST-Builder/releases/tag/v1.0.1
[1.0.0]: https://github.com/ml-ls/PST-Builder/releases/tag/v1.0.0
