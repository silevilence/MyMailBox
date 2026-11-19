namespace MyMailBox.Contracts;

public sealed record TemplateEntry(string Key,string Name);
public sealed record TemplateCatalog(string Digest,TemplateEntry[] Entries);
public sealed record TemplateField(string Key,string Label,Choice[]? Choices = null,string Initial = "");
public interface ISettingsTemplates
{
    Task Export(string file,CancellationToken token);
    Task<TemplateCatalog> Open(string file,CancellationToken token);
    Task<TemplateField[]> Fields(string file,string digest,string entry,CancellationToken token);
    Task<string> Review(string file,string digest,string entry,IReadOnlyDictionary<string,string> bindings,bool save,CancellationToken token);
}
