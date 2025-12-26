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

    /// <summary>
    /// Initializes a new instance of the <see cref="PragmaConnectionInterceptor"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cacheSize">Cache size.</param>
    /// <param name="lockingMode">Locking mode.</param>
    /// <param name="journalSizeLimit">Journal Size.</param>
    /// <param name="tempStoreMode">The https://sqlite.org/pragma.html#pragma_temp_store pragma.</param>
    /// <param name="syncMode">The https://sqlite.org/pragma.html#pragma_synchronous pragma.</param>
    /// <param name="customPragma">A list of custom provided Pragma in the list of CustomOptions starting with "#PRAGMA:".</param>
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

        // Load extra pragmas from env/file. These are executed per-connection (pool-safe).
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

    /// <inheritdoc/>
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);

        ExecuteInitialCommand(connection);
        ExecuteExtraPragmas(connection);
    }

    /// <inheritdoc/>
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
    /// - file:    JELLYFIN_SQLITE_PRAGMAS_FILE (raw text; semicolon separated; supports SQL-style comments)
    /// </summary>
    private static IReadOnlyList<string> LoadExtraPragmas(ILogger logger)
    {
        // Env var containing PRAGMA statements or simple "key=value" entries separated by semicolons.
        var raw = Environment.GetEnvironmentVariable("JELLYFIN_SQLITE_PRAGMAS");

        // Optional file path containing statements (recommended for easy tuning without rebuild).
        var filePath = Environment.GetEnvironmentVariable("JELLYFIN_SQLITE_PRAGMAS_FILE");

        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                if (File.Exists(filePath))
                {
                    raw = File.ReadAllText(filePath);
                }
                else
                {
                    logger.LogWarning("JELLYFIN_SQLITE_PRAGMAS_FILE was set but file does not exist: {Path}", filePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read JELLYFIN_SQLITE_PRAGMAS_FILE: {Path}", filePath);
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        // Strip common comment-only lines (we still allow inline comments; SQLite itself ignores them poorly in PRAGMA,
        // so we keep it simple and remove full-line comments).
        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !l.StartsWith("--", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("#", StringComparison.Ordinal))
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

            // Ensure it ends with ';'
            if (!stmt.EndsWith(";", StringComparison.Ordinal))
            {
                stmt += ";";
            }

            statements.Add(stmt);
        }

        // Deduplicate while preserving order
        var deduped = statements.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return deduped;
    }
}
