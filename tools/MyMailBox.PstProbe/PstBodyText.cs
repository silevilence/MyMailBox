using MimeKit.Text;

static class PstBodyText
{
    public static string FromRtf(string rtf)
    {
        var text = new RtfToText().Convert(rtf);
        // RTF ends its final paragraph with \par. Remove that one structural newline,
        // retaining spaces, blank paragraphs and any additional user-entered line breaks.
        return text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
            : text.EndsWith('\n') ? text[..^1] : text;
    }
}
