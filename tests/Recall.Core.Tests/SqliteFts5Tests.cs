using Microsoft.Data.Sqlite;

namespace Recall.Core.Tests;

public sealed class SqliteFts5Tests
{
    [Fact]
    public void BundledSqliteSupportsFts5AndHyphenSeparatedWords()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE documents USING fts5(filename, content, tokenize = 'unicode61');
            INSERT INTO documents(filename, content)
            VALUES ('paper.pdf', 'The clear-sky index transformation is used.');
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT filename FROM documents WHERE documents MATCH $query;";
        command.Parameters.AddWithValue("$query", "clear sky index");
        Assert.Equal("paper.pdf", command.ExecuteScalar());
    }
}
