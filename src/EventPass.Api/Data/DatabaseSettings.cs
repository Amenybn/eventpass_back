using Npgsql;

namespace EventPass.Api.Data;

/// <summary>
/// Choix du moteur à partir des variables d'environnement :
///  - DATABASE_URL (postgres://user:pass@hote:5432/base) ou DB_HOST (+ DB_PORT, DB_NAME, DB_USER, DB_PASSWORD) : PostgreSQL ;
///  - sinon SQLite dans SQLITE_PATH (défaut /app/data/eventpass.db), avec repli dans /tmp si le dossier n'est pas inscriptible.
/// </summary>
public sealed record DatabaseSettings(string Provider, string ConnectionString, string Location, bool Persistent, string? Warning)
{
    public const string PostgreSql = "PostgreSQL";
    public const string Sqlite = "SQLite";

    public bool IsPostgres => Provider == PostgreSql;

    public static DatabaseSettings From(IConfiguration cfg)
    {
        var url = cfg["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return FromUrl(url);

        var host = cfg["DB_HOST"];
        if (!string.IsNullOrWhiteSpace(host))
        {
            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = host.Trim(),
                Port = int.TryParse(cfg["DB_PORT"], out var port) ? port : 5432,
                Database = Or(cfg["DB_NAME"], "eventpass"),
                Username = Or(cfg["DB_USER"], "eventpass"),
                Password = cfg["DB_PASSWORD"] ?? "",
            };
            return Postgres(csb);
        }

        return FromSqlite(Or(cfg["SQLITE_PATH"], "/app/data/eventpass.db"));
    }

    private static DatabaseSettings FromUrl(string url)
    {
        var uri = new Uri(url.Trim());
        var credentials = uri.UserInfo.Split(':', 2);
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "",
        };
        var sslMode = System.Web.HttpUtility.ParseQueryString(uri.Query)["sslmode"];
        if (Enum.TryParse<SslMode>(sslMode, ignoreCase: true, out var mode)) csb.SslMode = mode;
        return Postgres(csb);
    }

    private static DatabaseSettings Postgres(NpgsqlConnectionStringBuilder csb) =>
        new(PostgreSql, csb.ConnectionString, $"{csb.Host}:{csb.Port}/{csb.Database}", true, null);

    private static DatabaseSettings FromSqlite(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/";
        string? warning = null;

        if (!CanWrite(directory))
        {
            var fallback = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
            warning = $"Le dossier {directory} n'est pas inscriptible : base SQLite déplacée dans {fallback} " +
                      "(données perdues au redémarrage). Montez un volume sur ce dossier ou configurez PostgreSQL (DB_HOST).";
            path = fallback;
            directory = Path.GetDirectoryName(fallback)!;
        }

        var persistent = !IsTemporary(directory) && IsMountPoint(directory) != false;
        if (warning is null && !persistent)
        {
            warning = $"Aucun volume monté sur {directory} : les données SQLite seront perdues si le conteneur est recréé. " +
                      "Montez un volume ou configurez PostgreSQL (DB_HOST).";
        }

        return new(Sqlite, $"Data Source={path}", path, persistent, warning);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTemporary(string directory)
    {
        var tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/', '\\');
        return Path.GetFullPath(directory).TrimEnd('/', '\\').StartsWith(tmp, StringComparison.Ordinal);
    }

    /// <summary>true/false sous Linux (lecture de /proc/mounts), null si on ne peut pas savoir.</summary>
    private static bool? IsMountPoint(string directory)
    {
        const string mounts = "/proc/mounts";
        if (!File.Exists(mounts)) return null;
        var full = Path.GetFullPath(directory).TrimEnd('/');
        return File.ReadLines(mounts)
            .Select(line => line.Split(' '))
            .Any(parts => parts.Length > 1 && parts[1] == full);
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
