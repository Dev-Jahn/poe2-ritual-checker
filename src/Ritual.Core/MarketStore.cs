using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ritual.Core;

public record MarketSnapshot<T>(DateTimeOffset RetrievedAt, T Data);

public sealed class MarketStore
{
    private readonly string connectionString;
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RitualChecker",
            "market.sqlite"
        );

    public MarketStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS snapshots (key TEXT PRIMARY KEY, at INTEGER NOT NULL, payload TEXT NOT NULL); DELETE FROM snapshots WHERE at < $cutoff AND key NOT LIKE 'ninja/v1/%'";
        command.Parameters.AddWithValue(
            "$cutoff",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds()
        );
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public MarketSnapshot<T>? Read<T>(string key, TimeSpan maxAge)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT at,payload FROM snapshots WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        using var row = command.ExecuteReader();
        if (!row.Read())
            return null;
        var at = DateTimeOffset.FromUnixTimeSeconds(row.GetInt64(0));
        var age = DateTimeOffset.UtcNow - at;
        if (age < TimeSpan.Zero || age > maxAge)
            return null;
        try
        {
            var data = JsonSerializer.Deserialize<T>(row.GetString(1), JsonFiles.Options);
            return data is null ? null : new(at, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write<T>(string key, T data, DateTimeOffset? at = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "INSERT INTO snapshots(key,at,payload) VALUES($key,$at,$data) ON CONFLICT(key) DO UPDATE SET at=excluded.at,payload=excluded.payload";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$at", (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data, JsonFiles.Options));
        command.ExecuteNonQuery();
    }
}
