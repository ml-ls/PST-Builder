# How PST Builder works (explained simply)

This project makes a **`.pst` file** — the kind of file Microsoft Outlook can open. Think of a `.pst`
as a **big toy box** that holds a person's mailbox: their letters (emails), address cards (contacts),
calendar notes (appointments), to‑do lists (tasks), and sticky notes.

We only ever **build a new box from scratch and write it once**. We never open someone else's box or
change it. Writing once (and never going back to fix things) is what keeps the whole job simple.

The box has a very picky shape that Outlook insists on. We build that shape in **layers**, like
stacking blocks: each layer only knows how to do its own small job and trusts the layer below it.

---

## The layers (bottom to top)

### Layer 1 — Foundation (the alphabet)
The tiniest building pieces, like letters before you can write words.
- **ID numbers** so every box and every thing inside has a unique name tag (`Nid`, `Bid`, `Bref`).
- A **checksum** (`Crc`) — a little number that means "this page wasn't torn." If even one byte
  changes, the number won't match, and Outlook knows the box is broken.
- A **signature** — a secret stamp proving a page is sitting in the right spot.
- Helpers to write numbers down in the exact order the box expects (`SpanWriter`), and a notebook we
  only ever add to the end of, never erasing (`PstOutputStream`).

### Layer 2 — NDB (boxes and phone books)
This layer chops everything into **fixed‑size boxes called blocks** (up to ~8 KB each) and writes them
into the file. To find anything again, it keeps two **phone books** (sorted trees):
- the **BBT** finds a box by its box‑number,
- the **NBT** finds a *thing* (a node) by its thing‑number and tells you which boxes hold it.

It also draws **maps** marking which shelf spots are taken, and writes the **front cover** of the box
(the header) last, once it knows where everything ended up. If one thing is too big for a box, a
**data‑tree** splits it across several boxes.

Outlook insists on a whole little **family of maps**, each summarizing the one below it so a huge file
can find free space quickly. We draw all of them at their exact required spots: the **AMap** (which
64‑byte slots are used), the **PMap** and **FMap** (summaries every ~2 MB / ~120 MB), and — only once a
file passes ~2 GB — the **FPMap**. Get one wrong or missing and Outlook refuses the box, so their
positions and contents are pinned down precisely (and re‑checked by our own reader).

### Layer 3 — LTP (bags and tables)
Layer 2 only sees raw bytes. Layer 3 gives the bytes *meaning* using two shapes, both built on a little
**scratch pad called a Heap‑on‑Node (HN)**:
- a **Property Context (PC)** = a **labelled bag**: "DisplayName → Bob", "Size → 1200". Perfect for one
  thing's details.
- a **Table Context (TC)** = a **spreadsheet**: rows and columns. Perfect for a list, like all the
  emails in a folder.

A **BTH** is a tiny phone book *inside* the scratch pad so you can find a label or row quickly. When a
bag or table gets big, parts spill into side‑boxes (subnodes) or extra scratch‑pad pages.

### Layer 4 — Messaging (real things)
This turns actual mailbox things into bags and tables:
- a **Store** (the box's own label),
- **Folders** (each = one bag for its details + spreadsheets for "what's inside" and "sub‑folders"),
- **Messages** (a bag of properties + side‑boxes for recipients and attachments),
- and the **Name‑to‑ID map**, a dictionary for special "named" fields that contacts/calendar/tasks use.

### Layer 5 — Adapters & the conveyor belt
The doors you actually pour data through:
- **PstBuilder.Eml** reads raw email files (`.eml`).
- **PstBuilder.Pim** reads contact cards (`.vcf`) and calendar files (`.ics`).
- **`PstExportSession`** is a **conveyor belt**: many helpers can hand items in at the same time, but a
  single careful worker writes them into the box one at a time. It can **split** into several boxes once
  one gets too big — and even a plain single-file export rolls into numbered boxes on its own rather than
  failing if it would blow past the ~3.4 GB per-file limit — and it hands back **progress** tickets
  (`IProgress<ExportProgress>`) as it goes. A producer can pause indefinitely (a disconnect) and resume —
  there's no timeout — and a **resumable** session can `Checkpoint()` to seal a finished box mid-run so a
  crash only loses the current one; `Resume` picks up in a fresh box afterwards.
- **`Pst`** is the **front door**: one short line to either compose a folder tree in memory and write it
  (`Pst.Write`) or open a streaming session (`Pst.Create`).

A big attachment can be handed in as a **stream** (`AttachmentItem.FromFile`/`FromStream`) instead of a
`byte[]`; the worker reads it a block at a time straight to the box, so even a multi‑gigabyte file never
sits in memory all at once.

---

## Two rules that make it all work
1. **Write once, in order.** We figure out where everything goes, write it front‑to‑back, and only go
   back to fill in the cover and the maps at the very end.
2. **Keep memory small.** Big things (email bodies, attachments) are written to the file the moment they
   arrive and then forgotten. We only keep tiny notes (the phone‑book entries) in memory, so even a huge
   mailbox doesn't fill up the computer's memory.

## How to use it (the short version)
```csharp
// One-liner for an in-memory tree:
Pst.Write("mailbox.pst", top => top.AddFolder("Inbox").AddMessage(
    new MessageItem { Subject = "Hello" }));

// Or a streaming session for larger exports:
using var session = PstExportSession.Create("mailbox.pst");
session.AddEml("Inbox", emailBytes);          // an email
session.AddVcard("Contacts", vcardBytes);     // a contact
session.AddIcal("Calendar", icsBytes);        // an appointment or task
session.Complete();                           // seal the box
```

## What's solid vs. still rough
- **Solid & checked in real Microsoft Outlook:** the box opens and browses with no "needs repair"
  warning, from a 3‑message file all the way up to a **3 GB file** (past the 2 GB mark). Folders, emails
  (with HTML bodies and **openable attachments**), contacts, calendar, recurring appointments, tasks,
  notes, big folders (thousands of items), and split files all read back correctly. Every layer also has
  automated round‑trip tests, plus a strict reader that re‑checks generated files.
- **Two harmless leftovers:** Microsoft's repair tool still prints two advisory notes — one about a
  message‑*size* number it likes to recompute, and one about deprecated search bookkeeping. Neither
  stops Outlook opening or reading the file (Outlook simply recomputes them). Attachment sizes, by
  contrast, are reproduced exactly, so attachments are never flagged.
- **No shrinking:** the `.pst` format has no built‑in compression, so a box is about as big as the
  stuff you put in it. To make it smaller on disk, zip the `.pst` file yourself and unzip before opening.
- **Packaged:** the three libraries multi‑target `netstandard2.0` and `net8.0` and `dotnet pack` builds
  ready‑to‑publish NuGet packages (README + symbols included).
- **Not done yet:** per‑instance changes to a recurring series (e.g. "move just this one meeting").
