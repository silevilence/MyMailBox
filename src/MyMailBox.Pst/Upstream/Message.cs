// Copyright (c) 2016,2019, Dijji, and released under Ms-PL.  This can be found in the root of this distribution.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace XstReader
{
    // Holds information about a single message, extracted from the xst tables

    public class Message
    {
        public Folder Folder { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Subject { get; set; }
        internal MessageFlags Flags { get; set; }
        public DateTime? Received { get; set; }
        public DateTime? Submitted { get; set; }
        public DateTime? Modified { get; set; }  // When any attachment was last modified
        public DateTime? Date { get { return Received ?? Submitted; } }
        public string DisplayDate { get { return Date != null ? ((DateTime)Date).ToString("g") : "<unknown>"; } }
        internal NID Nid { get; set; }
        internal BodyType NativeBody { get; set; }
        public string Body { get; set; }
        public string BodyHtml { get; set; }
        public byte[] Html { get; set; }
        public byte[] RtfCompressed { get; set; }
        public List<Attachment> Attachments { get; private set; } = new List<Attachment>();
        public List<Recipient> Recipients { get; private set; } = new List<Recipient>();
        public List<Property> Properties { get; private set; } = new List<Property>();
        public bool MayHaveInlineAttachment { get { return (Attachments.FirstOrDefault(a => a.HasContentId) != null); } }
        public bool IsEncryptedOrSigned { get { return (GetBodyAsHtmlString() == null && Attachments.Count() == 1 && Attachments[0].FileName == "smime.p7m"); } }
        public bool HasAttachment { get { return (Flags & MessageFlags.mfHasAttach) == MessageFlags.mfHasAttach; } }
        public bool HasFileAttachment { get { return (Attachments.FirstOrDefault(a => a.IsFile) != null); } }
        public bool HasVisibleFileAttachment { get { return (Attachments.FirstOrDefault(a => a.IsFile && !a.Hide) != null); } }
        public void ClearContents()
        {
            // Clear out any previous content
            Body = null;
            BodyHtml = null;
            Html = null;
            RtfCompressed = null;
            Attachments.Clear();
            Properties.Clear();
            Recipients.Clear();
        }

        public string GetBodyAsHtmlString()
        {
            if (BodyHtml != null)
                return BodyHtml; // This will be plain ASCII
            else if (Html != null)
            {
                var e = GetEncoding();
                if (e != null)
                {
                    return e.GetString(Html);
                }
            }
            else if (Body != null) // Not really expecting this as a source of HTML
                return EscapeUnicodeCharacters(Body);

            return null;
        }
        public void SaveAttachments(List<Attachment> atts)
        {
            Attachments = new List<Attachment>(atts);
        }

        private static string EscapeUnicodeCharacters(string source)
        {
            int length = source.Length;
            var escaped = new StringBuilder();

            for (int i = 0; i < length; i++)
            {
                char ch = source[i];

                if (ch >= '\x00a0')
                {
                    escaped.AppendFormat("&#x{0};", ((int)ch).ToString("X4"));
                }
                else
                {
                    escaped.Append(ch);
                }
            }

            return escaped.ToString();
        }

        private Encoding GetEncoding()
        {
            var p = Properties.FirstOrDefault(x => x.Guid == "00020386-0000-0000-c000-000000000046" && x.Name == "content-type");
            if (p != null)
            {

                Match m = Regex.Match((string)p.Value, @".*charset=""(.*?)""");
                if (m.Success)
                    return Encoding.GetEncoding(m.Groups[1].Value);
            }

            p = Properties.FirstOrDefault(x => x.Tag == EpropertyTag.PidTagInternetCodepage);
            if (p != null)
            {
                return Encoding.GetEncoding((int)p.Value);
            }

            return null;
        }
    }
}
