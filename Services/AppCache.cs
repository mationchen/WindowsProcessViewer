using Microsoft.Data.Sqlite;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 一个应用（可执行文件）的缓存记录：文件信息 + 判断结论 + AI 结论 + 出现次数。
/// 键优先用文件路径，读不到路径时退回进程名。
/// </summary>
public sealed class CachedApp
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long FileSize { get; set; }
    public long WriteTicks { get; set; }

    public string Company { get; set; } = "";
    public string Product { get; set; } = "";
    public string Description { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public string Signature { get; set; } = "";

    public string Software { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string CanClose { get; set; } = "";
    public string CloseMethod { get; set; } = "";
    public string Category { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "";

    public string AiSoftware { get; set; } = "";
    public string AiPurpose { get; set; } = "";
    public string AiLaunchedBy { get; set; } = "";
    public string AiCanClose { get; set; } = "";
    public string AiCloseMethod { get; set; } = "";
    public string AiRisk { get; set; } = "";
    public string AiSummary { get; set; } = "";
    public string AiDetail { get; set; } = "";
    public string AiModel { get; set; } = "";

    public int SeenCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>本进程内被修改过，等待 Flush 写盘（不持久化）。</summary>
    public bool Dirty { get; set; }

    public bool HasAi => !string.IsNullOrWhiteSpace(AiSummary) || !string.IsNullOrWhiteSpace(AiDetail);
}

/// <summary>
/// SQLite 缓存（exe 同目录 cache.db）。一次启动只读一次表，之后全在内存里判断，
/// 只有"首次见到的应用"和"AI 结果/文件信息变化"才写盘，避免每次刷新都触发几百条 SQL。
/// 缓存损坏/被占用时自动降级为纯内存模式，不影响进程列表功能。
/// </summary>
public sealed class AppCache : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS AppInfo (
            Key TEXT PRIMARY KEY,
            Name TEXT NOT NULL DEFAULT '',
            Path TEXT NOT NULL DEFAULT '',
            FileSize INTEGER NOT NULL DEFAULT 0,
            WriteTicks INTEGER NOT NULL DEFAULT 0,
            Company TEXT NOT NULL DEFAULT '',
            Product TEXT NOT NULL DEFAULT '',
            Description TEXT NOT NULL DEFAULT '',
            FileVersion TEXT NOT NULL DEFAULT '',
            Signature TEXT NOT NULL DEFAULT '',
            Software TEXT NOT NULL DEFAULT '',
            Purpose TEXT NOT NULL DEFAULT '',
            CanClose TEXT NOT NULL DEFAULT '',
            CloseMethod TEXT NOT NULL DEFAULT '',
            Category TEXT NOT NULL DEFAULT '',
            Risk TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            Source TEXT NOT NULL DEFAULT '',
            AiSoftware TEXT NOT NULL DEFAULT '',
            AiPurpose TEXT NOT NULL DEFAULT '',
            AiLaunchedBy TEXT NOT NULL DEFAULT '',
            AiCanClose TEXT NOT NULL DEFAULT '',
            AiCloseMethod TEXT NOT NULL DEFAULT '',
            AiRisk TEXT NOT NULL DEFAULT '',
            AiSummary TEXT NOT NULL DEFAULT '',
            AiDetail TEXT NOT NULL DEFAULT '',
            AiModel TEXT NOT NULL DEFAULT '',
            SeenCount INTEGER NOT NULL DEFAULT 0,
            FirstSeen TEXT NOT NULL DEFAULT '',
            LastSeen TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS IX_AppInfo_Name ON AppInfo(Name);
        """;

    private readonly Dictionary<string, CachedApp> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private SqliteConnection? _connection;

    public static string DatabasePath => System.IO.Path.Combine(AppContext.BaseDirectory, "cache.db");

    public bool Enabled { get; private set; }
    public string? LastError { get; private set; }
    public int Count { get { lock (_gate) return _apps.Count; } }

    public AppCache(bool enabled = true, string? databasePath = null)
    {
        if (!enabled)
            return;

        try
        {
            _connection = new SqliteConnection($"Data Source={databasePath ?? DatabasePath}");
            _connection.Open();
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteScalar();
            }

            using (var schema = _connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            LoadAll();
            Enabled = true;
            Log.Info($"SQLite 缓存已加载：{_apps.Count} 个应用 → {databasePath ?? DatabasePath}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("SQLite 缓存初始化失败，本次使用纯内存模式", ex);
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>缓存键：路径优先（同名不同版本/不同目录也算不同应用），无路径时退回进程名。</summary>
    public static string BuildKey(string path, string processName)
        => !string.IsNullOrWhiteSpace(path)
            ? "path:" + path.Trim().ToLowerInvariant()
            : "name:" + (processName ?? "").Trim().ToLowerInvariant();

    public CachedApp GetOrCreate(string key, string name, string path)
    {
        lock (_gate)
        {
            if (_apps.TryGetValue(key, out var existing))
            {
                if (_seenThisSession.Add(key))
                {
                    existing.SeenCount++;
                    existing.LastSeen = DateTime.Now;
                    existing.Dirty = true;
                }

                return existing;
            }

            var created = new CachedApp
            {
                Key = key,
                Name = name,
                Path = path,
                FirstSeen = DateTime.Now,
                LastSeen = DateTime.Now,
                SeenCount = 1,
                Dirty = true,
            };
            _apps[key] = created;
            _seenThisSession.Add(key);
            return created;
        }
    }

    public CachedApp? Find(string key)
    {
        lock (_gate)
            return _apps.TryGetValue(key, out var app) ? app : null;
    }

    public void MarkDirty(CachedApp app) => app.Dirty = true;

    /// <summary>把标记为 Dirty 的记录写回数据库；失败只记录一次，不影响界面。</summary>
    public void Flush()
    {
        if (!Enabled || _connection is null)
            return;

        List<CachedApp> pending;
        lock (_gate)
        {
            pending = _apps.Values.Where(a => a.Dirty).ToList();
            foreach (var app in pending)
                app.Dirty = false;
        }

        if (pending.Count == 0)
            return;

        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var app in pending)
                Upsert(app, transaction);
            transaction.Commit();
            Log.Info($"缓存写回 {pending.Count} 条");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error($"缓存写回失败（{pending.Count} 条）", ex);

            lock (_gate)
            {
                foreach (var app in pending)
                    app.Dirty = true;
            }
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            var count = _apps.Count;
            _apps.Clear();
            _seenThisSession.Clear();

            if (Enabled && _connection is not null)
            {
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "DELETE FROM AppInfo;";
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Error("清空缓存失败", ex);
                }
            }

            Log.Info($"已清空应用缓存：{count} 条");
            return count;
        }
    }

    private void LoadAll()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM AppInfo;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var app = new CachedApp
            {
                Key = reader.GetString(reader.GetOrdinal("Key")),
                Name = GetString(reader, "Name"),
                Path = GetString(reader, "Path"),
                FileSize = reader.GetInt64(reader.GetOrdinal("FileSize")),
                WriteTicks = reader.GetInt64(reader.GetOrdinal("WriteTicks")),
                Company = GetString(reader, "Company"),
                Product = GetString(reader, "Product"),
                Description = GetString(reader, "Description"),
                FileVersion = GetString(reader, "FileVersion"),
                Signature = GetString(reader, "Signature"),
                Software = GetString(reader, "Software"),
                Purpose = GetString(reader, "Purpose"),
                CanClose = GetString(reader, "CanClose"),
                CloseMethod = GetString(reader, "CloseMethod"),
                Category = GetString(reader, "Category"),
                Risk = GetString(reader, "Risk"),
                Notes = GetString(reader, "Notes"),
                Source = GetString(reader, "Source"),
                AiSoftware = GetString(reader, "AiSoftware"),
                AiPurpose = GetString(reader, "AiPurpose"),
                AiLaunchedBy = GetString(reader, "AiLaunchedBy"),
                AiCanClose = GetString(reader, "AiCanClose"),
                AiCloseMethod = GetString(reader, "AiCloseMethod"),
                AiRisk = GetString(reader, "AiRisk"),
                AiSummary = GetString(reader, "AiSummary"),
                AiDetail = GetString(reader, "AiDetail"),
                AiModel = GetString(reader, "AiModel"),
                SeenCount = (int)reader.GetInt64(reader.GetOrdinal("SeenCount")),
                FirstSeen = ParseTime(GetString(reader, "FirstSeen")),
                LastSeen = ParseTime(GetString(reader, "LastSeen")),
            };

            if (!string.IsNullOrWhiteSpace(app.Key))
                _apps[app.Key] = app;
        }
    }

    private static string GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }

    private static DateTime ParseTime(string text)
        => DateTime.TryParse(text, out var value) ? value : DateTime.MinValue;

    private static void Upsert(CachedApp app, SqliteTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AppInfo (
                Key, Name, Path, FileSize, WriteTicks, Company, Product, Description, FileVersion, Signature,
                Software, Purpose, CanClose, CloseMethod, Category, Risk, Notes, Source,
                AiSoftware, AiPurpose, AiLaunchedBy, AiCanClose, AiCloseMethod, AiRisk, AiSummary, AiDetail, AiModel,
                SeenCount, FirstSeen, LastSeen)
            VALUES (
                $key, $name, $path, $size, $ticks, $company, $product, $description, $version, $signature,
                $software, $purpose, $canClose, $closeMethod, $category, $risk, $notes, $source,
                $aiSoftware, $aiPurpose, $aiLaunchedBy, $aiCanClose, $aiCloseMethod, $aiRisk, $aiSummary, $aiDetail, $aiModel,
                $seenCount, $firstSeen, $lastSeen)
            ON CONFLICT(Key) DO UPDATE SET
                Name = excluded.Name,
                Path = excluded.Path,
                FileSize = excluded.FileSize,
                WriteTicks = excluded.WriteTicks,
                Company = excluded.Company,
                Product = excluded.Product,
                Description = excluded.Description,
                FileVersion = excluded.FileVersion,
                Signature = excluded.Signature,
                Software = excluded.Software,
                Purpose = excluded.Purpose,
                CanClose = excluded.CanClose,
                CloseMethod = excluded.CloseMethod,
                Category = excluded.Category,
                Risk = excluded.Risk,
                Notes = excluded.Notes,
                Source = excluded.Source,
                AiSoftware = excluded.AiSoftware,
                AiPurpose = excluded.AiPurpose,
                AiLaunchedBy = excluded.AiLaunchedBy,
                AiCanClose = excluded.AiCanClose,
                AiCloseMethod = excluded.AiCloseMethod,
                AiRisk = excluded.AiRisk,
                AiSummary = excluded.AiSummary,
                AiDetail = excluded.AiDetail,
                AiModel = excluded.AiModel,
                SeenCount = excluded.SeenCount,
                LastSeen = excluded.LastSeen;
            """;

        command.Parameters.AddWithValue("$key", app.Key);
        command.Parameters.AddWithValue("$name", app.Name ?? "");
        command.Parameters.AddWithValue("$path", app.Path ?? "");
        command.Parameters.AddWithValue("$size", app.FileSize);
        command.Parameters.AddWithValue("$ticks", app.WriteTicks);
        command.Parameters.AddWithValue("$company", app.Company ?? "");
        command.Parameters.AddWithValue("$product", app.Product ?? "");
        command.Parameters.AddWithValue("$description", app.Description ?? "");
        command.Parameters.AddWithValue("$version", app.FileVersion ?? "");
        command.Parameters.AddWithValue("$signature", app.Signature ?? "");
        command.Parameters.AddWithValue("$software", app.Software ?? "");
        command.Parameters.AddWithValue("$purpose", app.Purpose ?? "");
        command.Parameters.AddWithValue("$canClose", app.CanClose ?? "");
        command.Parameters.AddWithValue("$closeMethod", app.CloseMethod ?? "");
        command.Parameters.AddWithValue("$category", app.Category ?? "");
        command.Parameters.AddWithValue("$risk", app.Risk ?? "");
        command.Parameters.AddWithValue("$notes", app.Notes ?? "");
        command.Parameters.AddWithValue("$source", app.Source ?? "");
        command.Parameters.AddWithValue("$aiSoftware", app.AiSoftware ?? "");
        command.Parameters.AddWithValue("$aiPurpose", app.AiPurpose ?? "");
        command.Parameters.AddWithValue("$aiLaunchedBy", app.AiLaunchedBy ?? "");
        command.Parameters.AddWithValue("$aiCanClose", app.AiCanClose ?? "");
        command.Parameters.AddWithValue("$aiCloseMethod", app.AiCloseMethod ?? "");
        command.Parameters.AddWithValue("$aiRisk", app.AiRisk ?? "");
        command.Parameters.AddWithValue("$aiSummary", app.AiSummary ?? "");
        command.Parameters.AddWithValue("$aiDetail", app.AiDetail ?? "");
        command.Parameters.AddWithValue("$aiModel", app.AiModel ?? "");
        command.Parameters.AddWithValue("$seenCount", app.SeenCount);
        command.Parameters.AddWithValue("$firstSeen", app.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss"));
        command.Parameters.AddWithValue("$lastSeen", app.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    /// <summary>把一条进程记录的判断结果 + AI 结果写进对应缓存项。</summary>
    public void UpdateFromRecord(ProcessRecord record, string? aiModel = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(record.CacheKey))
            return;

        var app = GetOrCreate(record.CacheKey, record.Name, record.Path);
        app.Name = record.Name;
        app.Path = record.Path;

        if (!string.IsNullOrWhiteSpace(record.Software))
        {
            app.Software = record.Software;
            app.Purpose = record.Purpose;
            app.CanClose = record.CanClose;
            app.CloseMethod = record.CloseMethod;
            app.Category = record.Category;
            app.Risk = record.Risk;
            app.Notes = record.Notes;
            app.Source = record.AnalyzeSource;
        }

        if (record.HasAi)
        {
            app.AiSoftware = record.AiSoftware;
            app.AiPurpose = record.AiPurpose;
            app.AiLaunchedBy = record.AiLaunchedBy;
            app.AiCanClose = record.AiCanClose;
            app.AiCloseMethod = record.AiCloseMethod;
            app.AiRisk = record.AiRisk;
            app.AiSummary = record.AiSummary;
            app.AiDetail = record.AiDetail;
            app.AiModel = aiModel ?? app.AiModel;
        }

        app.Dirty = true;
    }

    public void Dispose()
    {
        try
        {
            Flush();
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
