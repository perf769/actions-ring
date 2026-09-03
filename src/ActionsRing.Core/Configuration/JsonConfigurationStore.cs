using System.Text.Json;

namespace ActionsRing.Core.Configuration;

public enum ConfigurationLoadStatus
{
    Loaded,
    CreatedDefault,
    Migrated,
    RecoveredFromBackup,
    RecoveredCorrupt,
    UnsupportedVersion,
}

public sealed record ConfigurationLoadResult(
    ActionsRingConfiguration Configuration,
    ConfigurationLoadStatus Status,
    IReadOnlyList<ConfigurationIssue> Issues,
    string? RecoveredFilePath = null,
    Exception? Error = null);

public interface IConfigurationStore
{
    string SettingsPath { get; }

    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ActionsRingConfiguration configuration, CancellationToken cancellationToken = default);
}

public static class ConfigurationPaths
{
    public static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "ActionsRing", "settings.json");
    }
}

/// <summary>
/// Version-aware JSON persistence with same-volume atomic replacement, last-known-good backup,
/// corrupt-file quarantine, and safe handling of files written by a newer application version.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public JsonConfigurationStore(string? settingsPath = null)
    {
        SettingsPath = Path.GetFullPath(settingsPath ?? ConfigurationPaths.GetDefaultSettingsPath());
    }

    public string SettingsPath { get; }

    public string BackupPath => SettingsPath + ".bak";

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var backup = await TryReadAsync(BackupPath, cancellationToken).ConfigureAwait(false);
                if (backup.Configuration is not null)
                {
                    await SaveCoreAsync(backup.Configuration, cancellationToken).ConfigureAwait(false);
                    return new ConfigurationLoadResult(
                        backup.Configuration,
                        ConfigurationLoadStatus.RecoveredFromBackup,
                        backup.Issues,
                        BackupPath,
                        backup.Error);
                }

                var defaults = ConfigurationDefaults.Create();
                await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
                return new ConfigurationLoadResult(
                    defaults,
                    ConfigurationLoadStatus.CreatedDefault,
                    Array.Empty<ConfigurationIssue>());
            }

            var primary = await TryReadAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
            if (primary.Configuration is not null)
            {
                var shouldRewrite = primary.WasMigrated || primary.Issues.Count > 0;
                if (shouldRewrite)
                {
                    await SaveCoreAsync(primary.Configuration, cancellationToken).ConfigureAwait(false);
                }

                return new ConfigurationLoadResult(
                    primary.Configuration,
                    primary.WasMigrated ? ConfigurationLoadStatus.Migrated : ConfigurationLoadStatus.Loaded,
                    primary.Issues);
            }

            if (primary.Error is UnsupportedConfigurationVersionException)
            {
                // Never rename or overwrite a valid file produced by a newer release.
                return new ConfigurationLoadResult(
                    ConfigurationDefaults.Create(),
                    ConfigurationLoadStatus.UnsupportedVersion,
                    Array.Empty<ConfigurationIssue>(),
                    Error: primary.Error);
            }

            var quarantinedPath = TryQuarantine(SettingsPath);
            var backupResult = await TryReadAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            if (backupResult.Configuration is not null)
            {
                if (quarantinedPath is not null)
                {
                    await SaveCoreAsync(backupResult.Configuration, cancellationToken).ConfigureAwait(false);
                }

                return new ConfigurationLoadResult(
                    backupResult.Configuration,
                    ConfigurationLoadStatus.RecoveredFromBackup,
                    backupResult.Issues,
                    quarantinedPath,
                    primary.Error);
            }

            var recoveredDefaults = ConfigurationDefaults.Create();
            if (quarantinedPath is not null)
            {
                await SaveCoreAsync(recoveredDefaults, cancellationToken).ConfigureAwait(false);
            }

            return new ConfigurationLoadResult(
                recoveredDefaults,
                ConfigurationLoadStatus.RecoveredCorrupt,
                Array.Empty<ConfigurationIssue>(),
                quarantinedPath,
                primary.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ActionsRingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    private async Task SaveCoreAsync(
        ActionsRingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var document = ConfigurationJson.Clone(configuration);
        var normalized = ConfigurationNormalizer.Normalize(document);
        var validation = ConfigurationValidator.Validate(normalized.Configuration);
        if (!validation.IsValid)
        {
            var details = string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}"));
            throw new InvalidDataException("Configuration could not be normalized:" + Environment.NewLine + details);
        }

        var directory = Path.GetDirectoryName(SettingsPath)
                        ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        normalized.Configuration,
                        ConfigurationJson.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > ConfigurationDocumentParser.MaximumFileSizeBytes)
                {
                    throw new InvalidDataException(
                        $"Configuration data exceeds {ConfigurationDocumentParser.MaximumFileSizeBytes} bytes.");
                }
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
            {
                ReplaceWithBackup(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<ReadAttempt> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ReadAttempt.Failed(new FileNotFoundException("Configuration file was not found.", path));
        }

        try
        {
            var information = new FileInfo(path);
            if (information.Length > ConfigurationDocumentParser.MaximumFileSizeBytes)
            {
                throw new InvalidDataException($"Configuration file exceeds {ConfigurationDocumentParser.MaximumFileSizeBytes} bytes.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var parsed = await ConfigurationDocumentParser.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
            return ReadAttempt.Succeeded(parsed.Configuration, parsed.WasMigrated, parsed.Issues);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or UnsupportedConfigurationVersionException)
        {
            return ReadAttempt.Failed(exception);
        }
    }

    private void ReplaceWithBackup(string temporaryPath)
    {
        try
        {
            File.Replace(temporaryPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            // Same-directory Move is atomic on supported local Windows file systems. Preserve a
            // backup explicitly when File.Replace is unavailable (for example, some network shares).
            File.Copy(SettingsPath, BackupPath, overwrite: true);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
    }

    private static string? TryQuarantine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var quarantinePath = Path.Combine(
            directory,
            $"{baseName}.{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.corrupt{extension}");

        try
        {
            File.Move(path, quarantinePath);
            return quarantinePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup. A future application run may remove stale .tmp files.
        }
    }

    private sealed record ReadAttempt(
        ActionsRingConfiguration? Configuration,
        bool WasMigrated,
        IReadOnlyList<ConfigurationIssue> Issues,
        Exception? Error)
    {
        public static ReadAttempt Succeeded(
            ActionsRingConfiguration configuration,
            bool wasMigrated,
            IReadOnlyList<ConfigurationIssue> issues) =>
            new(configuration, wasMigrated, issues, null);

        public static ReadAttempt Failed(Exception error) =>
            new(null, false, Array.Empty<ConfigurationIssue>(), error);
    }
}
