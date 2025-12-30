using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Injects a series of PRAGMA on each connection starts.
/// </summary>
public class PragmaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ILogger _logger;
    private readonly int? _cacheSize;
    private readonly string _lockingMode;
    private readonly int? _journalSizeLimit;
    private readonly int _tempStoreMode;
    private readonly int _syncMode;
    private readonly IDictionary<string, string> _customPragma;

    private readonly IReadOnlyList<string> _extraPragmaStatements;

    // Log the effective pragma values once per process start (avoid noisy logs)
    private static int _pragmaVerifyLogged;

    public PragmaConnectionInterceptor(
        ILogger logger,
        int? cacheSize,
        string lockingMode,
        int? journalSizeLimit,
        int tempStoreMode,
        int syncMode,
        IDictionary<string, string> customPragma)
    {
        _logger = logger;
        _cacheSize = cacheSize;
        _lockingMode = lockingMode;
        _journalSizeLimit = journalSizeLimit;
        _tempStoreMode = tempStoreMode;
        _syncMode = syncMode;
        _customPragma = customPragma;

        InitialCommand = BuildCommandText();

        // Load extra pragmas from env/file/auto-discovered default file. Executed per-connection (pool-safe).
        _extraPragmaStatements = LoadExtraPragmas(_logger);

        _logger.LogInformation("SQLITE connection pragma command set to: \r\n{PragmaCommand}", InitialCommand);

        if (_extraPragmaStatements.Count > 0)
        {
            _logger.LogInformation(
                "SQLITE extra pragma statements enabled ({Count}). Will execute on each connection open: {Pragmas}",
                _extraPragmaStatements.Count,
                string.Join(" ", _extraPragmaStatements));
        }
    }

    private string? InitialCommand { get; set; }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);

        ExecuteInitialCommand(connection);
        ExecuteExtraPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);

        await ExecuteInitialCommandAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteExtraPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private void ExecuteInitialCommand(DbConnection connection)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
        command.CommandText = InitialCommand;
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
        command.ExecuteNonQuery();
    }

    private async Task ExecuteInitialCommandAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
            command.CommandText = InitialCommand;
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ExecuteExtraPragmas(DbConnection connection)
    {
        if (_extraPragmaStatements.Count == 0)
        {
            return;
        }

        foreach (var stmt in _extraPragmaStatements)
        {
            using var cmd = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
            cmd.CommandText = stmt;
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
            cmd.ExecuteNonQuery();
        }

        // Definitive proof: log effective values from the SAME connection after applying
        LogEffectivePragmasOnce(connection);
    }

    private async Task ExecuteExtraPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_extraPragmaStatements.Count == 0)
        {
            return;
        }

        foreach (var stmt in _extraPragmaStatements)
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
                cmd.CommandText = stmt;
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Definitive proof: log effective values from the SAME connection after applying
        await LogEffectivePragmasOnceAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private void LogEffectivePragmasOnce(DbConnection connection)
    {
        if (Interlocked.Exchange(ref _pragmaVerifyLogged, 1) != 0)
        {
            return;
        }

        try
        {
            string? Get(string pragma)
            {
                using var cmd = connection.CreateCommand();
#pragma warning disable CA2100
                cmd.CommandText = $"PRAGMA {pragma};";
#pragma warning restore CA2100
                var val = cmd.ExecuteScalar();
                return val?.ToString();
            }

            var journalMode = Get("journal_mode");
            var synchronous = Get("synchronous");
            var tempStore = Get("temp_store");
            var cacheSize = Get("cache_size");
            var mmapSize = Get("mmap_size");
            var cacheSpill = Get("cache_spill");
            var threads = Get("threads");
            var walAutoCheckpoint = Get("wal_autocheckpoint");

            _logger.LogInformation(
                "SQLite PRAGMA verify (same Jellyfin connection, after apply): journal_mode={JournalMode}, synchronous={Synchronous}, temp_store={TempStore}, cache_size={CacheSize}, mmap_size={MmapSize}, cache_spill={CacheSpill}, threads={Threads}, wal_autocheckpoint={WalAutoCheckpoint}",
                journalMode,
                synchronous,
                tempStore,
                cacheSize,
                mmapSize,
                cacheSpill,
                threads,
                walAutoCheckpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite PRAGMA verify failed");
        }
    }

    private async Task LogEffectivePragmasOnceAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pragmaVerifyLogged, 1) != 0)
        {
            return;
        }

        try
        {
            async Task<string?> GetAsync(string pragma)
            {
                var cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100
                    cmd.CommandText = $"PRAGMA {pragma};";
#pragma warning restore CA2100
                    var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return val?.ToString();
                }
            }

            var journalMode = await GetAsync("journal_mode").ConfigureAwait(false);
            var synchronous = await GetAsync("synchronous").ConfigureAwait(false);
            var tempStore = await GetAsync("temp_store").ConfigureAwait(false);
            var cacheSize = await GetAsync("cache_size").ConfigureAwait(false);
            var mmapSize = await GetAsync("mmap_size").ConfigureAwait(false);
            var cacheSpill = await GetAsync("cache_spill").ConfigureAwait(false);
            var threads = await GetAsync("threads").ConfigureAwait(false);
            var walAutoCheckpoint = await GetAsync("wal_autocheckpoint").ConfigureAwait(false);

            _logger.LogInformation(
                "SQLite PRAGMA verify (same Jellyfin connection, after apply): journal_mode={JournalMode}, synchronous={Synchronous}, temp_store={TempStore}, cache_size={CacheSize}, mmap_size={MmapSize}, cache_spill={CacheSpill}, threads={Threads}, wal_autocheckpoint={WalAutoCheckpoint}",
                journalMode,
                synchronous,
                tempStore,
                cacheSize,
                mmapSize,
                cacheSpill,
                threads,
                walAutoCheckpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQLite PRAGMA verify failed");
        }
    }

    private string BuildCommandText()
    {
        var sb = new StringBuilder();
        if (_cacheSize.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA cache_size={_cacheSize.Value};");
        }

        if (!string.IsNullOrWhiteSpace(_lockingMode))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA locking_mode={_lockingMode};");
        }

        if (_journalSizeLimit.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA journal_size_limit={_journalSizeLimit};");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA synchronous={_syncMode};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA temp_store={_tempStoreMode};");

        foreach (var item in _customPragma)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PRAGMA {item.Key}={item.Value};");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Loads extra PRAGMA statements from:
    /// - env var: JELLYFIN_SQLITE_PRAGMAS (semicolon separated)
    /// - env var: JELLYFIN_SQLITE_PRAGMAS_FILE (path to a file)
    /// - auto-discovered default file(s) (no env required):
    ///   {JELLYFIN_DATA_DIR}/pragmas.sql (and a couple alternates)
    /// </summary>
    private static IReadOnlyList<string> LoadExtraPragmas(ILogger logger)
    {
        // 1) Direct env var (highest priority)
        var raw = Environment.GetEnvironmentVariable("JELLYFIN_SQLITE_PRAGMAS");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            logger.LogInformation("Loading SQLite extra PRAGMAs from env var JELLYFIN_SQLITE_PRAGMAS.");
            return ParsePragmaStatements(logger, raw);
        }

        // 2) Env var file path override
        var filePath = Environment.GetEnvironmentVariable("JELLYFIN_SQLITE_PRAGMAS_FILE");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var fromFile = TryReadFile(logger, filePath, logWhenMissing: true);
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                logger.LogInformation("Loading SQLite extra PRAGMAs from JELLYFIN_SQLITE_PRAGMAS_FILE: {Path}", filePath);
                return ParsePragmaStatements(logger, fromFile);
            }

            return Array.Empty<string>();
        }

        // 3) Auto-discover default file(s) (no env required)
        foreach (var candidate in GetDefaultPragmaFileCandidates())
        {
            var fromFile = TryReadFile(logger, candidate, logWhenMissing: false);
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                logger.LogInformation("Loading SQLite extra PRAGMAs from default file: {Path}", candidate);
                return ParsePragmaStatements(logger, fromFile);
            }
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetDefaultPragmaFileCandidates()
    {
        // Official image layout sets JELLYFIN_DATA_DIR=/config, but we also provide a fallback.
        var dataDir = Environment.GetEnvironmentVariable("JELLYFIN_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = "/config";
        }

        // Primary: /config/pragmas.sql
        yield return Path.Combine(dataDir, "pragmas.sql");

        // Alternates (optional but convenient)
        yield return Path.Combine(dataDir, "sqlite-pragmas.sql");
        yield return Path.Combine(dataDir, "config", "pragmas.sql");
        yield return Path.Combine(dataDir, "config", "sqlite-pragmas.sql");
    }

    private static string? TryReadFile(ILogger logger, string path, bool logWhenMissing)
    {
        try
        {
            if (!File.Exists(path))
            {
                if (logWhenMissing)
                {
                    logger.LogWarning("SQLite PRAGMA file was set but does not exist: {Path}", path);
                }

                return null;
            }

            // Guardrail: don’t allow huge files
            var info = new FileInfo(path);
            const long maxBytes = 256 * 1024; // 256KB
            if (info.Length > maxBytes)
            {
                logger.LogWarning(
                    "SQLite PRAGMA file is too large ({Size} bytes). Max allowed is {Max} bytes. Ignoring: {Path}",
                    info.Length,
                    maxBytes,
                    path);
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read SQLite PRAGMA file: {Path}", path);
            return null;
        }
    }

    private static IReadOnlyList<string> ParsePragmaStatements(ILogger logger, string raw)
    {
        // Strip comment-only lines + blank lines
        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !IsFullLineComment(l))
            .ToArray();

        var normalized = string.Join("\n", lines);

        // Split into statements by ';'
        var parts = normalized.Split(';')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var statements = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            // Allow either "PRAGMA foo=bar" OR "foo=bar"
            var stmt = p.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase) ? p : $"PRAGMA {p}";

            // Only allow PRAGMA commands (safety)
            if (!stmt.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Ignoring non-PRAGMA SQLite statement from extra pragmas: {Statement}", p);
                continue;
            }

            // Ensure it ends with ';' (CA1865: use char overload)
            if (!stmt.EndsWith(';'))
            {
                stmt += ";";
            }

            statements.Add(stmt);
        }

        // Deduplicate while preserving order
        return statements
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsFullLineComment(string line)
    {
        // CA1865: use char overload where applicable
        if (line.StartsWith('#'))
        {
            return true;
        }

        // SQL comment: "-- ..."
        // Avoid StartsWith("--") string overload by checking 2 chars.
        return line.Length >= 2 && line[0] == '-' && line[1] == '-';
    }
}
