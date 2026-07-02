using System;
using System.Text;
using PstBuilder.Messaging;

namespace PstBuilder.Pim
{
    /// <summary>
    /// In plain words: reads a contact-card file (.vcf) and fills in one of our contacts.
    /// Maps a vCard (.vcf) to a <see cref="ContactItem"/>. Covers the common fields (FN, N, ORG, TITLE,
    /// EMAIL, TEL, NOTE) of vCard 2.1/3.0/4.0; uncommon/extension properties are ignored.
    /// </summary>
    public static class VcardMapper
    {
        /// <summary>Parses the first VCARD in a byte buffer.</summary>
        public static ContactItem ToContactItem(byte[] vcard)
        {
            if (vcard == null) throw new ArgumentNullException(nameof(vcard));
            return ToContactItem(DecodeText(vcard));
        }

        /// <summary>Parses the first VCARD in a string.</summary>
        public static ContactItem ToContactItem(string vcard)
        {
            var c = new ContactItem();
            string? formatted = null, family = null, given = null;

            foreach (var line in ContentLine.Parse(vcard))
            {
                switch (line.Name)
                {
                    case "FN": formatted = line.Value; break;
                    case "N":
                        var parts = line.Value.Split(';');
                        family = Get(parts, 0);
                        given = Get(parts, 1);
                        break;
                    case "ORG": c.Company = line.Value.Split(';')[0]; break;
                    case "TITLE": c.JobTitle = line.Value; break;
                    case "NOTE": c.Notes = line.Value; break;
                    case "EMAIL": if (string.IsNullOrEmpty(c.Email)) c.Email = line.Value; break;
                    case "TEL":
                        if (line.TypeContains("CELL")) c.MobilePhone ??= line.Value;
                        else if (line.TypeContains("WORK")) c.BusinessPhone ??= line.Value;
                        else c.BusinessPhone ??= line.Value;
                        break;
                    case "END": goto done; // first card only
                }
            }
            done:
            c.GivenName = given;
            c.Surname = family;
            c.DisplayName = !string.IsNullOrEmpty(formatted)
                ? formatted!
                : string.Join(" ", given, family).Trim();
            return c;
        }

        private static string? Get(string[] arr, int i) => i < arr.Length && arr[i].Length > 0 ? arr[i] : null;

        private static string DecodeText(byte[] bytes)
        {
            // vCard/iCal are UTF-8 by default; tolerate a UTF-8 BOM.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
