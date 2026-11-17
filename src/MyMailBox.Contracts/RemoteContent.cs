namespace MyMailBox.Contracts;

public enum RemoteContentScope { Message, Sender, Domain }
public sealed record RemoteContentContext(Guid MessageId, string From);
public sealed record RemoteContentRule(RemoteContentScope Scope, string Value);
public sealed record RemoteContentPreview(string Sender, string Domain, RemoteContentRule[] MatchingRules);
public sealed record RemoteLoadResult(IReadOnlyDictionary<string, MailResource> Resources, string[] Results);
public interface IRemoteContentService
{
    Task<RemoteContentPreview> Preview(RemoteContentContext context, CancellationToken cancellation = default);
    Task SetRule(RemoteContentContext context, RemoteContentScope scope, bool allow, CancellationToken cancellation = default);
    Task<RemoteLoadResult> Load(RemoteContentContext context, IReadOnlyList<RemoteMailResource> resources, CancellationToken cancellation = default);
}
