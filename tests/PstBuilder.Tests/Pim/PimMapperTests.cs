using System;
using System.Text;
using PstBuilder.Pim;
using Xunit;

namespace PstBuilder.Tests.Pim
{
    public class PimMapperTests
    {
        [Fact]
        public void Vcard_ParsesCommonFields()
        {
            string vcf =
                "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
                "FN:Jane Q. Doe\r\n" +
                "N:Doe;Jane;Q;;\r\n" +
                "ORG:Acme Corp;Engineering\r\n" +
                "TITLE:Principal Engineer\r\n" +
                "EMAIL;TYPE=INTERNET:jane@example.com\r\n" +
                "TEL;TYPE=WORK,VOICE:+1 555 0100\r\n" +
                "TEL;TYPE=CELL:+1 555 0200\r\n" +
                "NOTE:Met at the conference.\r\n" +
                "END:VCARD\r\n";

            var c = VcardMapper.ToContactItem(Encoding.UTF8.GetBytes(vcf));
            Assert.Equal("Jane Q. Doe", c.DisplayName);
            Assert.Equal("Jane", c.GivenName);
            Assert.Equal("Doe", c.Surname);
            Assert.Equal("Acme Corp", c.Company);
            Assert.Equal("Principal Engineer", c.JobTitle);
            Assert.Equal("jane@example.com", c.Email);
            Assert.Equal("+1 555 0100", c.BusinessPhone);
            Assert.Equal("+1 555 0200", c.MobilePhone);
            Assert.Equal("Met at the conference.", c.Notes);
        }

        [Fact]
        public void Vcard_UnfoldsLongLines()
        {
            string vcf =
                "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
                "NOTE:This is a long note that has been fol\r\n ded across two lines.\r\n" +
                "FN:Folded Name\r\nEND:VCARD\r\n";
            var c = VcardMapper.ToContactItem(Encoding.UTF8.GetBytes(vcf));
            Assert.Equal("This is a long note that has been folded across two lines.", c.Notes);
        }

        [Fact]
        public void Ical_Vevent_ParsesAppointment()
        {
            string ics =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n" +
                "SUMMARY:Project sync\r\nLOCATION:Room A\r\n" +
                "DTSTART:20260626T150000Z\r\nDTEND:20260626T160000Z\r\n" +
                "DESCRIPTION:Weekly status.\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

            var a = IcalMapper.ToAppointmentItem(Encoding.UTF8.GetBytes(ics));
            Assert.Equal("Project sync", a.Subject);
            Assert.Equal("Room A", a.Location);
            Assert.Equal(new DateTime(2026, 6, 26, 15, 0, 0, DateTimeKind.Utc), a.StartUtc);
            Assert.Equal(new DateTime(2026, 6, 26, 16, 0, 0, DateTimeKind.Utc), a.EndUtc);
            Assert.False(a.AllDay);
        }

        [Fact]
        public void Ical_AllDay_AndVtodo()
        {
            string allDay =
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261225\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var a = IcalMapper.ToAppointmentItem(Encoding.UTF8.GetBytes(allDay));
            Assert.True(a.AllDay);
            Assert.Equal(new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc), a.StartUtc);

            string todo =
                "BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nSUMMARY:Ship it\r\nDUE:20260701T120000Z\r\nPERCENT-COMPLETE:50\r\nSTATUS:IN-PROCESS\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
            Assert.True(IcalMapper.IsTask(todo));
            var t = IcalMapper.ToTaskItem(Encoding.UTF8.GetBytes(todo));
            Assert.Equal("Ship it", t.Subject);
            Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), t.DueUtc);
            Assert.Equal(0.5, t.PercentComplete);
            Assert.Equal(1, t.Status);
        }
    }
}
