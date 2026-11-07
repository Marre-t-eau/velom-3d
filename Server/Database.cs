using System.Globalization;
using Microsoft.Data.Sqlite;
using Velom3d.Sync;

namespace Velom3d.Server;

/// <summary>
/// Base SQLite : un seul fichier, rien à installer. Comptes, jetons de connexion (seulement leur
/// empreinte), documents de chaque compte (le dernier état, suppressions comprises) et fichiers de
/// données des sorties (compressés en gzip).
/// </summary>
/// <summary>Un compte, tel qu'il est en base.</summary>
public sealed record UserRow(long Id, string Username, string PasswordHash, string? RecoveryHash, string? Email,
    string? ResetHash, DateTimeOffset? ResetExpires);

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
            -- Échecs de connexion par nom d'utilisateur (comptes inexistants compris : rien n'indique
            -- si un nom existe), pour ralentir les essais en boucle sur un même compte.
            CREATE TABLE IF NOT EXISTS auth_failures (
                key          TEXT PRIMARY KEY,
                count        INTEGER NOT NULL,
                last_at      TEXT NOT NULL,
                locked_until TEXT
            );
            """);
        // Bases créées avant les codes de secours et l'adresse e-mail.
        foreach (var column in new[] { "recovery_hash", "email", "reset_hash", "reset_expires" })
            if (!HasColumn(c, "users", column))
                Exec(c, $"ALTER TABLE users ADD COLUMN {column} TEXT;");
    }

    static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = Cmd(c, $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $n;", ("$n", column));
        return cmd.ExecuteScalar() is not null;
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

    static string Now() => Iso(DateTimeOffset.UtcNow);

    // Toujours en UTC et au même format : les dates se comparent comme du texte.
    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    // ---- Comptes ----

    /// <summary>Crée le compte ; null si le nom est déjà pris.</summary>
    public long? CreateUser(string username, string passwordHash, string? recoveryHash = null, string? email = null)
    {
        using var c = Open();
        try
        {
            using var cmd = Cmd(c, """
                INSERT INTO users (username, password_hash, recovery_hash, email, created_at) VALUES ($u, $p, $r, $e, $t) RETURNING id;
                """, ("$u", username), ("$p", passwordHash), ("$r", recoveryHash), ("$e", email), ("$t", Now()));
            return (long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return null; } // UNIQUE
    }

    public UserRow? FindUser(string username)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT id, username, password_hash, recovery_hash, email, reset_hash, reset_expires FROM users WHERE username = $u;
            """, ("$u", username));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new UserRow(r.GetInt64(0), r.GetString(1), r.GetString(2), Text(3), Text(4), Text(5),
            Text(6) is { } exp ? DateTimeOffset.Parse(exp, CultureInfo.InvariantCulture) : null);
    }

    public IReadOnlyList<(string Username, string? Email, string CreatedAt)> ListUsers()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT username, email, created_at FROM users ORDER BY username;");
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string?, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>Adresse e-mail du compte (null : aucune).</summary>
    public void SetEmail(long userId, string? email)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE users SET email = $e WHERE id = $id;", ("$e", email), ("$id", userId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Code de réinitialisation envoyé par e-mail (null : effacé), valable jusqu'à <paramref name="expires"/>.</summary>
    public void SetResetCode(long userId, string? resetHash, DateTimeOffset? expires)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE users SET reset_hash = $h, reset_expires = $x WHERE id = $id;",
            ("$h", resetHash), ("$x", expires is { } x ? Iso(x) : null), ("$id", userId));
        cmd.ExecuteNonQuery();
    }

    public void SetRecoveryHash(long userId, string recoveryHash)
    {
        using var c = Open();
        using var cmd = Cmd(c, "UPDATE users SET recovery_hash = $r WHERE id = $id;", ("$r", recoveryHash), ("$id", userId));
        cmd.ExecuteNonQuery();
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

    /// <summary>Compte du jeton, s'il a servi depuis moins de <paramref name="maxIdle"/> (sinon il a expiré).</summary>
    public long? UserForToken(string tokenHash, TimeSpan maxIdle)
    {
        using var c = Open();
        var now = DateTimeOffset.UtcNow;
        using var cmd = Cmd(c, "UPDATE tokens SET last_used_at = $t WHERE hash = $h AND last_used_at > $min RETURNING user_id;",
            ("$h", tokenHash), ("$t", Iso(now)), ("$min", Iso(now - maxIdle)));
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Oublie les jetons qui n'ont pas servi depuis <paramref name="maxIdle"/>.</summary>
    public int DeleteExpiredTokens(TimeSpan maxIdle)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM tokens WHERE last_used_at <= $min;", ("$min", Iso(DateTimeOffset.UtcNow - maxIdle)));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Déconnecte tous les appareils du compte.</summary>
    public void DeleteAllTokens(long userId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM tokens WHERE user_id = $u;", ("$u", userId));
        cmd.ExecuteNonQuery();
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

    // ---- Sauvegardes ----

    /// <summary>Copie cohérente de la base (même serveur allumé) dans <paramref name="directory"/>,
    /// nommée par la date du jour (« velom3d-2026-11-07.db », remplacée si elle existe), puis ne garde
    /// que les <paramref name="keep"/> plus récentes. Renvoie le chemin de la copie.</summary>
    public string Backup(string directory, int keep)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"velom3d-{DateTime.Now:yyyy-MM-dd}.db");
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var c = Open())
        using (var cmd = Cmd(c, "VACUUM INTO $p;", ("$p", tmp)))
            cmd.ExecuteNonQuery();
        File.Move(tmp, path, overwrite: true);
        foreach (var old in Directory.GetFiles(directory, "velom3d-????-??-??.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Math.Max(1, keep)))
            File.Delete(old);
        return path;
    }

    // ---- Échecs de connexion ----

    /// <summary>Jusqu'à quand cette clé (« login:nom ») est bloquée ; null si elle ne l'est pas.</summary>
    public DateTimeOffset? LockedUntil(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT locked_until FROM auth_failures WHERE key = $k;", ("$k", key));
        return cmd.ExecuteScalar() is string s && DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) is var until
               && until > DateTimeOffset.UtcNow ? until : null;
    }

    /// <summary>Note un échec. À partir de <paramref name="freeAttempts"/> échecs de suite, la clé est
    /// bloquée 1 min, puis 2, 4, 8… jusqu'à <paramref name="maxLock"/>. Les échecs sont oubliés après
    /// <paramref name="forgetAfter"/> sans nouvel essai. Renvoie la fin du blocage, s'il y en a un.</summary>
    public DateTimeOffset? RecordFailure(string key, int freeAttempts, TimeSpan maxLock, TimeSpan forgetAfter) =>
        RecordFailure(key, freeAttempts, maxLock, forgetAfter, out _);

    /// <param name="justLocked">Cet échec déclenche le premier blocage de la série.</param>
    public DateTimeOffset? RecordFailure(string key, int freeAttempts, TimeSpan maxLock, TimeSpan forgetAfter, out bool justLocked)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        int count = 1;
        using (var get = Cmd(c, "SELECT count, last_at FROM auth_failures WHERE key = $k;", ("$k", key)))
        {
            get.Transaction = tx;
            using var r = get.ExecuteReader();
            if (r.Read() && DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture) > now - forgetAfter)
                count = r.GetInt32(0) + 1;
        }
        DateTimeOffset? until = null;
        justLocked = count == freeAttempts;
        if (count >= freeAttempts)
        {
            var minutes = Math.Min(Math.Pow(2, Math.Min(count - freeAttempts, 20)), maxLock.TotalMinutes);
            until = now + TimeSpan.FromMinutes(minutes);
        }
        using (var set = Cmd(c, """
            INSERT INTO auth_failures (key, count, last_at, locked_until) VALUES ($k, $n, $t, $l)
            ON CONFLICT (key) DO UPDATE SET count = excluded.count, last_at = excluded.last_at, locked_until = excluded.locked_until;
            """, ("$k", key), ("$n", count), ("$t", Iso(now)), ("$l", until is { } u ? Iso(u) : null)))
        {
            set.Transaction = tx;
            set.ExecuteNonQuery();
        }
        tx.Commit();
        return until;
    }

    /// <summary>Oublie les échecs anciens (plus de 24 h, sans blocage en cours).</summary>
    public int DeleteOldFailures()
    {
        using var c = Open();
        var now = DateTimeOffset.UtcNow;
        using var cmd = Cmd(c, "DELETE FROM auth_failures WHERE last_at <= $old AND (locked_until IS NULL OR locked_until <= $now);",
            ("$old", Iso(now - TimeSpan.FromDays(1))), ("$now", Iso(now)));
        return cmd.ExecuteNonQuery();
    }

    public void ClearFailures(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "DELETE FROM auth_failures WHERE key = $k;", ("$k", key));
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
