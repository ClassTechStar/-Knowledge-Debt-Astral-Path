using Microsoft.Data.Sqlite;

namespace AstralPath.Mobile.Data;

/// <summary>手写 SQL 的本地库（禁用 EF）。全部参数化。</summary>
public sealed class AppDbContext : IDisposable
{
    private readonly SqliteConnection _conn;

    public AppDbContext(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_kp_node(
              kp_id TEXT PRIMARY KEY, title TEXT NOT NULL, course TEXT, difficulty INTEGER);
            CREATE TABLE IF NOT EXISTS local_kp_edge(
              from_kp TEXT NOT NULL, to_kp TEXT NOT NULL, edge_type TEXT NOT NULL, weight REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS local_mastery(
              kp_id TEXT PRIMARY KEY, raw REAL NOT NULL, age_days REAL NOT NULL,
              streak INTEGER NOT NULL, score REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS local_debt_edge(
              from_kp TEXT NOT NULL, to_kp TEXT NOT NULL, impact REAL NOT NULL,
              status TEXT NOT NULL, streak INTEGER NOT NULL,
              PRIMARY KEY(from_kp, to_kp));
            CREATE TABLE IF NOT EXISTS local_plan_item(
              id INTEGER PRIMARY KEY AUTOINCREMENT, day INTEGER NOT NULL, kp_id TEXT NOT NULL,
              layer TEXT NOT NULL, minutes INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS local_attempt(
              id TEXT PRIMARY KEY, kp_id TEXT NOT NULL, correct INTEGER NOT NULL,
              self_conf INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS local_question(
              id TEXT PRIMARY KEY, kp_id TEXT NOT NULL, stem TEXT NOT NULL,
              answer_kind TEXT NOT NULL, correct_payload TEXT NOT NULL, difficulty INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var c = _conn.CreateCommand();
        c.CommandText = sql;
        return c;
    }

    public void Dispose() => _conn.Dispose();
}
