namespace MyMailBox.Contracts;

public sealed record MailResource(string Key, string ContentType, byte[] Data);
public sealed record RemoteMailResource(string Id, string Url, string Kind);
public sealed record SafeMailDocument(string Html, IReadOnlyList<MailResource> Resources, IReadOnlyList<RemoteMailResource> RemoteResources, string[] Warnings);
public interface IHtmlMailRenderer
{
    Task<SafeMailDocument> Render(ParsedMail mail, string baseUri, IReadOnlyDictionary<string, MailResource>? loadedRemote = null, CancellationToken cancellation = default);
}
