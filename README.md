# PST Builder

[![CI](https://github.com/ml-ls/PST-Builder/actions/workflows/ci.yml/badge.svg)](https://github.com/ml-ls/PST-Builder/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**PST Builder turns a mailbox backup — emails, contacts, calendar, tasks, and notes — into a single
Unicode (64-bit) `.pst` file that Microsoft Outlook opens straight away.** No repair prompt, no import
wizard, none of the "this file needs to be checked for problems" dance.

It builds PSTs from scratch, one direction, one time: it writes a brand-new file and never reads, edits,
or mutates an existing one. Think *export*, not *sync*.

> Quick naming note: the product is **PST Builder**, but in code everything lives under `PstBuilder`
> (assemblies `PstBuilder.dll`, `PstBuilder.Eml`, `PstBuilder.Pim`).

> **First time here?** Pop open [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — a plain-English,
> no-jargon tour of how the whole thing fits together. Every source file also opens with an "In plain
> words:" line, so you can read the code top-down without learning the PST format first.

## Does it actually work? Yes — in real Outlook ✅

Generated PSTs open and browse in **real desktop Microsoft Outlook** (Microsoft 365, v16.x), the whole
way from a 3-message file up to a **3 GB single file — past the 2 GB boundary** that trips up naïve
writers. The folder tree, mail (with **HTML bodies and attachments that actually open**), contacts,
calendar (recurring appointments included), tasks, and notes all show up correctly.

Behind that, **70 automated tests** keep everything honest: CRCs checked against the spec's own vectors,
round-trips for every structure (NBT/BBT/AMap/PMap/FMap/FPMap, PC/TC/data-tree/heap), full store +
EML/PIM wiring, streamed-vs-buffered attachment equivalence, and a strict re-reader that re-parses the
files we generate and re-validates them from scratch.

The only thing `scanpst` still grumbles about is two **cosmetic advisory** lines (a message-size
recompute and a deprecated search-folder note) that Outlook quietly recomputes on open — neither blocks
opening or reading. The full story is in [Two harmless `scanpst` notes](#two-harmless-scanpst-notes).

## Quick start

The friendliest way in is the `Pst` front door. Want to build a small store in memory and write it out?
That's one call:

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

Got a big mailbox, or items trickling in as you go? Open a session instead (`Pst.Create` is just a
shortcut for `PstExportSession.Create`) and push items in — from as many threads as you like:

```csharp
using PstBuilder.Messaging;
using PstBuilder.Eml;   // .eml adapter
using PstBuilder.Pim;   // .vcf / .ics adapter

using var session = PstExportSession.Create("mailbox.pst");

// Hand items in from any number of producer threads:
session.AddEml("Inbox", emlBytes, receivedUtc);        // a raw RFC822 email
session.AddEml("Archive/2024", otherEmlBytes);
session.AddVcard("Contacts", vcardBytes);              // a contact
session.AddIcal("Calendar", icsBytes);                 // appointment (VEVENT) or task (VTODO)

session.Complete();   // the one, single-threaded PST write — seals the file
```

A few things worth knowing about those `Add*` calls:

- They're **thread-safe** — fire them from as many threads as you want.
- They return right away *unless* the internal queue is full, in which case they **block** for a moment
  (that's the backpressure quietly keeping memory in check).
- A single background worker applies everything in order, and the actual PST is written exactly once,
  when you call `Complete()`.
- `folderPath` takes `/` or `\` separators and builds the folder tree for you.
- The one rule: **don't call `Add*` after `Complete()`.**

Prefer async? Every producer has an awaitable twin that *awaits* backpressure instead of blocking, plus
`CompleteAsync` — all taking an optional `CancellationToken`:

```csharp
await session.AddEmlAsync("Inbox", emlBytes, receivedUtc, cancellationToken: ct);
await session.AddContactAsync("Contacts", contact, ct);
var result = await session.CompleteAsync(ct);
```

### Big attachments, without the big memory bill

Attachments can come in as a plain `byte[]`, or as a **stream we read on demand** — so a multi-gigabyte
file never has to sit in memory as one giant array. The bytes flow to disk block by block:

```csharp
var msg = new MessageItem { Subject = "Quarterly report", DisplayTo = "Me" };

// From a file on disk (opened and read only at write time):
msg.Attachments.Add(AttachmentItem.FromFile(@"C:\reports\q3.pdf"));

// …or from any stream source you can reopen, with a known exact length:
msg.Attachments.Add(AttachmentItem.FromStream("export.bin", () => blobStore.OpenRead(id), length));

session.AddMessage("Inbox", msg);
```

The in-memory `Content = byte[]` form is perfect for small attachments; the streamed form gives you a
**byte-for-byte identical PST** — it just never buffers the whole payload.

### Progress reporting

Want a progress bar? Hand any `Create*` / `Pst.Create*` an `IProgress<ExportProgress>` and it'll ping
you every `progressInterval` items (256 by default), plus one final snapshot once the file is sealed:

```csharp
var progress = new Progress<ExportProgress>(p =>
    Console.WriteLine($"{p.ItemsWritten} items, {p.BytesWritten:N0} bytes (part {p.PartNumber})"));

using var session = PstExportSession.Create("mailbox.pst", progress: progress, progressInterval: 500);
```

## Using the library

PST Builder is a class library — a set of DLLs your app references and calls. The heart of it is
`PstBuilder.dll`, which **multi-targets `netstandard2.0` and `net8.0`**. That netstandard build means you
can use it from **.NET Framework 4.6.1+**, **.NET Core**, and **.NET 5–9**; anyone on .NET 8+
automatically gets the `net8.0` build (in-box `Span`/`Channels`, no extra compatibility packages). The
`.eml` and PIM adapters are separate optional DLLs that target the same way.

There are three ways to pull it in — pick whichever suits you:

```xml
<!-- 1. Project reference — you've got the source in your solution -->
<ItemGroup>
  <ProjectReference Include="..\PstBuilder\src\PstBuilder\PstBuilder.csproj" />
  <ProjectReference Include="..\PstBuilder\src\PstBuilder.Eml\PstBuilder.Eml.csproj" /> <!-- optional -->
</ItemGroup>
```

```bash
# 2. NuGet — the usual choice
dotnet add package PstBuilder         # dependency-light core
dotnet add package PstBuilder.Eml     # optional .eml adapter (pulls in MimeKit)
dotnet add package PstBuilder.Pim     # optional .vcf/.ics adapter (no deps)

# …or grab the lot in one go:
dotnet add package PstBuilder.All     # core + both adapters, batteries included
```

```xml
<!-- 3. Direct DLL reference — works, but the least maintainable -->
<ItemGroup>
  <Reference Include="PstBuilder"><HintPath>libs\PstBuilder.dll</HintPath></Reference>
</ItemGroup>
```

From there it's just the API — the namespaces mirror the assemblies (`PstBuilder.Messaging`,
`PstBuilder.Eml`, `PstBuilder.Pim`), so head back to [Quick start](#quick-start). The `samples/`
projects are runnable EXEs that show all of this in action; just remember the thing *you* ship is the
DLL, not an EXE.

## How it handles memory, threads, and folders

This is the part people usually worry about with big mailboxes, so here's the straight story.

**It writes straight to disk — memory stays tiny.** `Create(path)` opens a real `FileStream`. As each
item is processed, its block bytes (message body, attachments) get written to that file **immediately**
and then released — nothing accumulates a copy of the mailbox in RAM. All that's kept is a little
bookkeeping: a few bytes of B-tree leaf entry per block/node, plus one lightweight row per message per
folder. So **peak memory scales with item _count_, not mailbox _size_** — a multi-GB PST builds in tens
of MB of RAM (a 3 GB file is happy with a queue of just 64 items). The file is genuinely on disk as it
grows rather than held in memory, which is exactly why sizes past the ~2 GB in-memory-buffer limit work
at all.

**Many producers, one careful writer.** The `Add*` methods are thread-safe, so you can hand items in
from as many producer threads as you like (say, a backup app reading its store in parallel). Items land
on an in-memory queue, and **one** background thread dequeues them and writes to the single PST stream in
order. (The PST writer has to be single-threaded; the queue is what makes concurrent producers safe.)

**Backpressure keeps things from piling up.** That queue is **bounded** (`queueCapacity`, default 1024 —
pass a smaller number to cap memory tighter). When it fills, `Add*` **blocks** (or, with the `…Async`
variants, **awaits**) until the writer drains an item, so fast producers get automatically paced to the
speed of the single sequential writer. Work can't balloon in RAM, and producers can't outrun the disk.
And if the writer ever throws, the fault is captured and re-thrown from the next `Add*` / `Complete()`,
so nothing fails silently.

**Folders are rebuilt from the paths you give.** Every `Add*` takes a `folderPath` — a `/`- or
`\`-separated path under the mailbox root ("Top of Personal Folders"). The tree is built **on demand**:
`AddEml("Archive/2024/Q1", …)` creates `Archive → 2024 → Q1` if they don't exist yet, and reuses them if
they do (folders are cached by path, so later items land in the same place — no duplicates). A folder's
type/view (mail vs. contacts vs. calendar vs. tasks vs. notes) comes from the **container class of the
first item** dropped into it (`AddContact` ⇒ `IPF.Contact`, `AddAppointment` ⇒ `IPF.Appointment`, and so
on). Want to pre-create an empty folder or lock in its type up front? Call
`EnsureFolder(path, containerClass)`.

```csharp
session.EnsureFolder("Contacts", "IPF.Contact");         // optional: force the view before adding
session.AddEml("Inbox", eml1);
session.AddEml("Archive/2024/January", eml2);            // nested folders auto-created
session.AddEml("Archive/2024/January", eml3);            // reuses the same folder
```

## Surviving disconnects and crashes

Long exports shouldn't fall apart if something hiccups. Two very different things can go wrong, so here's
how each is handled.

**Your source disconnects, but the process is alive** — say the backup feed or network you're pulling
from stalls. Nothing to do: a session has **no idle timeout**. The single writer just waits on an empty
queue, and when your source reconnects you carry on calling `Add*` exactly where you left off.

**The process itself crashes** — for that, checkpoint. A **resumable** session lets you seal the work so
far into a durable, standalone PST at any point with `Checkpoint()`; if the process then dies, you keep
every checkpointed part and lose only the items added since the last one. After a restart, `Resume`
reopens the set and continues in a fresh part, leaving the finished parts untouched — you just replay the
items your producer added since its last checkpoint.

```csharp
using var session = PstExportSession.CreateResumable("mailbox.pst");

foreach (var batch in source.ReadBatches())
{
    foreach (var eml in batch) session.AddEml("Inbox", eml);
    var part = session.Checkpoint();   // everything so far is now durable on disk
    source.MarkDone(batch);            // your producer records how far it got
}

session.Complete();
```

```csharp
// …after a crash and restart:
using var session = PstExportSession.Resume("mailbox.pst");   // continues at the next part
foreach (var eml in source.ReadSince(lastCheckpoint)) session.AddEml("Inbox", eml);
session.Complete();
```

A checkpointed export produces **numbered parts** (`mailbox.pst`, `mailbox-002.pst`, …), each a complete
PST — the same shape as [splitting](#file-size-splitting-and-compression). Each checkpoint is flushed all
the way to disk (a real `fsync`), so parts survive a power loss, not just a process crash; and a partial
trailing file left by the crash is detected and overwritten on resume, never mistaken for a finished part.

## What ends up in the PST

| Item | How |
|------|-----|
| **Mail** | HTML + plain-text body, sender, recipients, dates, flags. `cid:` inline images are stored as inline attachments (MHTML-ref). |
| **Attachments** | Real bytes stored in the message's subnode tree (`MSGFLAG_HASATTACH` set); `PidTagAttachSize` is computed exactly the way Outlook does, so attachments open in their apps. Supply by value (`byte[]`) or **streamed** (`AttachmentItem.FromFile`/`FromStream`) for large files. |
| **Contacts / Calendar / Tasks / Notes** | Built from **named properties** (property-set GUID + LID/name) with a real **Name-to-ID map**, so each folder gets its type-specific Outlook view. |
| **Recurring appointments** | Daily/weekly/monthly/yearly + interval + end rule, serialized to `PidLidAppointmentRecur` (MS-OXOCAL). No per-instance exceptions yet. |
| **Large folders** | Thousands of messages per folder — the Table Context row matrix spills to a row-aligned subnode and the Row-ID index becomes a multi-level BTH. |

## How it works under the hood

**Two phases, write-once.** Items are streamed and staged as they arrive, accumulating small NBT/BBT
leaf entries as blocks are written; then a single bottom-up *finalize* pass packs the B-trees,
synthesizes the allocation maps, and patches the header. Nothing is ever read back or mutated.

**Memory-bounded by design.** Block bytes (bodies, attachments) go straight to the output the moment an
item arrives and are then released; only the tiny per-block/per-node B-tree leaf entries and per-folder
contents-table rows stick around. Peak memory tracks item **count**, not mailbox **size**.

**A deliberately narrowed slice of the format:**

- Unicode (64-bit) PST only — no ANSI.
- Unencoded data (`NDB_CRYPT_NONE`) — no permute/cyclic obfuscation.
- Append-only allocation — blocks/pages only grow forward; nothing is freed. The full allocation-map
  hierarchy (**AMap → PMap → FMap → FPMap**) is generated deterministically at the very end.
- 64-bit-clean offset arithmetic throughout (verified past the 2³¹ boundary on a 3 GB file).

**Canonical reference:** Microsoft `[MS-PST]`.

## File size, splitting, and compression

- **Single files** are supported up to **~3.4 GB** (`PstConstants.MaxAMapRegions`). Bigger single files
  are possible but would need one more allocation-map interval confirmed against Outlook (see the FPMap
  note in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Splitting:** `PstExportSession.CreateSplit(path, maxBytesPerFile)` rolls over to numbered parts
  (`name.pst`, `name-002.pst`, …) once a file hits the threshold; each part is a standalone PST.
- **No compression.** The PST format has no general/whole-file compression — attachments and data are
  stored as-is (Outlook's own PSTs are the same). To shrink a PST at rest, zip the `.pst` file yourself
  (e.g. zip/7-Zip) and unzip it before opening.

## The layers, bottom to top

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

## Two harmless `scanpst` notes

Run Microsoft's `scanpst.exe` over a generated file and it prints two advisory (`!!`) lines. Neither
stops Outlook opening or reading the file:

- **`Contents Table … row doesn't match sub-object`** — Outlook and `scanpst` treat `PidTagMessageSize`
  as a *computed* property and recompute it from physical storage. That internal formula varies even
  across Outlook's own messages, so the cached value can't be made byte-identical. We store a realistic
  value (so the Outlook Size column looks right) and `scanpst` recomputes anyway.
- **`SAL missing entry (nid=2223)`** — deprecated search-folder bookkeeping. Our search nodes match a
  real Outlook store node-for-node; the leftover is internal search state that Outlook rebuilds on open.

`PidTagAttachSize`, on the other hand, **is** reproduced exactly (the Σ of the attachment's property
value bytes), so attachments are never flagged or dropped.

## Project layout

```
src/PstBuilder            core library        (netstandard2.0 + net8.0, MIT)
src/PstBuilder.Eml        .eml adapter        (netstandard2.0 + net8.0, MimeKit)
src/PstBuilder.Pim        .vcf/.ics adapter   (netstandard2.0 + net8.0, no deps)
src/PstBuilder.All        convenience metapackage (core + both adapters)
tests/PstBuilder.Tests    xUnit tests         (net9.0)
samples/PstBuilder.Sample console sample      (net9.0)
samples/PstBuilder.LargeTest large-file / item-variance generator (net9.0)
```

Shared package metadata and the multi-target settings live in `Directory.Build.props` /
`Directory.Build.targets`, so each project only sets its own `PackageId`/`Description`. `dotnet pack -c
Release` builds NuGet packages for the four packable projects (the three libraries also get a `.snupkg`
symbol package); the tests and samples are marked non-packable.

## Building it yourself

```
dotnet build
dotnet test
```

## License

MIT. The implementation is written from scratch against Microsoft's public `[MS-PST]` specification — no
code from any third-party or differently-licensed PST project is included.
