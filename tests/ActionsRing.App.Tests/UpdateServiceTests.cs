using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActionsRing.App.Services;
using ActionsRing.Core.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class UpdateServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 9, 3, 8, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReturnsReleaseDetailsAndNormalizedDigest()
    {
        var package = CreateValidPackage();
        var digest = Sha256(package);
        var handler = new StubHandler((request, _) =>
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            Assert.IsTrue(request.Headers.UserAgent.Any());
            return Task.FromResult(JsonResponse(CreateReleaseJson("v2.1.0", package.Length, digest)));
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.AreEqual("2.0.0", result.CurrentVersion.ToString());
        Assert.AreEqual("2.1.0", result.LatestRelease!.Version.ToString());
        Assert.AreEqual("Улучшения и исправления", result.LatestRelease.ReleaseNotes);
        Assert.AreEqual(digest, result.LatestRelease.Asset.Sha256Digest);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 2, 15, 0, 0, TimeSpan.Zero), result.LatestRelease.PublishedAtUtc);
        Assert.AreEqual(FixedNow, result.CheckedAtUtc);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReportsUpToDateAndHonorsSkippedVersion()
    {
        var package = CreateValidPackage();
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            var tag = calls == 1 ? "v2.0.0+release" : "v2.2.0";
            return Task.FromResult(JsonResponse(CreateReleaseJson(tag, package.Length, Sha256(package))));
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var current = await service.CheckForUpdatesAsync();
        var skipped = await service.CheckForUpdatesAsync("2.2.0");
        var included = await service.CheckForUpdatesAsync("2.2.0", includeSkipped: true);

        Assert.AreEqual(UpdateCheckStatus.UpToDate, current.Status);
        Assert.AreEqual(UpdateCheckStatus.Skipped, skipped.Status);
        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, included.Status);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_RejectsMissingAssetAndOversizedMetadata()
    {
        var responseNumber = 0;
        var handler = new StubHandler((_, _) =>
        {
            responseNumber++;
            if (responseNumber == 1)
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    tag_name = "v2.1.0",
                    name = "Release",
                    body = "Notes",
                    html_url = "https://github.com/perf769/actions-ring/releases/tag/v2.1.0",
                    draft = false,
                    assets = Array.Empty<object>(),
                })));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 2048)),
            });
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath, new UpdateServiceOptions
        {
            MaximumReleaseMetadataBytes = 1024,
        });

        var missing = await service.CheckForUpdatesAsync();
        var oversized = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, missing.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, missing.FailureReason);
        Assert.AreEqual(UpdateCheckStatus.Failed, oversized.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, oversized.FailureReason);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_RejectsReleaseWithoutSha256Digest()
    {
        var package = CreateValidPackage();
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v2.1.0",
            name = "Actions Ring 2.1.0",
            body = "Notes",
            html_url = "https://github.com/perf769/actions-ring/releases/tag/v2.1.0",
            draft = false,
            assets = new[]
            {
                new
                {
                    name = UpdateServiceOptions.PortableAssetName,
                    browser_download_url = "https://github.com/perf769/actions-ring/releases/download/v2.1.0/ActionsRing-portable.zip",
                    size = package.Length,
                    digest = (string?)null,
                },
            },
        });
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, result.FailureReason);
        StringAssert.Contains(result.UserMessage, "SHA-256");
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_RejectsGitHubUrlsOutsideOfficialReleasePaths()
    {
        var package = CreateValidPackage();
        var digest = Sha256(package);
        var validJson = CreateReleaseJson("v2.1.0", package.Length, digest);
        var responseNumber = 0;
        var handler = new StubHandler((_, _) =>
        {
            responseNumber++;
            var json = responseNumber == 1
                ? validJson.Replace(
                    "https://github.com/perf769/actions-ring/releases/tag/v2.1.0",
                    "https://github.com/another-owner/another-repository/releases/tag/v2.1.0",
                    StringComparison.Ordinal)
                : validJson.Replace(
                    "https://github.com/perf769/actions-ring/releases/download/v2.1.0/ActionsRing-portable.zip",
                    "https://github.com/another-owner/another-repository/releases/download/v2.1.0/ActionsRing-portable.zip",
                    StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(json));
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var foreignPage = await service.CheckForUpdatesAsync();
        var foreignAsset = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, foreignPage.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, foreignPage.FailureReason);
        Assert.AreEqual(UpdateCheckStatus.Failed, foreignAsset.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, foreignAsset.FailureReason);
    }

    [TestMethod]
    public async Task CheckForUpdatesAsync_ReturnsTimedOutStatus()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("Timed out request should have been cancelled.");
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath, new UpdateServiceOptions
        {
            CheckTimeout = TimeSpan.FromMilliseconds(20),
        });

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.TimedOut, result.FailureReason);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_VerifiesExtractsAndReusesPackage()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        var release = CreateReleaseInfo(packageBytes);
        var progress = new List<UpdateDownloadProgress>();

        var first = await service.DownloadAndStageAsync(
            release,
            new SynchronousProgress<UpdateDownloadProgress>(progress.Add));
        var second = await service.DownloadAndStageAsync(release);

        Assert.AreEqual(UpdateStageStatus.Staged, first.Status);
        Assert.IsNotNull(first.Package);
        Assert.IsTrue(File.Exists(first.Package.ArchivePath));
        Assert.IsTrue(File.Exists(first.Package.InstallerPath));
        Assert.IsTrue(File.Exists(first.Package.ExecutablePath));
        Assert.AreEqual(Sha256(packageBytes), first.Package.Sha256);
        Assert.AreEqual(UpdateStageStatus.AlreadyStaged, second.Status);
        Assert.AreEqual(first.Package.RootDirectory, second.Package!.RootDirectory);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(100, progress[^1].Percent);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_FollowsAllowedGitHubAssetRedirect()
    {
        var packageBytes = CreateValidPackage();
        var responseNumber = 0;
        var handler = new StubHandler((_, _) =>
        {
            responseNumber++;
            if (responseNumber == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(
                    "https://release-assets.githubusercontent.com/github-production-release-asset/package?token=test");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(BinaryResponse(packageBytes));
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Staged, result.Status);
        Assert.AreEqual(2, handler.CallCount);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsRedirectOutsideGitHubAssetHosts()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://downloads.example.com/ActionsRing-portable.zip");
            return Task.FromResult(redirect);
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidResponse, result.FailureReason);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_DetectsCorruptCachedArchiveAndReplacesStage()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        var release = CreateReleaseInfo(packageBytes);
        var first = await service.DownloadAndStageAsync(release);
        Assert.IsNotNull(first.Package);
        await File.AppendAllTextAsync(first.Package.ArchivePath, "tampered");

        var repaired = await service.DownloadAndStageAsync(release);

        Assert.AreEqual(UpdateStageStatus.Staged, repaired.Status);
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual(Sha256(packageBytes), Sha256(await File.ReadAllBytesAsync(repaired.Package!.ArchivePath)));
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_DetectsModifiedExtractedPayloadAndRestoresIt()
    {
        var packageBytes = CreateValidPackage();
        var expectedExecutable = GetActionsRingPeFixture();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        var release = CreateReleaseInfo(packageBytes);
        var first = await service.DownloadAndStageAsync(release);
        Assert.IsNotNull(first.Package);
        await File.WriteAllTextAsync(first.Package.ExecutablePath, "modified payload");

        var repaired = await service.DownloadAndStageAsync(release);

        Assert.AreEqual(UpdateStageStatus.Staged, repaired.Status);
        Assert.AreEqual(2, handler.CallCount);
        CollectionAssert.AreEqual(
            expectedExecutable,
            await File.ReadAllBytesAsync(repaired.Package!.ExecutablePath));
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RemovesOlderManagedStagesAfterSuccess()
    {
        var firstPackage = CreateValidPackage("first package");
        var secondPackage = CreateValidPackage("second package");
        var callCount = 0;
        var handler = new StubHandler((_, _) => Task.FromResult(
            BinaryResponse(++callCount == 1 ? firstPackage : secondPackage)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var first = await service.DownloadAndStageAsync(CreateReleaseInfo(firstPackage));
        var second = await service.DownloadAndStageAsync(CreateReleaseInfo(secondPackage));

        Assert.AreEqual(UpdateStageStatus.Staged, first.Status);
        Assert.AreEqual(UpdateStageStatus.Staged, second.Status);
        Assert.IsFalse(Directory.Exists(first.Package!.RootDirectory));
        Assert.IsTrue(Directory.Exists(second.Package!.RootDirectory));
    }

    [TestMethod]
    public async Task CleanupObsoleteStagesAsync_RemovesInstalledVersionsAndStaleTemporaryStages()
    {
        using var workspace = new TemporaryWorkspace();
        var oldStage = Path.Combine(workspace.DirectoryPath, $"v1.9.0-{new string('A', 64)}");
        var currentStage = Path.Combine(workspace.DirectoryPath, $"v2.0.0-{new string('B', 64)}");
        var futureStage = Path.Combine(workspace.DirectoryPath, $"v2.1.0-{new string('C', 64)}");
        var staleTemporary = Path.Combine(workspace.DirectoryPath, $".stage-{Guid.NewGuid():N}");
        var unrelated = Path.Combine(workspace.DirectoryPath, "personal-files");
        foreach (var directory in new[] { oldStage, currentStage, futureStage, staleTemporary, unrelated })
        {
            Directory.CreateDirectory(directory);
        }
        Directory.SetLastWriteTimeUtc(staleTemporary, FixedNow.UtcDateTime.AddDays(-2));
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = CreateService(handler, workspace.DirectoryPath);

        await service.CleanupObsoleteStagesAsync();

        Assert.IsFalse(Directory.Exists(oldStage));
        Assert.IsFalse(Directory.Exists(currentStage));
        Assert.IsFalse(Directory.Exists(staleTemporary));
        Assert.IsTrue(Directory.Exists(futureStage));
        Assert.IsTrue(Directory.Exists(unrelated));
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsExecutableWithDifferentProductVersion()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(
            CreateReleaseInfo(packageBytes, versionText: "99.0.0"));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidPackage, result.FailureReason);
        StringAssert.Contains(result.UserMessage, "неверную версию");
        Assert.IsFalse(Directory.EnumerateDirectories(workspace.DirectoryPath).Any());
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsForeignOrInvalidExecutable()
    {
        var packageBytes = CreatePackage(
            ("ActionsRing.exe", File.ReadAllBytes(typeof(UpdateServiceTests).Assembly.Location)),
            ("Install.ps1", Encoding.UTF8.GetBytes("Write-Output installed")),
            ("Uninstall.ps1", Encoding.UTF8.GetBytes("Write-Output uninstalled")));
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidPackage, result.FailureReason);
        Assert.IsFalse(Directory.EnumerateDirectories(workspace.DirectoryPath).Any());
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsPackageWithoutUninstaller()
    {
        var packageBytes = CreatePackage(
            ("ActionsRing.exe", GetActionsRingPeFixture()),
            ("Install.ps1", Encoding.UTF8.GetBytes("Write-Output installed")));
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidPackage, result.FailureReason);
        Assert.IsFalse(Directory.EnumerateDirectories(workspace.DirectoryPath).Any());
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsDigestMismatchAndCleansTemporaryFiles()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        var release = CreateReleaseInfo(packageBytes) with
        {
            Asset = CreateReleaseInfo(packageBytes).Asset with { Sha256Digest = new string('0', 64) },
        };

        var result = await service.DownloadAndStageAsync(release);

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.IntegrityCheckFailed, result.FailureReason);
        Assert.IsFalse(Directory.EnumerateDirectories(workspace.DirectoryPath).Any());
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_BlocksZipSlip()
    {
        var packageBytes = CreateTextPackage(("../outside.txt", "escape"), ("ActionsRing.exe", "binary"), ("Install.ps1", "script"));
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.InvalidPackage, result.FailureReason);
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(workspace.DirectoryPath)!, "outside.txt")));
        Assert.IsFalse(Directory.EnumerateDirectories(workspace.DirectoryPath).Any());
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_RejectsDeclaredPackageAboveLimitWithoutRequest()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler((_, _) => Task.FromResult(BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath, new UpdateServiceOptions
        {
            MaximumPackageBytes = packageBytes.Length - 1,
            MaximumExtractedBytes = 1024,
        });

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.PackageTooLarge, result.FailureReason);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_ReturnsTimedOutStatus()
    {
        var packageBytes = CreateValidPackage();
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("Timed out request should have been cancelled.");
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath, new UpdateServiceOptions
        {
            DownloadTimeout = TimeSpan.FromMilliseconds(20),
        });

        var result = await service.DownloadAndStageAsync(CreateReleaseInfo(packageBytes));

        Assert.AreEqual(UpdateStageStatus.Failed, result.Status);
        Assert.AreEqual(UpdateFailureReason.TimedOut, result.FailureReason);
    }

    [TestMethod]
    public async Task DownloadAndStageAsync_PropagatesCallerCancellation()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException("The request should have been cancelled.");
        });
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await service.DownloadAndStageAsync(
                CreateReleaseInfo(CreateValidPackage()),
                cancellationToken: cancellation.Token);
            Assert.Fail("A caller-cancelled update must not return a regular result.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task RunAutomaticCheckAsync_RespectsSwitchesAndCanPreloadPackage()
    {
        var packageBytes = CreateValidPackage();
        var digest = Sha256(packageBytes);
        var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(CreateReleaseJson("v" + FixtureVersion, packageBytes.Length, digest))
                : BinaryResponse(packageBytes)));
        using var workspace = new TemporaryWorkspace();
        using var service = CreateService(handler, workspace.DirectoryPath);
        var disabled = new UpdatePreferences { CheckAutomatically = false };

        var disabledResult = await service.RunAutomaticCheckAsync(disabled);
        Assert.AreEqual(0, handler.CallCount);
        var enabled = new UpdatePreferences
        {
            CheckAutomatically = true,
            DownloadAutomatically = true,
        };
        var enabledResult = await service.RunAutomaticCheckAsync(enabled);

        Assert.AreEqual(UpdateCheckStatus.AutomaticCheckDisabled, disabledResult.Check.Status);
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, enabledResult.Check.Status);
        Assert.AreEqual(UpdateStageStatus.Staged, enabledResult.Stage!.Status);
        Assert.IsTrue(enabledResult.RequiresUserConfirmation);
        Assert.AreEqual(FixedNow, enabled.LastCheckedAtUtc);
    }

    private static UpdateService CreateService(
        HttpMessageHandler handler,
        string stagingDirectory,
        UpdateServiceOptions? options = null) =>
        new(
            handler,
            SemanticVersion.Parse("2.0.0"),
            stagingDirectory,
            options,
            new FixedTimeProvider(FixedNow));

    private static UpdateReleaseInfo CreateReleaseInfo(
        byte[] package,
        string? versionText = null)
    {
        var version = SemanticVersion.Parse(versionText ?? FixtureVersion);
        return new UpdateReleaseInfo(
            version,
            $"v{version}",
            $"Actions Ring {version}",
            "Улучшения и исправления",
            new Uri($"https://github.com/perf769/actions-ring/releases/tag/v{version}"),
            FixedNow,
            new UpdateAssetInfo(
                UpdateServiceOptions.PortableAssetName,
                new Uri($"https://github.com/perf769/actions-ring/releases/download/v{version}/ActionsRing-portable.zip"),
                package.Length,
                Sha256(package)));
    }

    private static string CreateReleaseJson(string tag, int packageSize, string digest) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = $"Actions Ring {tag.TrimStart('v', 'V')}",
            body = "Улучшения и исправления",
            html_url = $"https://github.com/perf769/actions-ring/releases/tag/{tag}",
            published_at = "2026-09-02T15:00:00Z",
            draft = false,
            assets = new[]
            {
                new
                {
                    name = UpdateServiceOptions.PortableAssetName,
                    browser_download_url = $"https://github.com/perf769/actions-ring/releases/download/{tag}/ActionsRing-portable.zip",
                    size = packageSize,
                    digest = "sha256:" + digest.ToLowerInvariant(),
                },
            },
        });

    private static string FixtureVersion => typeof(UpdateService).Assembly.GetName().Version!.ToString(3);

    private static byte[] CreateValidPackage(string readme = "Actions Ring") => CreatePackage(
        ("ActionsRing.exe", GetActionsRingPeFixture()),
        ("Install.ps1", Encoding.UTF8.GetBytes("Write-Output installed")),
        ("Uninstall.ps1", Encoding.UTF8.GetBytes("Write-Output uninstalled")),
        ("README.md", Encoding.UTF8.GetBytes(readme)));

    private static byte[] CreateTextPackage(params (string Name, string Content)[] entries) =>
        CreatePackage(entries.Select(entry =>
            (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    private static byte[] CreatePackage(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var output = entry.Open();
                output.Write(content);
            }
        }
        return memory.ToArray();
    }

    private static byte[] GetActionsRingPeFixture() =>
        File.ReadAllBytes(typeof(ActionsRing.App.App).Assembly.Location);

    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responder(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "ActionsRing.UpdateService.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
