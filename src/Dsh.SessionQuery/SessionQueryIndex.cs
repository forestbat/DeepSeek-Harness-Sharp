using Dsh.Core;
using Dsh.Llm;
using Microsoft.Data.Sqlite;

namespace Dsh.SessionQuery;

public sealed record SessionQueryHit(string SessionId, long Seq, string Type, string Snippet);

public sealed class SessionQueryIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    static SessionQueryIndex()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SessionQueryIndex(string databasePath = ":memory:")
    {
        _connection = new SqliteConnection(databasePath == ":memory:" ? "Data Source=:memory:" : $"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    public void IndexSession(Session session)
    {
        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM docs WHERE session_id = $id";
        delete.Parameters.AddWithValue("$id", session.Id.Value);
        delete.ExecuteNonQuery();

        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO docs(session_id, seq, type, text)
            VALUES ($id, $seq, $type, $text)
            """;
        var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
        var seqParameter = insert.Parameters.Add("$seq", SqliteType.Integer);
        var typeParameter = insert.Parameters.Add("$type", SqliteType.Text);
        var textParameter = insert.Parameters.Add("$text", SqliteType.Text);

        foreach (var sessionEvent in session.SnapshotEvents())
        {
            var text = EventText(sessionEvent);
            if (text.Length == 0)
                continue;
            idParameter.Value = session.Id.Value;
            seqParameter.Value = sessionEvent.Seq;
            typeParameter.Value = sessionEvent.Type;
            textParameter.Value = text;
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SessionQueryHit> Search(string query, int limit = 20)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, seq, type, snippet(docs, 0, '[', ']', '...', 8) AS snippet
            FROM docs
            WHERE docs MATCH $query
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<SessionQueryHit>();
        while (reader.Read())
        {
            hits.Add(new SessionQueryHit(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return hits;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static string EventText(SessionEvent sessionEvent)
    {
        var builder = new System.Text.StringBuilder();
        switch (sessionEvent.Data)
        {
            case UserMessagePayload user:
                AppendText(builder, user.Message.Content);
                break;
            case AssistantMessagePayload assistant:
                AppendText(builder, assistant.Message.Content);
                break;
            case ToolResultPayload toolResult:
                AppendText(builder, toolResult.Message.Content);
                break;
        }
        return builder.ToString();
    }

    private static void AppendText(System.Text.StringBuilder builder, IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is TextBlock text)
                builder.Append(text.Text).Append('\n');
        }
    }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS search_state (
              singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
              global_generation INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS docs USING fts5(
              text,
              session_id UNINDEXED,
              seq UNINDEXED,
              type UNINDEXED,
              tokenize = 'unicode61'
            );
            """;
        command.ExecuteNonQuery();
    }
}
