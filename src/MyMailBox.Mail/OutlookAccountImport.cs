using System.Text;
using System.Runtime.Versioning;
using Microsoft.Win32;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

public sealed class OutlookAccountImport(AccountService accounts, Func<string,CancellationToken,Task<IReadOnlyList<OutlookAccountPreview>>> readProfile) : IOutlookAccountImport
{
    public OutlookAccountImport(AccountService accounts) : this(accounts, ReadProfile) { }
    // Classic Outlook 16 Internet account sections. Providers may omit these optional fields.
    private const string ProfileRoot = @"Software\Microsoft\Office\16.0\Outlook\Profiles";
    private const string AccountSection = "9375CFF0413111d3B88A00104B2A6676";

    public Task<IReadOnlyList<string>> Profiles(CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("需要 Windows 经典 Outlook。");
        using var profiles = Registry.CurrentUser.OpenSubKey(ProfileRoot);
        return profiles?.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }, token);

    public async Task<IReadOnlyList<OutlookAccountPreview>> Preview(string profile, CancellationToken token) =>
        accounts.MarkImported(await readProfile(profile, token), token);

    private static Task<IReadOnlyList<OutlookAccountPreview>> ReadProfile(string profile, CancellationToken token) => Task.Run<IReadOnlyList<OutlookAccountPreview>>(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var profiles = Registry.CurrentUser.OpenSubKey(ProfileRoot);
        if (profiles == null || !profiles.GetSubKeyNames().Contains(profile, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("所选经典 Outlook Profile 已不存在，请重新发现。");
        using var entries = profiles.OpenSubKey(profile + "\\" + AccountSection);
        var result = new List<OutlookAccountPreview>();
        if (entries == null) return result;
        foreach (var section in entries.GetSubKeyNames())
        {
            token.ThrowIfCancellationRequested();
            using var row = entries.OpenSubKey(section);
            if (row == null) continue;
            var entry = ReadSection(profile + " / " + section, row);
            if (entry != null) result.Add(entry);
        }
        return result;
    }, token);

    [SupportedOSPlatform("windows")]
    private static OutlookAccountPreview? ReadSection(string source, RegistryKey row) =>
        ReadSection(source, name => row.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));

    public static OutlookAccountPreview? ReadSection(string source, Func<string, object?> read)
    {
        string Value(string field) => read(field) switch
        {
            string value => value.TrimEnd('\0').Trim(),
            byte[] data when data.Length % 2 == 0 => Encoding.Unicode.GetString(data).TrimEnd('\0').Trim(),
            _ => ""
        };
        OutlookEndpoint? Endpoint(string protocol)
        {
            var host = Value(protocol + " Server");
            var user = Value(protocol + " User");
            var port = read(protocol + " Port") switch
            {
                int n => n,
                byte[] { Length: 4 } data => BitConverter.ToInt32(data),
                string text when int.TryParse(text, out var n) => n,
                _ => 0
            };
            return host.Length == 0 && user.Length == 0 && port == 0 ? null : new(host, port is > 0 and <= 65535 ? port : null, user);
        }
        var imap = Endpoint("IMAP");
        var pop = Endpoint("POP3");
        var smtp = Endpoint("SMTP");
        var name = Value("Account Name");
        var email = Value("Email");
        if (name == "" && email == "" && imap == null && pop == null && smtp == null) return null;
        var provider = imap != null ? smtp == null ? "imap" : "imap-smtp" : pop != null ? "pop3" : smtp != null ? "smtp" : "";
        return new(source, name, email, provider, imap ?? pop, smtp);
    }

    public async Task<IReadOnlyList<MailAccount>> Matches(string provider, string address, CancellationToken token) =>
        (await accounts.List(token)).Where(a => a.Provider == provider && a.Address.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    public async Task<MailAccount> ImportSource(string profile, OutlookAccountPreview source, AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token)
    {
        var receipt = OutlookReceipt.From(source);
        var current = (await readProfile(profile, token)).SingleOrDefault(row => row.Source == source.Source);
        if (current == null || OutlookReceipt.From(current) != receipt)
            throw new InvalidDataException("来源账户已变化。请重新读取 Profile 并确认映射。");
        if (edit.Provider != current.Provider) throw new InvalidDataException("来源账户类型已变化，请重新确认。");
        return await Import(edit, settings, mapTo, token, receipt);
    }

    public Task<MailAccount> Import(AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token) =>
        Import(edit, settings, mapTo, token, null);

    private async Task<MailAccount> Import(AccountEdit edit, ProtocolSettings settings, Guid? mapTo, CancellationToken token, OutlookReceipt? receipt)
    {
        if (edit.Id != null || edit.Provider is not ("imap" or "imap-smtp" or "pop3" or "smtp"))
            throw new InvalidDataException("请选择支持的 Internet 邮箱类型。");
        if (mapTo is { } mapped && receipt != null) return accounts.MapImported(edit, mapped, receipt, token);
        if (mapTo is { } id)
            return (await Matches(edit.Provider, edit.Address, token)).SingleOrDefault(m => m.Id == id)
                ?? throw new InvalidDataException("映射的账户或邮箱身份已变化，请重新核对。");
        if ((edit.Provider != "smtp") != (settings.Incoming != null) || (edit.Provider is "smtp" or "imap-smtp") != (settings.Outgoing != null))
            throw new InvalidDataException("请补全所选协议的服务器设置。");
        if (settings.Incoming != null) ProtocolMailService.Validate(settings.Incoming);
        if (settings.Outgoing != null) ProtocolMailService.Validate(settings.Outgoing);
        return await accounts.CreateImported(edit, settings, token, receipt);
    }
}
