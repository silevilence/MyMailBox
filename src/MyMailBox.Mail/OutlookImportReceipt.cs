using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal sealed record OutlookReceipt(string Source, string Fingerprint)
{
    internal static OutlookReceipt From(OutlookAccountPreview row)
    {
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new(Hash(row.Source.ToUpperInvariant()), Hash(JsonSerializer.Serialize(row with { ImportedAccount = null, SourceChanged = false })));
    }
}

public sealed partial class AccountService
{
    internal IReadOnlyList<OutlookAccountPreview> MarkImported(IReadOnlyList<OutlookAccountPreview> entries, CancellationToken token)
    {
        using var db = _store.Open();
        using var lookup = db.CreateCommand();
        lookup.CommandText = "SELECT i.fingerprint,a.id FROM outlook_imported i JOIN accounts a ON a.id=i.account AND a.removed=0 WHERE i.source=$source";
        var source = lookup.Parameters.Add("$source", SqliteType.Text);
        return entries.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = OutlookReceipt.From(row);
            source.Value = receipt.Source;
            using var read = lookup.ExecuteReader();
            if (!read.Read()) return row;
            return read.GetString(0) == receipt.Fingerprint ? row with { ImportedAccount = Guid.Parse(read.GetString(1)) } : row with { SourceChanged = true };
        }).ToArray();
    }

    internal MailAccount MapImported(AccountEdit edit, Guid id, OutlookReceipt receipt, CancellationToken token)
    {
        using var db = _store.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM accounts WHERE id=$id AND provider=$p AND address=$a COLLATE NOCASE AND removed=0";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$p", edit.Provider); cmd.Parameters.AddWithValue("$a", edit.Address.Trim());
        MailAccount mapped;
        using (var read = cmd.ExecuteReader()) mapped = read.Read() ? Read(read) : throw new InvalidDataException("映射目标已变化，请重新选择账户。");
        StoreReceipt(db, tx, receipt, mapped.Id);
        token.ThrowIfCancellationRequested(); tx.Commit();
        return mapped;
    }

    private static MailAccount? ImportedAccount(SqliteConnection db, SqliteTransaction tx, OutlookReceipt receipt)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT a.* FROM outlook_imported i JOIN accounts a ON i.account=a.id AND a.removed=0 WHERE i.source=$s AND i.fingerprint=$f";
        cmd.Parameters.AddWithValue("$s", receipt.Source); cmd.Parameters.AddWithValue("$f", receipt.Fingerprint);
        using var read = cmd.ExecuteReader();
        return read.Read() ? Read(read) : null;
    }

    private static void StoreReceipt(SqliteConnection db, SqliteTransaction tx, OutlookReceipt receipt, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO outlook_imported VALUES($s,$f,$a) ON CONFLICT(source) DO UPDATE SET fingerprint=$f,account=$a";
        cmd.Parameters.AddWithValue("$s", receipt.Source); cmd.Parameters.AddWithValue("$f", receipt.Fingerprint); cmd.Parameters.AddWithValue("$a", id.ToString());
        cmd.ExecuteNonQuery();
    }
}
