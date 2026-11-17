namespace MyMailBox.Contracts;

public sealed record StorageReport(long Messages, long RawBytes, long CacheBytes, string[] Problems)
{
    public override string ToString() => $"邮件 {Messages} 封 · 原文 {RawBytes:N0} 字节 · 可丢弃缓存 {CacheBytes:N0} 字节\n" + (Problems.Length == 0 ? "完整性检查通过。" : string.Join("\n", Problems));
}

public interface IStorageMaintenance
{
    Task<StorageReport> Inspect(CancellationToken cancellation = default);
    Task<int> Rebuild(CancellationToken cancellation = default);
    Task<long> ClearCache(CancellationToken cancellation = default);
}
