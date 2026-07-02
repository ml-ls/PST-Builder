using System;
using System.Diagnostics;
using System.IO;
using PstBuilder.Messaging;

// Generates a large (~200 MB) test PST file with a realistic mailbox structure so the output can be
// opened in Outlook to validate that all item types render correctly: mail with attachments, contacts,
// recurring appointments, tasks, and sticky notes.
//
// Usage: dotnet run --project samples/PstBuilder.LargeTest [output.pst]
//
// Target breakdown (~200 MB total attachment data, ~5 MB bodies/overhead):
//   Inbox          60 × 400 KB attachments  = 24 MB  + 140 plain HTML
//   Sent Items     40 × 500 KB attachments  = 20 MB  + 110 plain HTML
//   Archive/2024  100 × 500 KB attachments  = 50 MB  + 200 plain HTML
//   Archive/2025   80 × 450 KB attachments  = 36 MB  + 120 plain HTML
//   Projects/Alpha 50 × 600 KB attachments  = 30 MB  +  50 plain HTML
//   Projects/Beta  40 × 500 KB attachments  = 20 MB  +  40 plain HTML
//   Legal          20 × 500 KB attachments  = 10 MB  +  30 plain HTML
//   HR/Benefits    20 × 350 KB attachments  =  7 MB  +  30 plain HTML
//   HR/Payroll     15 × 200 KB attachments  =  3 MB  +  25 plain HTML
//   Personal        0 attachments                    +  60 plain HTML
//   Contacts, Calendar, Tasks, Notes — lightweight PIM items

// Modes:
//   (default)        → full ~200 MB dataset, split into ~20 MB parts
//   --tiny  [path]   → 3 plain messages, no attachments (smallest possible, same streaming path)
//   --small [path]   → a few folders + 1 medium attachment each + all PIM types (~a few MB)
//   --single [path]  → the full ~200 MB dataset as ONE unsplit file
//   --size <MB>      → one unsplit file grown to ~<MB>, with full item variance (for large / >2 GB tests)
bool tiny   = Array.IndexOf(args, "--tiny")   >= 0;
bool small  = Array.IndexOf(args, "--small")  >= 0;
bool single = Array.IndexOf(args, "--single") >= 0;
int sizeIdx = Array.IndexOf(args, "--size");
int sizeMb  = sizeIdx >= 0 && sizeIdx + 1 < args.Length ? int.Parse(args[sizeIdx + 1]) : 0;
string outPath = Array.Find(args, a => !a.StartsWith("--") && !int.TryParse(a, out _))
                 ?? (tiny ? "test-tiny.pst" : small ? "test-small.pst"
                     : single ? "test-single.pst"
                     : sizeMb > 0 ? $"test-{sizeMb}mb.pst" : "test-large.pst");

var app = new Generator(outPath);
if (tiny) app.RunTiny();
else if (small) app.RunSmall();
else if (single) app.RunSingle();
else if (sizeMb > 0) app.RunSize(sizeMb);
else app.Run();

internal sealed class Generator
{
    // ── content pools ─────────────────────────────────────────────────────────────
    private static readonly string[] Senders =
    {
        "Alice Chen|alice.chen@contoso.com",
        "Bob Martinez|bob.martinez@contoso.com",
        "Carol Williams|carol.williams@contoso.com",
        "David Kim|david.kim@contoso.com",
        "Emma Thompson|emma.thompson@contoso.com",
        "Frank Nguyen|frank.nguyen@contoso.com",
        "Grace Patel|grace.patel@contoso.com",
        "Henry Johansson|henry.johansson@contoso.com",
        "Iris Santos|iris.santos@contoso.com",
        "James O'Brien|james.obrien@contoso.com",
    };

