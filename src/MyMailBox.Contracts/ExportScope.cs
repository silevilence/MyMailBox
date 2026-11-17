namespace MyMailBox.Contracts;

public static class ExportScope
{
    public static async Task<ExportItem[]> Collect(IMailStore store, IReadOnlyCollection<Guid> accounts, IReadOnlyCollection<Guid> folders, string subject, CancellationToken cancellation = default)
    {
        var selected = (await store.Folders(cancellation)).Where(f => accounts.Contains(f.AccountId) || folders.Contains(f.Id));
        var items = new List<ExportItem>();
        foreach (var folder in selected)
        {
            for (var offset = 0; ; offset += 200)
            {
                cancellation.ThrowIfCancellationRequested();
                var page = await store.List(folder.Id, offset, 200, cancellation);
                items.AddRange(page.Items.Where(m => m.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase)).Select(m => new ExportItem(m.Id, m.Subject)));
                if (!page.HasMore) break;
            }
        }
        return items.DistinctBy(m => m.MessageId).ToArray();
    }
}
