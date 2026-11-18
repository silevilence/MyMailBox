namespace MyMailBox.Contracts;

public sealed record AddressChoice(string Name,string Address,bool Contact=false);
public sealed record RecipientFields(string To,string Cc,string Bcc,int Duplicates=0);
public sealed record AddressProvider(string Id,string Name,Func<Guid,string,CancellationToken,Task<IReadOnlyList<AddressChoice>>> Suggest);
