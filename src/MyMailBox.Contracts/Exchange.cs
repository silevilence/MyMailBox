namespace MyMailBox.Contracts;

public sealed record ExchangeSettings(string Endpoint,string User,bool DeviceAllowed,bool WriteAllowed,string Version,string[] Commands);
public interface IExchangeSetup
{
    Task<ExchangeSettings> Settings(Guid account,CancellationToken token);
    Task Configure(Guid account,string endpoint,string user,string? password,bool deviceAllowed,bool writeAllowed,CancellationToken token);
    Task<string> SaveDraft(MailDraft draft,CancellationToken token);
    Task<ExchangeSettings> Check(Guid account,CancellationToken token);
}
public sealed record ExchangePage(IAccountService Accounts,IExchangeSetup Service);
