using System.Text;
using System.Xml.Linq;

namespace MyMailBox.Updates;

public static class SignedAppCast
{
    public static byte[] Create(string version, Uri installer, long size, string signature, string notes)
    {
        var parsed = ReleaseVersion.Parse(version) ?? throw new ArgumentException("发布版本无效。");
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var document = new XDocument(new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "sparkle", sparkle),
            new XElement("channel", new XElement("title", "MyMailBox"), new XElement("item",
                new XElement("title", "MyMailBox V" + parsed.ToFullString()), new XElement("description", notes),
                new XElement("enclosure", new XAttribute("url", installer.AbsoluteUri), new XAttribute("length", size),
                    new XAttribute("type", "application/octet-stream"), new XAttribute(sparkle + "version", parsed.ToFullString()),
                    new XAttribute(sparkle + "os", "windows"), new XAttribute(sparkle + "signature", signature))))));
        return Encoding.UTF8.GetBytes(document.ToString());
    }
}