    private static readonly string[] Subjects =
    {
        "Q4 Budget Review — action required",
        "Re: Project Alpha status update",
        "Fwd: Customer feedback summary",
        "Meeting notes: Architecture sync",
        "Action items from yesterday's stand-up",
        "Please review the attached proposal",
        "Quick question about the deployment schedule",
        "Team lunch next Friday — RSVP by Thursday",
        "Security patch notification — apply by EOD",
        "New hire orientation schedule",
        "Reminder: Performance reviews due",
        "Client demo materials v3",
        "Invoice #4872 for your approval",
        "FYI: Infrastructure maintenance window Sat 02:00",
        "RE: Contract renewal discussion",
        "Product roadmap for H1 2026",
        "Test environment credentials update",
        "Office supply order — please confirm",
        "Compliance training completion deadline",
        "Backup policy changes effective 1 Jan",
        "RE: Support ticket #92847",
        "Quarterly newsletter — internal edition",
        "Conference registration confirmation",
        "Updated org chart — please review",
        "Welcome to the team, Sarah!",
    };

    private static readonly (string Name, string Mime)[] AttachTypes =
    {
        ("Report_Q4_2025.pdf",       "application/pdf"),
        ("Project_Charter.docx",     "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ("Budget_FY2026.xlsx",       "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("Meeting_Notes.txt",        "text/plain"),
        ("Proposal_v2.docx",         "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ("Contract_Draft.pdf",       "application/pdf"),
        ("Q4_Metrics.csv",           "text/csv"),
        ("Data_Export_2025.xlsx",    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("Roadmap_H1_2026.pdf",      "application/pdf"),
        ("Onboarding_Pack.zip",      "application/zip"),
    };

    // Fictional mailbox owner — Contoso is Microsoft's reserved sample domain; not a real person.
    private const string MeName  = "Sam Rivera";
    private const string MeEmail = "sam.rivera@contoso.com";
    private static readonly DateTime BaseDate = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _outputPath;
    private int _seq;

    public Generator(string outputPath) => _outputPath = outputPath;

    public void Run()
    {
        Console.WriteLine("PST Builder large-file generator");
        Console.WriteLine($"  Output : {Path.GetFullPath(_outputPath)}");
        Console.WriteLine($"  Target : ~200 MB (this may take 30–90 s)");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        // Low queue capacity keeps peak RAM bounded: at most 50 large messages buffered at once.
        // Split at 20 MB/part: a single PST must stay under ~29 MB until FMap pages are emitted, and
        // splitting also exercises the rollover path. Produces test-large.pst, test-large-002.pst, …
        using var session = PstExportSession.CreateSplit(_outputPath, 20L * 1000 * 1000,
            "Sample Mailbox — Sam Rivera", queueCapacity: 50);

        //                               folder            total  w/att  attSz    unread
        Step("Inbox (200 msg)",      () => AddMail(session, "Inbox",          200,  60, 400_000, someUnread: true));
        Step("Sent Items (150 msg)", () => AddMail(session, "Sent Items",     150,  40, 500_000));
        Step("Archive/2024 (300)",   () => AddMail(session, "Archive/2024",   300, 100, 500_000));
        Step("Archive/2025 (200)",   () => AddMail(session, "Archive/2025",   200,  80, 450_000));
        Step("Projects/Alpha (100)", () => AddMail(session, "Projects/Alpha", 100,  50, 600_000));
        Step("Projects/Beta (80)",   () => AddMail(session, "Projects/Beta",   80,  40, 500_000));
        Step("Legal (50 msg)",       () => AddMail(session, "Legal",           50,  20, 500_000));
        Step("HR/Benefits (50 msg)", () => AddMail(session, "HR/Benefits",     50,  20, 350_000));
        Step("HR/Payroll (40 msg)",  () => AddMail(session, "HR/Payroll",      40,  15, 200_000));
        Step("Personal (60 msg)",    () => AddMail(session, "Personal",        60,   0,       0));
        Step("Contacts (60)",        () => AddContacts(session));
        Step("Calendar (50 events)", () => AddAppointments(session));
        Step("Tasks (20)",           () => AddTasks(session));
        Step("Notes (10)",           () => AddNotes(session));

        Console.WriteLine();
        Console.WriteLine("  Finalizing PST (writing B-trees and AMap pages)...");
        var result = session.Complete();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("── Result ──────────────────────────────────────────────────────");
        foreach (var part in result.Parts)
        {
            var info = new FileInfo(part.Name);
            Console.WriteLine($"  File    : {part.Name}");
            Console.WriteLine($"  Size    : {info.Length / 1_048_576.0:F1} MB  ({info.Length:N0} bytes)");
            Console.WriteLine($"  Mail    : {_seq:N0} messages  +  PIM items");
        }
        Console.WriteLine($"  Time    : {sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine();
        Console.WriteLine("Open the .pst in Outlook: File → Open & Export → Open Outlook Data File");
    }

    /// <summary>
    /// One UNSPLIT large file (~200 MB) — used to probe the FMap/FPMap size threshold. AMap/PMap pages
    /// already scale to any size; only FMap/FPMap are unimplemented, so scanpst on this file names the
    /// exact offset/ptype of the first map it wants. Requires PstConstants.MaxAMapRegions raised.
    /// </summary>
    public void RunSingle()
    {
        Console.WriteLine($"Generating SINGLE (unsplit) large PST → {Path.GetFullPath(_outputPath)}");
        var sw = Stopwatch.StartNew();
        using (var session = PstExportSession.Create(_outputPath, "Test Mailbox — Single", queueCapacity: 50))
        {
            Step("Inbox (200 msg)",      () => AddMail(session, "Inbox",          200,  60, 400_000, someUnread: true));
            Step("Sent Items (150 msg)", () => AddMail(session, "Sent Items",     150,  40, 500_000));
            Step("Archive/2024 (300)",   () => AddMail(session, "Archive/2024",   300, 100, 500_000));
            Step("Archive/2025 (200)",   () => AddMail(session, "Archive/2025",   200,  80, 450_000));
            Step("Projects/Alpha (100)", () => AddMail(session, "Projects/Alpha", 100,  50, 600_000));
            Step("Projects/Beta (80)",   () => AddMail(session, "Projects/Beta",   80,  40, 500_000));
            Step("Legal (50 msg)",       () => AddMail(session, "Legal",           50,  20, 500_000));
            Step("HR/Benefits (50 msg)", () => AddMail(session, "HR/Benefits",     50,  20, 350_000));
            Step("HR/Payroll (40 msg)",  () => AddMail(session, "HR/Payroll",      40,  15, 200_000));
            Step("Personal (60 msg)",    () => AddMail(session, "Personal",        60,   0,       0));
            Step("Contacts (60)",        () => AddContacts(session));
            Step("Calendar (50 events)", () => AddAppointments(session));
            Step("Tasks (20)",           () => AddTasks(session));
            Step("Notes (10)",           () => AddNotes(session));
            session.Complete();
        }
        sw.Stop();
        var info = new FileInfo(_outputPath);
        Console.WriteLine($"  Size: {info.Length / 1_048_576.0:F1} MB  ({info.Length:N0} bytes) in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// One UNSPLIT file grown to ~<paramref name="targetMb"/> MB with the full item variance (folders,
    /// contacts, calendar, tasks, notes, and mail with real attachments), for testing large / >2 GB PSTs.
    /// Adds the PIM set once, then streams attachment-bearing mail into Archive folders until the written
    /// size reaches the target.
    /// </summary>
    public void RunSize(int targetMb)
    {
        long target = (long)targetMb * 1024 * 1024;
        Console.WriteLine($"Generating {targetMb} MB single PST → {Path.GetFullPath(_outputPath)}");
        var sw = Stopwatch.StartNew();
        using (var session = PstExportSession.Create(_outputPath, $"Test Mailbox — {targetMb}MB", queueCapacity: 64))
        {
            // Full variance up front so the file exercises every item type.
            AddMail(session, "Inbox", 120, 40, 400_000, someUnread: true);
            AddMail(session, "Sent Items", 80, 20, 500_000);
            AddContacts(session);
            AddAppointments(session);
            AddTasks(session);
            AddNotes(session);

            // Bulk attachment-bearing mail across dated archive folders until we hit the target size.
            int year = 2015, batch = 0;
            while (session.Position < target)
            {
                string folder = $"Archive/{year + (batch % 10)}";
                AddMail(session, folder, 50, 50, 600_000); // 50 msgs, all with ~600 KB attachments
                batch++;
                if (batch % 4 == 0)
                    Console.WriteLine($"  … {session.Position / 1_048_576.0:F0} MB written");
            }
            session.Complete();
        }
        sw.Stop();
        var info = new FileInfo(_outputPath);
        Console.WriteLine($"  Size: {info.Length / 1_048_576.0:F1} MB  ({info.Length:N0} bytes)  in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>Smallest possible file: 3 plain messages, no attachments, no PIM. Isolates format from size.</summary>
    public void RunTiny()
    {
        Console.WriteLine($"Generating TINY test PST → {Path.GetFullPath(_outputPath)}");
        using (var session = PstExportSession.Create(_outputPath, "Test Mailbox — Tiny"))
        {
            for (int i = 0; i < 3; i++)
            {
                var m = new MessageItem
                {
                    Subject    = $"Plain test message {i + 1}",
                    Body       = $"This is plain message {i + 1}. No HTML, no attachments.",
                    SenderName = "Alice Chen",
                    SenderEmail = "alice.chen@contoso.com",
                    DisplayTo  = MeName,
                };
                m.Recipients.Add(new RecipientItem { DisplayName = MeName, EmailAddress = MeEmail });
                session.AddMessage("Inbox", m);
            }
            session.Complete();
        }
        Report();
    }

    /// <summary>A few MB: a couple of folders, one medium attachment, and every PIM type.</summary>
    public void RunSmall()
    {
        Console.WriteLine($"Generating SMALL test PST → {Path.GetFullPath(_outputPath)}");
        using (var session = PstExportSession.Create(_outputPath, "Test Mailbox — Small"))
        {
            AddMail(session, "Inbox", 10, 3, 300_000);
            AddMail(session, "Sent Items", 5, 0, 0);
            AddContacts(session);
            AddAppointments(session);
            AddTasks(session);
            AddNotes(session);
            session.Complete();
        }
        Report();
    }

    private void Report()
    {
        var info = new FileInfo(_outputPath);
        Console.WriteLine($"  Size: {info.Length / 1024.0:F0} KB  ({info.Length:N0} bytes)");
        Console.WriteLine("Open in Outlook: File → Open & Export → Open Outlook Data File");
    }

    private static void Step(string label, Action action)
    {
        Console.Write($"  {label,-28}...");
        action();
        Console.WriteLine(" done");
    }

    // ── bulk mail ─────────────────────────────────────────────────────────────────

    private void AddMail(PstExportSession s, string folder, int count, int attachCount,
                         int attachSize, bool someUnread = false)
    {
        for (int i = 0; i < count; i++, _seq++)
        {
            bool attach = i < attachCount;
            bool unread = someUnread && i % 4 != 0;
            s.AddMessage(folder, MakeMessage(_seq, attach, attachSize, unread));
        }
    }

    private static MessageItem MakeMessage(int seq, bool attach, int attachBytes, bool unread)
    {
        var parts    = Senders[seq % Senders.Length].Split('|');
        var sender   = parts[0];
        var senderEmail = parts[1];
        var subject  = Subjects[seq % Subjects.Length];
        var delivery = BaseDate.AddDays(seq % 365).AddHours(seq % 12).AddMinutes(seq % 60);

        var html = "<html><body style=\"font-family:Calibri,sans-serif;font-size:11pt\">" +
                   $"<p>Hi {MeName},</p>" +
                   $"<p>This is message {seq + 1:N0} in the validation dataset. Subject: <b>{subject}</b>.</p>" +
                   "<p>Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt " +
                   "ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco " +
                   "laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in " +
                   "voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat " +
                   "non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.</p>" +
                   "<p>Curabitur pretium tincidunt lacus. Nulla gravida orci a odio. Nullam varius, turpis molestie " +
                   "pretium placerat, arcu purus aliquam ante, et laoreet sapien justo vitae quam.</p>" +
                   $"<p>Best regards,<br/><b>{sender}</b><br/>{senderEmail}</p></body></html>";

        var m = new MessageItem
        {
            Subject                 = subject,
            Body                    = $"Hi {MeName},\r\n\r\nThis is message {seq + 1}.\r\nSubject: {subject}\r\n\r\nBest regards,\r\n{sender}",
            BodyHtml                = html,
            SenderName              = sender,
            SenderEmail             = senderEmail,
            DisplayTo               = MeName,
            Flags                   = unread ? 0 : PropertyTags.MsgFlagRead,
            DeliveryTimeUtc         = delivery,
            SubmitTimeUtc           = delivery,
            CreationTimeUtc         = delivery,
            LastModificationTimeUtc = delivery,
        };
        m.Recipients.Add(new RecipientItem { DisplayName = MeName, EmailAddress = MeEmail });

        if (attach && attachBytes > 0)
        {
            var (attachName, mime) = AttachTypes[seq % AttachTypes.Length];
            // Real, openable, deterministically-generated files (not random bytes) — see SampleAttachments.
            var blob = SampleAttachments.Create(attachName, attachBytes, seq);
            m.Attachments.Add(new AttachmentItem { FileName = attachName, MimeType = mime, Content = blob });
        }
        return m;
    }

    // ── contacts (60) ─────────────────────────────────────────────────────────────

    private static void AddContacts(PstExportSession s)
    {
        var companies = new[] { "Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Adventure Works", "Woodgrove Bank" };
        var titles    = new[] { "Senior Engineer", "Product Manager", "Director", "VP Engineering", "Analyst", "Consultant", "CTO" };

        for (int i = 0; i < 60; i++)
        {
            var parts     = Senders[i % Senders.Length].Split('|');
            var name      = parts[0];
            var nameParts = name.Split(' ');
            s.AddContact("Contacts", new ContactItem
            {
                DisplayName   = $"{name} ({i + 1})",
                GivenName     = nameParts[0],
                Surname       = nameParts.Length > 1 ? nameParts[1] : string.Empty,
                Company       = companies[i % companies.Length],
                JobTitle      = titles[i % titles.Length],
                Email         = $"{nameParts[0].ToLowerInvariant()}.{(nameParts.Length > 1 ? nameParts[1].ToLowerInvariant() : "user")}{i + 1}@example.com",
                BusinessPhone = $"+1 555 {100 + i:D3} {1000 + i * 7:D4}",
                Notes         = $"Contact #{i + 1} — {companies[i % companies.Length]}.",
            });
        }
    }

    // ── appointments (50: 10 recurring, 10 all-day) ───────────────────────────────

    private static void AddAppointments(PstExportSession s)
    {
        var locations = new[] { "Conference Room A", "Conference Room B", "Main Boardroom", "Teams (online)", "Client Site", "" };
        var apptSubj  = new[]
        {
            "Weekly team stand-up", "Project Alpha review", "1:1 with manager",
            "Customer demo", "Architecture planning", "Sprint retrospective",
            "Quarterly business review", "All-hands meeting", "Design review",
            "Code review session", "Release planning", "Incident post-mortem",
        };

        for (int i = 0; i < 40; i++)
        {
            var start = BaseDate.AddDays(i * 5).AddHours(9 + i % 4);
            var end   = start.AddHours(1);

            Recurrence? recur = null;
            if (i % 4 == 0)
            {
                recur = new Recurrence
                {
                    Frequency = i % 8 == 0 ? RecurrenceFrequency.Daily : RecurrenceFrequency.Weekly,
                    Interval  = 1,
                    End       = RecurrenceEnd.AfterCount,
                    Count     = 10,
                    Days      = i % 8 == 0 ? RecurrenceDays.None : RecurrenceDays.Monday | RecurrenceDays.Wednesday,
                };
            }

            s.AddAppointment("Calendar", new AppointmentItem
            {
                Subject    = apptSubj[i % apptSubj.Length],
                Location   = locations[i % locations.Length],
                Body       = "Agenda: 1. Status  2. Blockers  3. Next steps",
                StartUtc   = start,
                EndUtc     = end,
                BusyStatus = i % 3 == 0 ? 1 : 2,
                Recurrence = recur,
            });
        }

        // all-day events
        for (int i = 0; i < 10; i++)
        {
            s.AddAppointment("Calendar", new AppointmentItem
            {
                Subject    = $"Company holiday: {BaseDate.AddDays(i * 30):MMMM d}",
                Body       = "Office closed.",
                StartUtc   = BaseDate.AddDays(i * 30),
                EndUtc     = BaseDate.AddDays(i * 30 + 1),
                AllDay     = true,
                BusyStatus = 3,
            });
        }
    }

    // ── tasks (20) ────────────────────────────────────────────────────────────────

    private static void AddTasks(PstExportSession s)
    {
        var titles = new[]
        {
            "Review Q4 budget proposal", "Update architecture documentation", "Fix auth regression in prod",
            "Prepare quarterly report", "Migrate legacy service to cloud", "Onboard new team members",
            "Complete compliance training", "Refactor data access layer", "Write unit tests for module X",
            "Resolve customer escalation #4821", "Deploy hotfix to staging", "Conduct performance review",
            "Draft product roadmap 2026", "Set up CI/CD for new repo", "Archive old project files",
            "Renew SSL certificates", "Update dependency versions", "Document API endpoints",
            "Evaluate new monitoring tools", "Plan office move logistics",
        };
        var statuses = new[] { 0, 1, 1, 2, 3, 4 };
        for (int i = 0; i < titles.Length; i++)
        {
            var status = statuses[i % statuses.Length];
            s.AddTask("Tasks", new TaskItem
            {
                Subject         = titles[i],
                Body            = $"Priority: {(i % 3 == 0 ? "High" : i % 3 == 1 ? "Medium" : "Low")}\n\nDetails: {titles[i]}.",
                Status          = status,
                PercentComplete = status == 2 ? 1.0 : status == 1 ? 0.5 : 0.0,
                Complete        = status == 2,
                DueUtc          = BaseDate.AddDays(7 + i * 3),
            });
        }
    }

    // ── notes (10) ────────────────────────────────────────────────────────────────

    private static void AddNotes(PstExportSession s)
    {
        var notes = new[]
        {
            ("Remember",          "Call the vendor back about the SLA extension.",                    0),
            ("API design",        "Use cursor-based pagination — offset breaks on concurrent deletes.", 1),
            ("Deploy checklist",  "1. Drain old instances\n2. Migrate DB\n3. Roll containers\n4. Smoke test", 3),
            ("Meeting takeaway",  "Dashboard wanted by end of month — loop in design team Monday.",   2),
            ("Phone number",      "IT helpdesk direct: 555-0199. Bypass queue for P1 incidents.",    4),
            ("Book rec",          "\"Designing Data-Intensive Applications\" — finish chapter 7.",   1),
            ("Project idea",      "Auto-generate changelog from conventional commits via CI.",        3),
            ("Expense reminder",  "Claims must be submitted before the 5th of each month.",          0),
            ("Lunch spots",       "Try the new sandwich place on 2nd; the soup place is closed Mondays.", 2),
            ("Tech idea",         "Stream a checksum sidecar alongside the PST for fast validation.", 4),
        };
        foreach (var (subj, body, color) in notes)
            s.AddNote("Notes", new NoteItem { Subject = subj, Body = body, Color = color });
    }
}
