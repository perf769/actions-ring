using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActionsRing.Core.Configuration;

namespace ActionsRing.App.Services;

/// <summary>Checks the official GitHub release feed and safely prepares an update for installation.</summary>
public sealed class UpdateService : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly UpdateServiceOptions _options;
    private readonly string _stagingRoot;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _stageGate = new(1, 1);
    private int _disposed;

    public UpdateService(
        SemanticVersion currentVersion,
        string? stagingRootDirectory = null,
        UpdateServiceOptions? options = null)
        : this(
            CreateDefaultHttpClient(),
            currentVersion,
            stagingRootDirectory,
            options,
            TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    public UpdateService(
        HttpMessageHandler handler,
        SemanticVersion currentVersion,
        string? stagingRootDirectory = null,
        UpdateServiceOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            CreateInjectedHttpClient(handler),
            currentVersion,
            stagingRootDirectory,
            options,
            timeProvider ?? TimeProvider.System,
            ownsHttpClient: true)
    {
    }

    public UpdateService(
        HttpClient httpClient,
        SemanticVersion currentVersion,
        string? stagingRootDirectory = null,
        UpdateServiceOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            httpClient,
            currentVersion,
            stagingRootDirectory,
            options,
            timeProvider ?? TimeProvider.System,
            ownsHttpClient: false)
    {
    }

    private UpdateService(
        HttpClient httpClient,
        SemanticVersion currentVersion,
        string? stagingRootDirectory,
        UpdateServiceOptions? options,
        TimeProvider timeProvider,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        CurrentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _options = options ?? new UpdateServiceOptions();
        ValidateOptions(_options);
        _stagingRoot = Path.GetFullPath(stagingRootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ActionsRing",
            "updates"));
        _timeProvider = timeProvider;
        _ownsHttpClient = ownsHttpClient;
    }

    public SemanticVersion CurrentVersion { get; }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? skippedVersion = null,
        bool includeSkipped = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var checkedAt = _timeProvider.GetUtcNow();
        try
        {
            using var timeout = CreateTimeout(cancellationToken, _options.CheckTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.LatestReleaseApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"ActionsRing/{CurrentVersion}");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var noRelease = response.StatusCode == HttpStatusCode.NotFound;
                return CheckFailure(
                    checkedAt,
                    noRelease ? UpdateFailureReason.PackageUnavailable : UpdateFailureReason.Network,
                    noRelease
                        ? "Опубликованные обновления пока не найдены."
                        : "Не удалось проверить обновления. Проверьте подключение к интернету.");
            }

            var json = await ReadBoundedContentAsync(
                response.Content,
                _options.MaximumReleaseMetadataBytes,
                timeout.Token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<ReleaseDto>(json, JsonOptions);
            if (!TryCreateRelease(dto, out var release, out var validationMessage))
            {
                return CheckFailure(
                    checkedAt,
                    UpdateFailureReason.InvalidResponse,
                    validationMessage);
            }

            var precedence = release.Version.CompareTo(CurrentVersion);
            if (precedence <= 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    CurrentVersion,
                    release,
                    checkedAt,
                    "Установлена актуальная версия.");
            }

            if (!includeSkipped
                && SemanticVersion.TryParse(skippedVersion, out var skipped)
                && skipped.CompareTo(release.Version) == 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Skipped,
                    CurrentVersion,
                    release,
                    checkedAt,
                    $"Версия {release.Version} пропущена.");
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                CurrentVersion,
                release,
                checkedAt,
                $"Доступна новая версия {release.Version}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckFailure(
                checkedAt,
                UpdateFailureReason.TimedOut,
                "Сервер обновлений не ответил вовремя.");
        }
        catch (InvalidDataException exception)
        {
            AppLog.Error("Update response exceeded its safety limit", exception);
            return CheckFailure(
                checkedAt,
                UpdateFailureReason.InvalidResponse,
                "Сервер обновлений вернул слишком большой или некорректный ответ.",
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            AppLog.Error("Update check failed", exception);
            return CheckFailure(
                checkedAt,
                UpdateFailureReason.Network,
                "Не удалось проверить обновления. Проверьте подключение к интернету.",
                exception);
        }
        catch (Exception exception) when (exception is JsonException
                                          or NotSupportedException
                                          or FormatException
                                          or InvalidOperationException)
        {
            AppLog.Error("Update response was invalid", exception);
            return CheckFailure(
                checkedAt,
                UpdateFailureReason.InvalidResponse,
                "Сервер обновлений вернул некорректный ответ.",
                exception);
        }
    }

    public async Task<UpdateStageResult> DownloadAndStageAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(release);

        if (!IsExpectedAsset(release))
        {
            return StageFailure(
                UpdateFailureReason.PackageUnavailable,
                "Архив обновления отсутствует или имеет неверный адрес.");
        }
        if (release.Asset.Size <= 0 || release.Asset.Size > _options.MaximumPackageBytes)
        {
            return StageFailure(
                UpdateFailureReason.PackageTooLarge,
                "Размер архива обновления выходит за допустимые пределы.");
        }

        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryDirectory = null;
        try
        {
            EnsureSafeStagingRoot();
            if (release.Asset.Sha256Digest is { } knownDigest
                && await TryReadStagedPackageAsync(
                    release.Version,
                    knownDigest,
                    cancellationToken).ConfigureAwait(false) is { } knownPackage)
            {
                CleanupStagingRoot(knownPackage.RootDirectory);
                progress?.Report(new UpdateDownloadProgress(release.Asset.Size, release.Asset.Size));
                return new UpdateStageResult(
                    UpdateStageStatus.AlreadyStaged,
                    knownPackage,
                    "Обновление уже загружено и готово к установке.");
            }

            temporaryDirectory = Path.Combine(_stagingRoot, $".stage-{Guid.NewGuid():N}");
            EnsureDirectChild(temporaryDirectory, _stagingRoot);
            Directory.CreateDirectory(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "package.zip");

            var download = await DownloadArchiveAsync(
                release,
                archivePath,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (release.Asset.Size != download.BytesReceived)
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.IntegrityCheckFailed,
                    "Размер загруженного архива не совпадает с опубликованным.");
            }
            if (release.Asset.Sha256Digest is { } expectedDigest
                && !string.Equals(expectedDigest, download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.IntegrityCheckFailed,
                    "Контрольная сумма загруженного обновления не совпала.");
            }

            var payloadDirectory = Path.Combine(temporaryDirectory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            await ExtractArchiveSafelyAsync(
                archivePath,
                payloadDirectory,
                cancellationToken).ConfigureAwait(false);

            var installerPath = Path.Combine(payloadDirectory, "Install.ps1");
            var uninstallerPath = Path.Combine(payloadDirectory, "Uninstall.ps1");
            var executablePath = Path.Combine(payloadDirectory, "ActionsRing.exe");
            if (!IsNonEmptyFile(installerPath)
                || !IsNonEmptyFile(uninstallerPath)
                || !IsNonEmptyFile(executablePath))
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.InvalidPackage,
                    "В архиве обновления отсутствуют обязательные файлы.");
            }
            if (!HasExpectedApplicationIdentity(executablePath, release.Version))
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.InvalidPackage,
                    "Исполняемый файл обновления не принадлежит Actions Ring или имеет неверную версию.");
            }

            var finalDirectory = GetStageDirectory(release.Version, download.Sha256);
            var cachedPackage = await TryReadStagedPackageAsync(
                release.Version,
                download.Sha256,
                cancellationToken).ConfigureAwait(false);
            if (cachedPackage is not null)
            {
                SafeDeleteDirectory(temporaryDirectory);
                temporaryDirectory = null;
                CleanupStagingRoot(cachedPackage.RootDirectory);
                return new UpdateStageResult(
                    UpdateStageStatus.AlreadyStaged,
                    cachedPackage,
                    "Обновление уже загружено и готово к установке.");
            }

            if (Directory.Exists(finalDirectory))
            {
                SafeDeleteDirectory(finalDirectory);
            }

            var manifest = new StageManifest(release.Version.ToString(), download.Sha256);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "stage.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(temporaryDirectory, finalDirectory);
            temporaryDirectory = null;

            var package = CreateStagedPackage(release.Version, download.Sha256, finalDirectory);
            CleanupStagingRoot(package.RootDirectory);
            return new UpdateStageResult(
                UpdateStageStatus.Staged,
                package,
                "Обновление загружено и готово к установке.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StageFailure(
                UpdateFailureReason.TimedOut,
                "Загрузка обновления не завершилась вовремя.");
        }
        catch (UpdateServiceException exception)
        {
            AppLog.Error("Update package could not be staged", exception);
            return StageFailure(exception.Reason, exception.Message, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException)
        {
            AppLog.Error("Update download failed", exception);
            return StageFailure(
                UpdateFailureReason.Network,
                "Не удалось загрузить обновление. Проверьте подключение к интернету.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidDataException)
        {
            AppLog.Error("Update archive was invalid", exception);
            return StageFailure(
                UpdateFailureReason.InvalidPackage,
                "Загруженный архив обновления повреждён.",
                exception);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Update staging failed", exception);
            return StageFailure(
                UpdateFailureReason.Storage,
                "Не удалось сохранить обновление на диске.",
                exception);
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                try
                {
                    SafeDeleteDirectory(temporaryDirectory);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
                {
                    AppLog.Error("Temporary update files could not be removed", exception);
                }
            }
            _stageGate.Release();
        }
    }

    public async Task<AutomaticUpdateResult> RunAutomaticCheckAsync(
        UpdatePreferences preferences,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(preferences);
        if (!preferences.CheckAutomatically)
        {
            return new AutomaticUpdateResult(
                new UpdateCheckResult(
                    UpdateCheckStatus.AutomaticCheckDisabled,
                    CurrentVersion,
                    null,
                    _timeProvider.GetUtcNow(),
                    "Автоматическая проверка обновлений отключена."),
                null);
        }

        var check = await CheckForUpdatesAsync(
            preferences.SkippedVersion,
            includeSkipped: false,
            cancellationToken).ConfigureAwait(false);
        preferences.LastCheckedAtUtc = check.CheckedAtUtc;
        if (check.Status != UpdateCheckStatus.UpdateAvailable
            || check.LatestRelease is null
            || !preferences.DownloadAutomatically)
        {
            return new AutomaticUpdateResult(check, null);
        }

        var stage = await DownloadAndStageAsync(
            check.LatestRelease,
            progress,
            cancellationToken).ConfigureAwait(false);
        return new AutomaticUpdateResult(check, stage);
    }

    public async Task CleanupObsoleteStagesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_stagingRoot))
            {
                return;
            }

            EnsureSafeStagingRoot();
            await Task.Run(
                () => CleanupObsoleteStagingRoot(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stageGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stageGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<DownloadResult> DownloadArchiveAsync(
        UpdateReleaseInfo release,
        string destination,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken, _options.DownloadTimeout);
        using var response = await SendDownloadRequestAsync(
            release.Asset.DownloadUri,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Update asset request returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseLength = response.Content.Headers.ContentLength;
        if (responseLength is > 0 && responseLength > _options.MaximumPackageBytes)
        {
            throw new UpdateServiceException(
                UpdateFailureReason.PackageTooLarge,
                "Архив обновления превышает допустимый размер.");
        }

        var totalBytes = responseLength ?? release.Asset.Size;
        progress?.Report(new UpdateDownloadProgress(0, totalBytes));
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received = checked(received + read);
            if (received > _options.MaximumPackageBytes)
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.PackageTooLarge,
                    "Архив обновления превышает допустимый размер.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            progress?.Report(new UpdateDownloadProgress(received, totalBytes));
        }

        await target.FlushAsync(timeout.Token).ConfigureAwait(false);
        if (responseLength is { } declaredLength && declaredLength != received)
        {
            throw new UpdateServiceException(
                UpdateFailureReason.IntegrityCheckFailed,
                "Загрузка архива обновления завершилась не полностью.");
        }
        if (received == 0)
        {
            throw new UpdateServiceException(
                UpdateFailureReason.InvalidPackage,
                "Загруженный архив обновления пуст.");
        }

        progress?.Report(new UpdateDownloadProgress(received, totalBytes));
        return new DownloadResult(received, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        const int maximumRedirects = 5;
        var requestUri = initialUri;
        for (var redirectCount = 0; redirectCount <= maximumRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.UserAgent.ParseAdd($"ActionsRing/{CurrentVersion}");
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                var finalUri = response.RequestMessage?.RequestUri ?? requestUri;
                if (!IsAllowedDownloadEndpoint(finalUri))
                {
                    response.Dispose();
                    throw InvalidDownloadRedirect();
                }
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirectCount == maximumRedirects)
            {
                throw InvalidDownloadRedirect();
            }

            requestUri = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
            if (!IsAllowedDownloadEndpoint(requestUri))
            {
                throw InvalidDownloadRedirect();
            }
        }

        throw InvalidDownloadRedirect();
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
        || (int)statusCode == 308;

    private static bool IsAllowedDownloadEndpoint(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static UpdateServiceException InvalidDownloadRedirect() => new(
        UpdateFailureReason.InvalidResponse,
        "Сервер обновлений перенаправил загрузку на недопустимый адрес.");

    private async Task ExtractArchiveSafelyAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var root = EnsureTrailingSeparator(Path.GetFullPath(destinationRoot));
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var file = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > _options.MaximumArchiveEntries)
        {
            throw new UpdateServiceException(
                UpdateFailureReason.InvalidPackage,
                "Архив обновления содержит недопустимое количество файлов.");
        }

        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymbolicLink(entry))
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.InvalidPackage,
                    "Архив обновления содержит недопустимую ссылку.");
            }

            var relativePath = NormalizeArchivePath(entry.FullName);
            if (relativePath is null)
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.InvalidPackage,
                    "Архив обновления содержит небезопасный путь.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !seenPaths.Add(targetPath))
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.InvalidPackage,
                    "Архив обновления содержит конфликтующие пути.");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.Length < 0
                || entry.Length > _options.MaximumExtractedBytes - extractedBytes)
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.PackageTooLarge,
                    "Распакованное обновление превышает допустимый размер.");
            }

            var targetDirectory = Path.GetDirectoryName(targetPath)
                                  ?? throw new InvalidDataException("Archive entry has no parent directory.");
            Directory.CreateDirectory(targetDirectory);
            await using var source = entry.Open();
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long entryBytes = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                entryBytes = checked(entryBytes + read);
                extractedBytes = checked(extractedBytes + read);
                if (entryBytes > entry.Length || extractedBytes > _options.MaximumExtractedBytes)
                {
                    throw new UpdateServiceException(
                        UpdateFailureReason.PackageTooLarge,
                        "Распакованное обновление превышает допустимый размер.");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (entryBytes != entry.Length)
            {
                throw new UpdateServiceException(
                    UpdateFailureReason.IntegrityCheckFailed,
                    "Файл в архиве обновления распакован не полностью.");
            }
        }
    }

    private bool TryCreateRelease(
        ReleaseDto? dto,
        out UpdateReleaseInfo release,
        out string validationMessage)
    {
        release = null!;
        validationMessage = "Сервер обновлений вернул неполные данные.";
        if (dto is null
            || dto.Draft
            || !SemanticVersion.TryParseTag(dto.TagName, out var version)
            || !TryCreateHttpsUri(dto.HtmlUrl, expectedHost: "github.com", out var releasePage)
            || !HasExactGitHubPath(
                releasePage,
                $"/perf769/actions-ring/releases/tag/{dto.TagName!.Trim()}"))
        {
            return false;
        }

        var matchingAssets = dto.Assets?
            .Where(asset => string.Equals(
                asset.Name,
                UpdateServiceOptions.PortableAssetName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (matchingAssets.Length != 1)
        {
            validationMessage = "В выпуске не найден архив Actions Ring для Windows.";
            return false;
        }

        var dtoAsset = matchingAssets[0];
        if (dtoAsset.Size <= 0
            || dtoAsset.Size > _options.MaximumPackageBytes
            || !TryCreateHttpsUri(dtoAsset.BrowserDownloadUrl, expectedHost: "github.com", out var downloadUri)
            || !HasExactGitHubPath(
                downloadUri,
                $"/perf769/actions-ring/releases/download/{dto.TagName!.Trim()}/{UpdateServiceOptions.PortableAssetName}"))
        {
            validationMessage = "Архив обновления имеет неверный адрес или размер.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dtoAsset.Digest)
            || !TryNormalizeSha256(dtoAsset.Digest, out var digest))
        {
            validationMessage = "У архива обновления отсутствует корректная контрольная сумма SHA-256.";
            return false;
        }

        var notes = dto.Body?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        release = new UpdateReleaseInfo(
            version,
            dto.TagName!.Trim(),
            string.IsNullOrWhiteSpace(dto.Name) ? $"Actions Ring {version}" : dto.Name.Trim(),
            string.IsNullOrWhiteSpace(notes) ? "Описание изменений не указано." : notes,
            releasePage,
            dto.PublishedAtUtc,
            new UpdateAssetInfo(
                UpdateServiceOptions.PortableAssetName,
                downloadUri,
                dtoAsset.Size,
                digest));
        return true;
    }

    private bool TryReadStagedPackage(
        SemanticVersion version,
        string sha256,
        out StagedUpdatePackage package,
        CancellationToken cancellationToken)
    {
        package = null!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = GetStageDirectory(version, sha256);
            var manifestPath = Path.Combine(directory, "stage.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var manifestBytes = File.ReadAllBytes(manifestPath);
            if (manifestBytes.Length is 0 or > 16 * 1024)
            {
                return false;
            }
            var manifest = JsonSerializer.Deserialize<StageManifest>(manifestBytes, JsonOptions);
            if (manifest is null
                || !string.Equals(manifest.Version, version.ToString(), StringComparison.Ordinal)
                || !string.Equals(manifest.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = CreateStagedPackage(version, sha256, directory);
            if (!IsNonEmptyFile(candidate.ArchivePath)
                || !IsNonEmptyFile(candidate.InstallerPath)
                || !IsNonEmptyFile(Path.Combine(candidate.PayloadDirectory, "Uninstall.ps1"))
                || !IsNonEmptyFile(candidate.ExecutablePath)
                || !FileHashMatches(candidate.ArchivePath, sha256, cancellationToken)
                || !PayloadMatchesArchive(
                    candidate.ArchivePath,
                    candidate.PayloadDirectory,
                    cancellationToken)
                || !HasExpectedApplicationIdentity(candidate.ExecutablePath, version))
            {
                return false;
            }

            package = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Staged update could not be read", exception);
            return false;
        }
    }

    private Task<StagedUpdatePackage?> TryReadStagedPackageAsync(
        SemanticVersion version,
        string sha256,
        CancellationToken cancellationToken) => Task.Run(
        () => TryReadStagedPackage(version, sha256, out var package, cancellationToken)
            ? package
            : null,
        cancellationToken);

    private StagedUpdatePackage CreateStagedPackage(
        SemanticVersion version,
        string sha256,
        string directory)
    {
        var payload = Path.Combine(directory, "payload");
        return new StagedUpdatePackage(
            version,
            directory,
            payload,
            Path.Combine(directory, "package.zip"),
            Path.Combine(payload, "Install.ps1"),
            Path.Combine(payload, "ActionsRing.exe"),
            sha256);
    }

    private string GetStageDirectory(SemanticVersion version, string sha256)
    {
        var path = Path.Combine(_stagingRoot, $"v{version}-{sha256.ToUpperInvariant()}");
        EnsureDirectChild(path, _stagingRoot);
        return path;
    }

    private void EnsureSafeStagingRoot()
    {
        Directory.CreateDirectory(_stagingRoot);
        var root = new DirectoryInfo(_stagingRoot);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UpdateServiceException(
                UpdateFailureReason.Storage,
                "Папка обновлений указывает на недопустимое расположение.");
        }
    }

    private void SafeDeleteDirectory(string path)
    {
        EnsureDirectChild(path, _stagingRoot);
        if (!Directory.Exists(path))
        {
            return;
        }

        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to recursively delete a reparse point.");
        }
        DeleteDirectoryContents(root);
        root.Delete();
    }

    private static void DeleteDirectoryContents(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo linkedDirectory)
                {
                    linkedDirectory.Delete();
                }
                else
                {
                    entry.Delete();
                }
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteDirectoryContents(childDirectory);
                childDirectory.Delete();
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
    }

    private void CleanupStagingRoot(string retainedDirectory)
    {
        try
        {
            var retainedPath = Path.GetFullPath(retainedDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var directory in new DirectoryInfo(_stagingRoot).EnumerateDirectories())
            {
                if (string.Equals(
                        directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        retainedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var temporary = IsManagedTemporaryDirectoryName(directory.Name)
                                && directory.LastWriteTimeUtc < _timeProvider.GetUtcNow().UtcDateTime.AddDays(-1);
                if (!temporary && !TryGetManagedStageVersion(directory.Name, out _))
                {
                    continue;
                }

                try
                {
                    SafeDeleteDirectory(directory.FullName);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
                {
                    AppLog.Error("Old staged update could not be removed", exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Update staging cleanup could not be completed", exception);
        }
    }

    private void CleanupObsoleteStagingRoot(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var directory in new DirectoryInfo(_stagingRoot).EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporary = IsManagedTemporaryDirectoryName(directory.Name)
                                && directory.LastWriteTimeUtc < _timeProvider.GetUtcNow().UtcDateTime.AddDays(-1);
                var obsoleteStage = TryGetManagedStageVersion(directory.Name, out var version)
                                    && version.CompareTo(CurrentVersion) <= 0;
                if (!temporary && !obsoleteStage)
                {
                    continue;
                }

                try
                {
                    SafeDeleteDirectory(directory.FullName);
                }
                catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
                {
                    AppLog.Error("Obsolete staged update could not be removed", exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Obsolete update cleanup could not be completed", exception);
        }
    }

    private static bool IsManagedTemporaryDirectoryName(string name) =>
        name.StartsWith(".stage-", StringComparison.Ordinal)
        && name.Length == ".stage-".Length + 32
        && name[".stage-".Length..].All(Uri.IsHexDigit);

    private static bool TryGetManagedStageVersion(string name, out SemanticVersion version)
    {
        version = null!;
        if (!name.StartsWith('v'))
        {
            return false;
        }

        var digestSeparator = name.LastIndexOf('-');
        if (digestSeparator <= 1 || digestSeparator + 65 != name.Length)
        {
            return false;
        }

        return SemanticVersion.TryParse(name[1..digestSeparator], out version)
               && name[(digestSeparator + 1)..].All(Uri.IsHexDigit);
    }

    private static string? NormalizeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOf('\0') >= 0
            || value.IndexOf(':') >= 0
            || Path.IsPathFullyQualified(value))
        {
            return null;
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".."
                                       || segment.EndsWith(' ')
                                       || segment.EndsWith('.')))
        {
            return null;
        }
        return Path.Combine(segments);
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixMode == UnixSymbolicLink
               || (windowsAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool TryNormalizeSha256(string value, out string? digest)
    {
        digest = null;
        const string prefix = "sha256:";
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hash = trimmed[prefix.Length..];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            return false;
        }

        digest = hash.ToUpperInvariant();
        return true;
    }

    private static bool TryCreateHttpsUri(
        string? value,
        string expectedHost,
        out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !string.Equals(candidate.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort
            || candidate.UserInfo.Length != 0
            || candidate.Query.Length != 0
            || candidate.Fragment.Length != 0)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool HasExactGitHubPath(Uri uri, string expectedPath)
    {
        try
        {
            return string.Equals(
                Uri.UnescapeDataString(uri.AbsolutePath),
                expectedPath,
                StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsExpectedAsset(UpdateReleaseInfo release) =>
        !string.IsNullOrWhiteSpace(release.TagName)
        && SemanticVersion.TryParseTag(release.TagName, out var tagVersion)
        && tagVersion.CompareTo(release.Version) == 0
        && string.Equals(release.Asset.Name, UpdateServiceOptions.PortableAssetName, StringComparison.OrdinalIgnoreCase)
        && release.Asset.DownloadUri.IsAbsoluteUri
        && release.Asset.DownloadUri.Scheme == Uri.UriSchemeHttps
        && string.Equals(release.Asset.DownloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && HasExactGitHubPath(
            release.Asset.DownloadUri,
            $"/perf769/actions-ring/releases/download/{release.TagName.Trim()}/{UpdateServiceOptions.PortableAssetName}")
        && release.Asset.Sha256Digest is { Length: 64 } digest
        && digest.All(Uri.IsHexDigit);

    private bool PayloadMatchesArchive(
        string archivePath,
        string payloadDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var payloadRoot = EnsureTrailingSeparator(Path.GetFullPath(payloadDirectory));
            if (!Directory.Exists(payloadDirectory)
                || (File.GetAttributes(payloadDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var archiveFile = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > _options.MaximumArchiveEntries)
            {
                return false;
            }

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSymbolicLink(entry) || NormalizeArchivePath(entry.FullName) is not { } relativePath)
                {
                    return false;
                }

                var targetPath = Path.GetFullPath(Path.Combine(payloadDirectory, relativePath));
                if (!targetPath.StartsWith(payloadRoot, StringComparison.OrdinalIgnoreCase)
                    || !expectedPaths.Add(targetPath))
                {
                    return false;
                }

                if (entry.Name.Length == 0)
                {
                    if (!Directory.Exists(targetPath)
                        || (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                    continue;
                }

                if (!File.Exists(targetPath)
                    || (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(targetPath).Length != entry.Length
                    || !StreamsEqual(entry.Open(), File.OpenRead(targetPath), cancellationToken))
                {
                    return false;
                }
            }

            return PayloadContainsOnlyExpectedFiles(
                new DirectoryInfo(payloadDirectory),
                expectedPaths,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Staged update payload integrity check failed", exception);
            return false;
        }
    }

    private static bool StreamsEqual(
        Stream left,
        Stream right,
        CancellationToken cancellationToken)
    {
        using (left)
        using (right)
        {
            var leftBuffer = new byte[64 * 1024];
            var rightBuffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftRead = ReadFullBuffer(left, leftBuffer);
                var rightRead = ReadFullBuffer(right, rightBuffer);
                if (leftRead != rightRead
                    || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                {
                    return false;
                }
                if (leftRead == 0)
                {
                    return true;
                }
            }
        }
    }

    private static bool PayloadContainsOnlyExpectedFiles(
        DirectoryInfo directory,
        HashSet<string> expectedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (entry is DirectoryInfo childDirectory)
            {
                if (!PayloadContainsOnlyExpectedFiles(childDirectory, expectedPaths, cancellationToken))
                {
                    return false;
                }
            }
            else if (!expectedPaths.Contains(Path.GetFullPath(entry.FullName)))
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadFullBuffer(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }
        return offset;
    }

    private static bool FileHashMatches(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static bool HasExpectedApplicationIdentity(
        string executablePath,
        SemanticVersion expectedVersion)
    {
        try
        {
            var information = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.Equals(information.ProductName?.Trim(), "Actions Ring", StringComparison.OrdinalIgnoreCase)
                || !IsActionsRingDescription(information.FileDescription)
                || !SemanticVersion.TryParse(information.ProductVersion, out var productVersion))
            {
                return false;
            }

            return productVersion.CompareTo(expectedVersion) == 0;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception
                                          or System.Security.SecurityException)
        {
            AppLog.Error("Update executable identity could not be read", exception);
            return false;
        }
    }

    private static bool IsActionsRingDescription(string? value)
    {
        var description = value?.Trim();
        return string.Equals(description, "Actions Ring", StringComparison.OrdinalIgnoreCase)
               || string.Equals(description, "ActionsRing", StringComparison.OrdinalIgnoreCase);
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
        {
            throw new InvalidDataException("HTTP content exceeds its configured size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }
            if (memory.Length + read > maximumBytes)
            {
                throw new InvalidDataException("HTTP content exceeds its configured size limit.");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureDirectChild(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = EnsureTrailingSeparator(Path.GetFullPath(parent));
        if (!fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase)
            || fullPath.AsSpan(fullParent.Length).IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0)
        {
            throw new InvalidOperationException("Update path is not a direct child of the staging directory.");
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpClient CreateInjectedHttpClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void ValidateOptions(UpdateServiceOptions options)
    {
        if (!options.LatestReleaseApiUri.IsAbsoluteUri
            || options.LatestReleaseApiUri.Scheme != Uri.UriSchemeHttps
            || options.CheckTimeout <= TimeSpan.Zero
            || options.DownloadTimeout <= TimeSpan.Zero
            || options.MaximumReleaseMetadataBytes <= 0
            || options.MaximumPackageBytes <= 0
            || options.MaximumExtractedBytes <= 0
            || options.MaximumArchiveEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Update service limits must be positive and the API must use HTTPS.");
        }
    }

    private UpdateCheckResult CheckFailure(
        DateTimeOffset checkedAt,
        UpdateFailureReason reason,
        string message,
        Exception? exception = null) =>
        new(
            UpdateCheckStatus.Failed,
            CurrentVersion,
            null,
            checkedAt,
            message,
            reason,
            exception);

    private static UpdateStageResult StageFailure(
        UpdateFailureReason reason,
        string message,
        Exception? exception = null) =>
        new(UpdateStageStatus.Failed, null, message, reason, exception);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record DownloadResult(long BytesReceived, string Sha256);

    private sealed record StageManifest(string Version, string Sha256);

    private sealed class UpdateServiceException : Exception
    {
        public UpdateServiceException(UpdateFailureReason reason, string message, Exception? inner = null)
            : base(message, inner)
        {
            Reason = reason;
        }

        public UpdateFailureReason Reason { get; }
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAtUtc { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("assets")]
        public List<ReleaseAssetDto>? Assets { get; init; }
    }

    private sealed class ReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
