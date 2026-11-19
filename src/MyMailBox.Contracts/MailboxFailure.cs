namespace MyMailBox.Contracts;

public enum MailboxFailureKind { Offline, Authorization, Throttled, Policy, Storage, Unavailable, PluginPermission }

/// <summary>Classify an error for the receive queue while preserving its original exception type.</summary>
public static class MailboxFailure
{
    public static T Mark<T>(T error, MailboxFailureKind kind, DateTimeOffset? retryAfter = null) where T : Exception
    {
        error.Data[typeof(MailboxFailureKind)] = kind;
        if (retryAfter.HasValue) error.Data[typeof(MailboxFailure)] = retryAfter.Value;
        return error;
    }

    public static MailboxFailureKind? Kind(Exception error) => error.Data[typeof(MailboxFailureKind)] as MailboxFailureKind?;
    public static DateTimeOffset? RetryAfter(Exception error) => error.Data[typeof(MailboxFailure)] as DateTimeOffset?;
}
