# PST Builder

[![CI](https://github.com/ml-ls/PST-Builder/actions/workflows/ci.yml/badge.svg)](https://github.com/ml-ls/PST-Builder/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A write-once .NET library that turns a mailbox backup (emails + contacts + calendar + tasks + notes)
into a single **Unicode (64-bit) PST** file that **Microsoft Outlook opens directly** — no repair
prompt, no import step.

> The library/namespace identifier is `PstBuilder` (assemblies `PstBuilder.dll`,
> `PstBuilder.Eml`, `PstBuilder.Pim`); "PST Builder" is the product name.

It builds PSTs **from scratch, one-way, once**. It never reads, edits, or mutates an existing PST.

> **New here?** Start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — a plain-English, no-jargon
> tour of how the whole thing works. Every source file also opens with an "In plain words:" line, so
> you can read the code top-down without knowing the PST format first.

## Status: validated in real Outlook ✅

Generated PSTs open and browse in **real desktop Microsoft Outlook** (Microsoft 365, Version
16.x) across the full size range — from a 3-message file up to a **3 GB single file (past the 2 GB
boundary)**. Folder tree, mail with **HTML bodies and openable attachments**, contacts, calendar
(including recurring appointments), tasks, and notes all render correctly. **70 automated tests** pass
(CRC vs. spec vectors, NBT/BBT/AMap/PMap/FMap/FPMap round-trips, PC/TC/data-tree/heap round-trips, full
store + EML/PIM wiring, streamed-vs-buffered attachment equivalence, plus a strict NDB+LTP re-reader
that re-validates generated files).

The only residual `scanpst` output is two **cosmetic advisory** lines (a message-size recompute and a
deprecated search-folder note) that Outlook itself recomputes on open — they do **not** block opening
or reading. See [Known cosmetic notes](#known-cosmetic-notes).

## Quick start

The one-line front door is `Pst`. To compose a small store in memory and write it:

```csharp
using PstBuilder;                // the Pst façade
using PstBuilder.Messaging;

Pst.Write("mailbox.pst", top =>
{
    var inbox = top.AddFolder("Inbox");
    inbox.AddMessage(new MessageItem { Subject = "Hello", Body = "Hi there", DisplayTo = "Me" });
    top.AddFolder("Projects").AddFolder("2026").AddMessage(new MessageItem { Subject = "Kickoff" });
});
```

For a large or streamed export, open a session (`Pst.Create` is a shortcut for
`PstExportSession.Create`) and push items — from as many threads as you like:

```csharp
using PstBuilder.Messaging;
using PstBuilder.Eml;   // .eml adapter
using PstBuilder.Pim;   // .vcf / .ics adapter

using var session = PstExportSession.Create("mailbox.pst");

// Add items from any number of producer threads:
session.AddEml("Inbox", emlBytes, receivedUtc);        // a raw RFC822 email
session.AddEml("Archive/2024", otherEmlBytes);
session.AddVcard("Contacts", vcardBytes);              // a contact
session.AddIcal("Calendar", icsBytes);                 // appointment (VEVENT) or task (VTODO)

session.Complete();   // the one, single-threaded PST write — seals the file
```

`Add*` methods are **thread-safe** — call them from any number of threads. They return immediately
unless the bounded queue is full, in which case they **block** (that's the backpressure). A single
background consumer applies items in order; the PST is written exactly once at `Complete()`.
`folderPath` uses `/` or `\` separators and auto-creates the folder tree. **Never call `Add*` after
`Complete()`.**

Prefer async? Every producer has an awaitable twin that awaits backpressure instead of blocking, plus
`CompleteAsync` — all with an optional `CancellationToken`:

```csharp
await session.AddEmlAsync("Inbox", emlBytes, receivedUtc, cancellationToken: ct);
await session.AddContactAsync("Contacts", contact, ct);
var result = await session.CompleteAsync(ct);
```

### Large attachments (streaming)

Attachments can be given by value (`byte[]`) or as a **stream read on demand** — so a multi-gigabyte
file never has to be loaded into a single array. The bytes flow to disk block by block:

```csharp
var msg = new MessageItem { Subject = "Quarterly report", DisplayTo = "Me" };

// From a file on disk (opened and read only at write time):
msg.Attachments.Add(AttachmentItem.FromFile(@"C:\reports\q3.pdf"));

// …or from any stream source you can reopen, with a known exact length:
msg.Attachments.Add(AttachmentItem.FromStream("export.bin", () => blobStore.OpenRead(id), length));

session.AddMessage("Inbox", msg);
```

The in-memory `Content = byte[]` form still works for small attachments; the streamed form produces
**byte-for-byte the same PST** — it just never buffers the whole payload.

### Progress reporting

Pass an `IProgress<ExportProgress>` to any `Create*`/`Pst.Create*` to receive periodic updates (every
`progressInterval` items, default 256) plus a final snapshot once the file is sealed:

```csharp
var progress = new Progress<ExportProgress>(p =>
    Console.WriteLine($"{p.ItemsWritten} items, {p.BytesWritten:N0} bytes (part {p.PartNumber})"));

using var session = PstExportSession.Create("mailbox.pst", progress: progress, progressInterval: 500);
```

## Consuming the library

PST Builder is a set of DLLs (a class library) — your application references it and calls its API. The
core is `PstBuilder.dll`, which **multi-targets `netstandard2.0` and `net8.0`**: the netstandard build
makes it usable from **.NET Framework 4.6.1+**, **.NET Core**, and **.NET 5–9**, while consumers on
.NET 8+ automatically pick the `net8.0` build (in-box `Span`/`Channels`, no compatibility packages). The
`.eml` and PIM adapters are separate optional DLLs that multi-target the same way.

Three ways to reference it:

```xml
<!-- 1. Project reference (source is in your solution) -->
<ItemGroup>
  <ProjectReference Include="..\PstBuilder\src\PstBuilder\PstBuilder.csproj" />
  <ProjectReference Include="..\PstBuilder\src\PstBuilder.Eml\PstBuilder.Eml.csproj" /> <!-- optional -->
</ItemGroup>
```

```bash
# 2. NuGet package (once published — the projects already set PackageId)
dotnet add package PstBuilder
dotnet add package PstBuilder.Eml     # optional .eml adapter
dotnet add package PstBuilder.Pim     # optional .vcf/.ics adapter
```

```xml
<!-- 3. Direct DLL reference (least maintainable) -->
<ItemGroup>
  <Reference Include="PstBuilder"><HintPath>libs\PstBuilder.dll</HintPath></Reference>
</ItemGroup>
```

Then use the API (namespaces mirror the assemblies: `PstBuilder.Messaging`, `PstBuilder.Eml`,
`PstBuilder.Pim`) — see [Quick start](#quick-start). The `samples/` projects are runnable EXEs that
demonstrate exactly this; the thing you ship is the DLL, not an EXE.

## Ingestion model — memory, concurrency, and folders

**Writes straight to disk; memory stays small.** `Create(path)` opens a real `FileStream`. As each item
is processed, its block bytes (message body, attachments) are written to that file **immediately** and
then released — nothing accumulates a copy of the mailbox in RAM. Only tiny bookkeeping is retained: a
few bytes of B-tree leaf entry per block/node, plus one lightweight row per message per folder. So
**peak memory scales with item _count_, not mailbox _size_** — a multi-GB PST builds in tens of MB of
RAM. (A 3 GB file builds fine with a queue of just 64 items.) The file is genuinely on disk as it grows,
not held in memory, which is why sizes past the ~2 GB in-memory-buffer limit work at all.

**Concurrent producers, single ordered writer.** `Add*` are thread-safe — hand items in from as many
producer threads as you like (e.g. a backup app reading its store in parallel). Items land on an
in-memory queue; **one** background thread dequeues them and writes to the single PST stream in order.
(The PST writer must be single-threaded; the queue is what makes concurrent producers safe.)

**Backpressure / throttling.** That queue is **bounded** (`queueCapacity`, default 1024 — pass a smaller
number to cap memory tighter). When it fills, `Add*` **blocks** (or, with the `…Async` variants,
**awaits**) until the writer drains an item, so fast producers are automatically paced to the speed of
the single sequential writer. Work can't pile up unbounded in RAM, and producers can't outrun the disk.
(This is backpressure — the OS/disk still write at their own rate; the queue simply stops items queuing
up ahead of the writer.) If the writer ever throws, the fault is captured and re-thrown from the next
`Add*`/`Complete()`.

**Folder structure is recreated from paths.** Every `Add*` takes a `folderPath` — a `/`- or
`\`-separated path under the mailbox root ("Top of Personal Folders"). The tree is built **on demand**:
`AddEml("Archive/2024/Q1", …)` creates `Archive → 2024 → Q1` if they don't exist and reuses them if they
do (folders are cached by path, so later items land in the same folder — no duplicates). A folder's
type/view (mail vs. contacts vs. calendar vs. tasks vs. notes) comes from the **container class of the
first item** added to it (`AddContact` ⇒ `IPF.Contact`, `AddAppointment` ⇒ `IPF.Appointment`, etc.). To
pre-create an empty folder or force its type up front, call `EnsureFolder(path, containerClass)`.

```csharp
session.EnsureFolder("Contacts", "IPF.Contact");         // optional: force the view before adding
session.AddEml("Inbox", eml1);
session.AddEml("Archive/2024/January", eml2);            // nested folders auto-created
session.AddEml("Archive/2024/January", eml3);            // reuses the same folder
```

## What it produces

| Item | How |
|------|-----|
| **Mail** | HTML + plain-text body, sender, recipients, dates, flags. `cid:` inline images stored as inline attachments (MHTML-ref). |
| **Attachments** | Real bytes stored in the message's subnode tree (`MSGFLAG_HASATTACH` set); `PidTagAttachSize` computed exactly as Outlook does, so attachments open in their apps. Supply by value (`byte[]`) or **streamed** (`AttachmentItem.FromFile`/`FromStream`) for large files. |
| **Contacts / Calendar / Tasks / Notes** | Built via **named properties** (property-set GUID + LID/name) with a real **Name-to-ID map**, so each folder shows its type-specific Outlook view. |
| **Recurring appointments** | Daily/weekly/monthly/yearly + interval + end rule serialized to `PidLidAppointmentRecur` (MS-OXOCAL). No per-instance exceptions yet. |
| **Large folders** | Thousands of messages per folder — the Table Context row matrix spills to a row-aligned subnode and the Row-ID index becomes a multi-level BTH. |

## Design

**Two-phase, write-once.** Items are streamed and staged as they arrive, accumulating small NBT/BBT
leaf entries as blocks are written; then a single bottom-up *finalize* pass packs the B-trees,
synthesizes the allocation maps, and patches the header. Nothing is ever read back or mutated.

**Memory-bounded.** Block bytes (bodies, attachments) are written straight to the output the moment an
item arrives and then released; only the tiny per-block/per-node B-tree leaf entries and per-folder
contents-table rows are retained. Peak memory scales with item **count**, not mailbox **size**.

**Deliberately narrowed format:**

- Unicode (64-bit) PST only — no ANSI.
- Unencoded data (`NDB_CRYPT_NONE`) — no permute/cyclic obfuscation.
- Append-only allocation — blocks/pages only grow forward; nothing is freed. The full allocation-map
  hierarchy (**AMap → PMap → FMap → FPMap**) is generated deterministically at the end.
- 64-bit-clean offset arithmetic throughout (verified past the 2³¹ boundary on a 3 GB file).

**Canonical reference:** Microsoft `[MS-PST]`.

## File size, splitting, and compression

- **Single files** are supported up to **~3.4 GB** (`PstConstants.MaxAMapRegions`). Larger single files
  are possible but would need one more allocation-map interval confirmed against Outlook (see the FPMap
  note in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Splitting:** `PstExportSession.CreateSplit(path, maxBytesPerFile)` rolls over to numbered parts
  (`name.pst`, `name-002.pst`, …) once a file reaches the threshold; each part is a standalone PST.
- **No compression.** The PST format has no general/whole-file compression — attachments and data are
  stored as-is (Outlook's own PSTs are the same). To shrink a PST at rest, compress the `.pst` file
  externally (e.g. zip/7-Zip) and decompress before opening.

## Layers (bottom-up)

1. **Foundation** (`src/PstBuilder/Foundation`) — NID/BID/BREF types, CRC, page signature,
   little-endian primitives, append-only output stream.
2. **NDB write engine** (`src/PstBuilder/Ndb`) — block writer/allocator, NBT+BBT packed serializer,
   allocation-map generator (AMap/PMap/FMap/FPMap), data-tree (XBLOCK/XXBLOCK), subnode SLBLOCK,
   header writer.
3. **LTP write engine** (`src/PstBuilder/Ltp`) — Heap-on-Node (single- and multi-block), BTH
   (multi-level), Property Context (PC), Table Context (TC) with in-heap and spilled row matrices.
4. **Messaging model** (`src/PstBuilder/Messaging`) — Store → Folder → Message → {Recipients,
   Attachments} graph, message-store/folder/skeleton builders, Name-to-ID map, EntryIDs.
5. **Adapters & driver** — `PstBuilder.Eml` (raw `.eml` via MimeKit, kept out of the core),
   `PstBuilder.Pim` (`.vcf`/`.ics`, no third-party deps), and `PstExportSession` (the push-based,
   thread-safe, memory-bounded, optionally-splitting ingestion pipeline).

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the plain-English version of each layer.

## Known cosmetic notes

Running Microsoft's `scanpst.exe` against a generated file reports two advisory (`!!`) lines that do
**not** prevent Outlook from opening or reading it:

- **`Contents Table … row doesn't match sub-object`** — Outlook/`scanpst` treat `PidTagMessageSize` as
  a *computed* property and recompute it from physical storage; that internal formula varies even across
  Outlook's own messages, so the cached value can't be made byte-identical. We store a realistic value
  (so the Outlook Size column is correct) but `scanpst` still recomputes.
- **`SAL missing entry (nid=2223)`** — deprecated search-folder bookkeeping. Our search nodes match a
  real Outlook store node-for-node; the residual is internal search state Outlook rebuilds on open.

`PidTagAttachSize`, by contrast, **is** reproduced exactly (Σ of the attachment's property value
bytes), so attachments are never flagged or dropped.

## Project layout

```
src/PstBuilder            core library        (netstandard2.0 + net8.0, MIT)
src/PstBuilder.Eml        .eml adapter        (netstandard2.0 + net8.0, MimeKit)
src/PstBuilder.Pim        .vcf/.ics adapter   (netstandard2.0 + net8.0, no deps)
tests/PstBuilder.Tests    xUnit tests         (net9.0)
samples/PstBuilder.Sample console sample      (net9.0)
samples/PstBuilder.LargeTest large-file / item-variance generator (net9.0)
```

Shared package metadata and the multi-target settings live in `Directory.Build.props` /
`Directory.Build.targets`; each library sets only its own `PackageId`/`Description`. `dotnet pack -c
Release` produces NuGet packages (with the README and a `.snupkg` symbol package) for the three
libraries; the tests/samples are marked non-packable.

## Build

```
dotnet build
dotnet test
```

## License

MIT. The implementation is written from scratch against Microsoft's public `[MS-PST]` specification;
no code from any third-party or differently-licensed PST project is included.
