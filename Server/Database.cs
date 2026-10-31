using System.Globalization;
using Microsoft.Data.Sqlite;
using Velom3d.Sync;

namespace Velom3d.Server;

/// <summary>
/// Base SQLite : un seul fichier, rien à installer. Comptes, jetons de connexion (seulement leur
/// empreinte), documents de chaque compte (le dernier état, suppressions comprises) et fichiers de
/// données des sorties (compressés en gzip).
/// </summary>
public sealed class Database
{
    readonly string _connectionString;

    public Database(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY,
                username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                seq           INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS tokens (
                hash         TEXT PRIMARY KEY,
                user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at   TEXT NOT NULL,
                last_used_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS documents (
                user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                kind       TEXT NOT NULL,
                id         TEXT NOT NULL,
                seq        INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                deleted    INTEGER NOT NULL,
                content    TEXT NOT NULL,
                PRIMARY KEY (user_id, kind, id)
            );
            CREATE INDEX IF NOT EXISTS documents_seq ON documents(user_id, seq);
            CREATE TABLE IF NOT EXISTS ride_data (
                user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                id         TEXT NOT NULL,
                gzip       BLOB NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (user_id, id)
            );
            """);
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout = 10000;");
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    // ---- Comptes ----

    /// <summary>Crée le compte ; null si le nom est déjà pris.</summary>
    public long? CreateUser(string username, string passwordHash)
    {
        using var c = Open();
        try
        {
            using var cmd = Cmd(c, "INSERT INTO users (username, password_hash, created_at) VALUES ($u, $p, $t) RETURNING id;",
                ("$u", username), ("$p", passwordHash), ("$t", Now()));
            return (long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return null; } // UNIQUE
    }

    public (long Id, string Username, string PasswordHash)? FindUser(string username)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, username, password_hash FROM users WHERE username = $u;", ("$u", username));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1), r.GetString(2)) : null;
    }

    public string? Username(long userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT username FROM users WHERE id = $id;", ("$id", userId));
        return cmd.ExecuteScalar() as string;
    }

    public void SetPassword(long userId, string passwordHash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE users SET password_hash = $p WHERE id = $id;", ("$p", passwordHash), ("$id", userId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Supprime le compte et toutes ses données.</summary>
    public void DeleteUser(long userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM users WHERE id = $id;", ("$id", userId));
        cmd.ExecuteNonQuery();
    }

    public void AddToken(string tokenHash, long userId)
    {
        using var c = Open();
        var now = Now();
        using var cmd = Cmd(c, "INSERT INTO tokens (hash, user_id, created_at, last_used_at) VALUES ($h, $u, $t, $t);",
            ("$h", tokenHash), ("$u", userId), ("$t", now));
        cmd.ExecuteNonQuery();
    }

    public long? UserForToken(string tokenHash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE tokens SET last_used_at = $t WHERE hash = $h RETURNING user_id;",
            ("$h", tokenHash), ("$t", Now()));
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    public void DeleteToken(string tokenHash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM tokens WHERE hash = $h;", ("$h", tokenHash));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Déconnecte tous les appareils du compte sauf celui-ci.</summary>
    public void DeleteOtherTokens(long userId, string keepHash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM tokens WHERE user_id = $u AND hash <> $h;", ("$u", userId), ("$h", keepHash));
        cmd.ExecuteNonQuery();
    }

    // ---- Documents ----

    /// <summary>Enregistre les changements, chacun avec un nouveau numéro d'ordre. Le dernier envoyé
    /// remplace la version du serveur : c'est l'appareil qui décide (les données locales font foi).</summary>
    public int Push(long userId, IReadOnlyList<SyncChange> changes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var ch in changes)
        {
            long seq;
            using (var next = Cmd(c, "UPDATE users SET seq = seq + 1 WHERE id = $u RETURNING seq;", ("$u", userId)))
            {
                next.Transaction = tx;
                seq = (long)next.ExecuteScalar()!;
            }
            using (var up = Cmd(c, """
                INSERT INTO documents (user_id, kind, id, seq, updated_at, deleted, content)
                VALUES ($u, $k, $i, $s, $t, $d, $c)
                ON CONFLICT (user_id, kind, id) DO UPDATE SET
                    seq = excluded.seq, updated_at = excluded.updated_at,
                    deleted = excluded.deleted, content = excluded.content;
                """,
                ("$u", userId), ("$k", ch.Kind), ("$i", ch.Id), ("$s", seq),
                ("$t", ch.UpdatedAt.ToString("o", CultureInfo.InvariantCulture)),
                ("$d", ch.Deleted ? 1 : 0), ("$c", ch.Deleted ? "" : ch.Content)))
            {
                up.Transaction = tx;
                up.ExecuteNonQuery();
            }
            if (ch.Deleted && ch.Kind == SyncKinds.Ride)
            {
                using var del = Cmd(c, "DELETE FROM ride_data WHERE user_id = $u AND id = $i;", ("$u", userId), ("$i", ch.Id));
                del.Transaction = tx;
                del.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return changes.Count;
    }

    /// <summary>Documents changés après <paramref name="since"/>, dans l'ordre.</summary>
    public SyncPullResponse Pull(long userId, long since, int limit)
    {
        using var c = Open();
        var res = new SyncPullResponse { Cursor = since };
        using (var cmd = Cmd(c, """
            SELECT kind, id, seq, updated_at, deleted, content FROM documents
            WHERE user_id = $u AND seq > $s ORDER BY seq LIMIT $l;
            """, ("$u", userId), ("$s", since), ("$l", limit + 1)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (res.Changes.Count == limit) { res.More = true; break; }
                res.Changes.Add(new SyncChange
                {
                    Kind = r.GetString(0), Id = r.GetString(1), Seq = r.GetInt64(2),
                    UpdatedAt = DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                    Deleted = r.GetInt64(4) != 0, Content = r.GetString(5),
                });
            }
        }
        if (res.Changes.Count > 0) res.Cursor = res.Changes[^1].Seq;
        return res;
    }

    // ---- Données des sorties ----

    public void PutRideData(long userId, string id, byte[] gzip)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            INSERT INTO ride_data (user_id, id, gzip, updated_at) VALUES ($u, $i, $g, $t)
            ON CONFLICT (user_id, id) DO UPDATE SET gzip = excluded.gzip, updated_at = excluded.updated_at;
            """, ("$u", userId), ("$i", id), ("$g", gzip), ("$t", Now()));
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetRideData(long userId, string id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT gzip FROM ride_data WHERE user_id = $u AND id = $i;", ("$u", userId), ("$i", id));
        return cmd.ExecuteScalar() as byte[];
    }
}
