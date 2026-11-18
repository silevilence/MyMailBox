using System.Security.Cryptography;
using System.Text;
using MimeKit;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed record AutomaticMailResult(bool Skipped, string Detail);

public sealed partial class ProtocolMailService
{
    internal const int AutomaticHourlyLimit = 10;

    internal static string DescribeAutomatic(bool forward, string value) => forward
        ? $"自动转发到 {value}，仅发送主题和可读正文，不含附件。每账户每小时最多 {AutomaticHourlyLimit} 封。"
        : $"自动回复原信的退信地址，使用固定回复「{value}」。同一收件人每天最多一次，每账户每小时最多 {AutomaticHourlyLimit} 封。";

    internal async Task ValidateAutomatic(Guid? source, bool forward, string value, CancellationToken token)
    {
        if (!source.HasValue) throw new InvalidDataException("自动外发需要明确选择具有收发能力的账户。");
        await Account(source.Value, AccountCapabilities.Receive | AccountCapabilities.Send, token);
        if (forward)
        {
            if (value.Contains('\r') || value.Contains('\n') || !MailboxAddress.TryParse(value, out var address) ||
                string.IsNullOrWhiteSpace(address.Domain))
                throw new InvalidDataException("自动转发需要一个固定的有效邮箱地址。");
        }
        else if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
            throw new InvalidDataException("自动回复需要填写 1–4096 字的固定正文。");
    }

    internal async Task<AutomaticMailResult> ApplyAutomatic(Guid operation, Guid source, Guid folder, string key, bool forward, string value, CancellationToken token)
    {
        await ValidateAutomatic(source, forward, value, token);
        var request = new TransferRequest(operation, source, folder, key, Guid.Empty, false);
        var content = Guid.TryParse(key, out _) ? _transfers.ReadLocal(request) : await ReadTransferSource(request, token);
        using var original = await MimeMessage.LoadAsync(new MemoryStream(content.Raw, false), token);
        var headers = original.Headers;
        bool suppressed = headers.Any(h => h.Id == HeaderId.AutoSubmitted &&
                !h.Value.Split(';')[0].Trim().Equals("no",StringComparison.OrdinalIgnoreCase)) ||
            headers.Contains("X-MyMailBox-Automation") || headers.Contains("X-Loop") || headers.Contains("List-Id") ||
            headers.Contains("List-Unsubscribe") || headers.Contains("X-Auto-Response-Suppress") ||
            original.Body?.ContentType.MimeType.Equals("multipart/report", StringComparison.OrdinalIgnoreCase) == true ||
            headers["Precedence"]?.Trim().ToLowerInvariant() is "bulk" or "list" or "junk";
        if (suppressed) return new(true,"已跳过自动提交、列表、回执或标明禁止自动响应的邮件。");

        string? path = headers["Return-Path"]?.Trim();
        if (path == "<>") return new(true,"已跳过空退信地址。");
        MailboxAddress? recipient;
        if (forward) recipient = MailboxAddress.Parse(value);
        else if (path is null || !MailboxAddress.TryParse(path.Trim('<','>'), out recipient))
            return new(true,"缺少可验证的退信地址，已跳过自动回复。");

        var identity = await Account(source, AccountCapabilities.Send, token);
        var aliases = _draftStore.Profile(source).Aliases.Append(identity.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (aliases.Contains(recipient.Address) || original.From.Mailboxes.Any(a => aliases.Contains(a.Address)))
            return new(true,"已跳过发给自己或来自本账户的邮件。");
        if (!forward && !original.To.Mailboxes.Concat(original.Cc.Mailboxes).Any(a => aliases.Contains(a.Address)))
            return new(true,"原信没有将当前账户列为直接收件人，已跳过自动回复。");
        if (!forward && (recipient.LocalPart.StartsWith("mailer-daemon", StringComparison.OrdinalIgnoreCase) ||
            recipient.LocalPart.Equals("postmaster",StringComparison.OrdinalIgnoreCase)))
            return new(true,"已跳过退信处理地址。");

        string fingerprint = string.IsNullOrWhiteSpace(original.MessageId)
            ? Convert.ToHexString(SHA256.HashData(content.Raw))
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.MessageId)));
        string? skipped = ReserveAutomatic(operation, source, fingerprint, recipient.Address, forward);
        if (skipped is not null) return new(true,skipped);
        string body = forward ? original.TextBody ?? (await new MimeReader().Parse(content.Raw,token)).Text : value;
        var outgoing = new TextMail(recipient.Address,"","",(forward ? "Fwd: " : "Re: ") + original.Subject,body)
        {
            InReplyTo = forward ? null : original.MessageId,
            References = forward ? [] : original.References.Append(original.MessageId).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
        };
        await Queue(operation,source,outgoing,null,token,forward ? "auto-generated" : "auto-replied");
        var sent = await Deliver(operation,token);
        if (sent.State != "sent") throw new InvalidDataException($"自动外发已记录在发件箱，状态 {sent.State}。{sent.Message}");
        return new(false,$"已由服务器接收，目标 {recipient.Address}，发件箱编号 {operation}。");
    }

    private string? ReserveAutomatic(Guid operation, Guid source, string fingerprint, string recipient, bool forward)
    {
        using var db = _store.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$op", operation.ToString());
        cmd.Parameters.AddWithValue("$sender", source.ToString());
        cmd.Parameters.AddWithValue("$fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("$recipient", recipient);
        cmd.Parameters.AddWithValue("$kind", forward ? "forward" : "reply");
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.CommandText = "SELECT operation FROM automatic_mail WHERE sender=$sender AND fingerprint=$fingerprint AND recipient=$recipient AND kind=$kind";
        if (cmd.ExecuteScalar() is string previous)
            return previous == operation.ToString() ? null : "同一封邮件已处理，已跳过重复外发。";
        cmd.CommandText = "SELECT count(*) FROM automatic_mail WHERE sender=$sender AND at>$now-3600";
        if ((long)cmd.ExecuteScalar()! >= AutomaticHourlyLimit) return "本账户已达每小时自动外发上限，已跳过。";
        if (!forward)
        {
            cmd.CommandText = "SELECT count(*) FROM automatic_mail WHERE sender=$sender AND kind='reply' AND recipient=$recipient AND at>$now-86400";
            if ((long)cmd.ExecuteScalar()! > 0) return "同一收件人 24 小时内已自动回复，已跳过。";
        }
        cmd.CommandText = "INSERT INTO automatic_mail VALUES($op,$sender,$fingerprint,$recipient,$kind,$now)";
        cmd.ExecuteNonQuery();
        tx.Commit();
        return null;
    }
}
