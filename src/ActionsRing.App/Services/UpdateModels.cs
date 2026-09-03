using ActionsRing.Core.Configuration;

namespace ActionsRing.App.Services;

public enum UpdateCheckStatus
{
    AutomaticCheckDisabled,
    UpToDate,
    UpdateAvailable,
    Skipped,
    Failed,
}

public enum UpdateStageStatus
{
    Staged,
    AlreadyStaged,
    Failed,
}

public enum UpdateFailureReason
{
    None,
    Network,
    TimedOut,
    InvalidResponse,
    PackageUnavailable,
    PackageTooLarge,
    IntegrityCheckFailed,
    InvalidPackage,
    Storage,
}

public sealed record UpdateAssetInfo(
    string Name,
    Uri DownloadUri,
    long Size,
    string? Sha256Digest);

public sealed record UpdateReleaseInfo(
    SemanticVersion Version,
    string TagName,
    string DisplayName,
    string ReleaseNotes,
    Uri ReleasePageUri,
    DateTimeOffset? PublishedAtUtc,
    UpdateAssetInfo Asset);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    SemanticVersion CurrentVersion,
    UpdateReleaseInfo? LatestRelease,
    DateTimeOffset CheckedAtUtc,
    string UserMessage,
    UpdateFailureReason FailureReason = UpdateFailureReason.None,
    Exception? Error = null);

public sealed record StagedUpdatePackage(
    SemanticVersion Version,
    string RootDirectory,
    string PayloadDirectory,
    string ArchivePath,
    string InstallerPath,
    string ExecutablePath,
    string Sha256);

public sealed record UpdateStageResult(
    UpdateStageStatus Status,
    StagedUpdatePackage? Package,
    string UserMessage,
    UpdateFailureReason FailureReason = UpdateFailureReason.None,
    Exception? Error = null);

public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : null;

    public int? Percent => Fraction is { } fraction
        ? (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero)
        : null;
}

public sealed record AutomaticUpdateResult(
    UpdateCheckResult Check,
    UpdateStageResult? Stage)
{
    public bool RequiresUserConfirmation =>
        Check.Status == UpdateCheckStatus.UpdateAvailable
        && (Stage is null || Stage.Status is UpdateStageStatus.Staged or UpdateStageStatus.AlreadyStaged);
}

public sealed class UpdateServiceOptions
{
    public const string PortableAssetName = "ActionsRing-portable.zip";

    public Uri LatestReleaseApiUri { get; init; } =
        new("https://api.github.com/repos/perf769/actions-ring/releases/latest");

    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public long MaximumReleaseMetadataBytes { get; init; } = 1024 * 1024;

    public long MaximumPackageBytes { get; init; } = 256L * 1024 * 1024;

    public long MaximumExtractedBytes { get; init; } = 768L * 1024 * 1024;

    public int MaximumArchiveEntries { get; init; } = 512;
}

public interface IUpdateService : IDisposable
{
    SemanticVersion CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? skippedVersion = null,
        bool includeSkipped = false,
        CancellationToken cancellationToken = default);

    Task<UpdateStageResult> DownloadAndStageAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AutomaticUpdateResult> RunAutomaticCheckAsync(
        UpdatePreferences preferences,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task CleanupObsoleteStagesAsync(CancellationToken cancellationToken = default);
}
