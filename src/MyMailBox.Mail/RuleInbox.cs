using Microsoft.Data.Sqlite;
using MyMailBox.Contracts;

namespace MyMailBox.Mail;

internal static class RuleInbox
{
    internal static void Initialize(SqliteCommand cmd)
    {
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS receive_rule(id TEXT PRIMARY KEY,version INTEGER NOT NULL,name TEXT NOT NULL,account TEXT,folder TEXT,
                priority INTEGER NOT NULL,enabled INTEGER NOT NULL,history INTEGER NOT NULL,definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rule_run(id TEXT PRIMARY KEY,event TEXT NOT NULL,rule TEXT NOT NULL,version INTEGER NOT NULL,
                name TEXT NOT NULL,account TEXT NOT NULL,folder TEXT NOT NULL,item TEXT NOT NULL,priority INTEGER NOT NULL,
                definition TEXT NOT NULL,state TEXT NOT NULL,detail TEXT NOT NULL,created INTEGER NOT NULL,UNIQUE(event,rule));
            CREATE TABLE IF NOT EXISTS rule_action_log(op TEXT PRIMARY KEY,run TEXT NOT NULL REFERENCES rule_run(id),ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,state TEXT NOT NULL,source TEXT NOT NULL,item TEXT NOT NULL,destination TEXT,target TEXT,undo TEXT,detail TEXT NOT NULL,UNIQUE(run,ordinal));
            CREATE INDEX IF NOT EXISTS rule_run_work ON rule_run(state,created,priority);
            CREATE TABLE IF NOT EXISTS pop_rule_baseline(account TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(account,source));
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void Arrived(SqliteTransaction tx, Guid folder, string key, string eventId, bool historical)
    {
        using var cmd = tx.Connection!.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO rule_run
            SELECT lower(hex(randomblob(16))),$event,r.id,r.version,r.name,f.account_id,f.id,$item,r.priority,r.definition,
                'pending','等待判断',unixepoch()
            FROM receive_rule r JOIN folders f ON f.id=$folder
            WHERE r.enabled AND ($history=0 OR r.history) AND (r.account IS NULL OR r.account=f.account_id) AND (r.folder IS NULL OR r.folder=f.id)
            """;
        cmd.Parameters.AddWithValue("$event", eventId); cmd.Parameters.AddWithValue("$item", key); cmd.Parameters.AddWithValue("$folder", folder.ToString());
        cmd.Parameters.AddWithValue("$history", historical); cmd.ExecuteNonQuery();
    }

    internal static bool PopBaseline(SqliteMailStore store, Guid account, string source, bool complete)
    {
        using var db = store.Open(); using var c = db.CreateCommand();
        c.Parameters.AddWithValue("$a", account.ToString()); c.Parameters.AddWithValue("$s", source);
        if (complete) { c.CommandText = "INSERT OR IGNORE INTO pop_rule_baseline VALUES($a,$s)"; c.ExecuteNonQuery(); }
        c.CommandText = "SELECT count(*) FROM pop_rule_baseline WHERE account=$a AND source=$s";
        return (long)c.ExecuteScalar()! != 0;
    }
}
