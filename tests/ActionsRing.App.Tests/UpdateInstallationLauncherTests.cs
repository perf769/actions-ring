using ActionsRing.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ActionsRing.App.Tests;

[TestClass]
public sealed class UpdateInstallationLauncherTests
{
    private string _temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "ActionsRingLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public void PrepareLaunch_UsesArgumentListAndVerifiedPackagePaths()
    {
        var package = CreatePackage();
        var systemDirectory = Path.Combine(_temporaryRoot, "Windows", "System32");
        var powershellPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(powershellPath)!);
        File.WriteAllText(powershellPath, "placeholder");
        var localApplicationData = Path.Combine(_temporaryRoot, "Local App Data");
        Directory.CreateDirectory(localApplicationData);
        var fallbackExecutable = CreateFallbackExecutable();

        var launcher = new UpdateInstallationLauncher();
        var (startInfo, installedExecutable) = launcher.PrepareLaunch(
            package,
            1234,
            systemDirectory,
            localApplicationData,
            fallbackExecutable,
            134_172_894_000_000_000L);

        Assert.AreEqual(Path.GetFullPath(powershellPath), startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), package.InstallerPath);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), package.PayloadDirectory);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), package.ArchivePath);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), package.Sha256);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), fallbackExecutable);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "1234");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "134172894000000000");
        StringAssert.EndsWith(installedExecutable, @"Programs\ActionsRing\ActionsRing.exe");
        var bootstrapPath = Path.Combine(package.RootDirectory, "ApplyActionsRingUpdate.ps1");
        var manifestPath = Path.Combine(package.RootDirectory, "install-payload.json");
        Assert.IsTrue(File.Exists(bootstrapPath));
        Assert.IsTrue(File.Exists(manifestPath));
        var bootstrap = File.ReadAllText(bootstrapPath);
        StringAssert.Contains(bootstrap, "$LASTEXITCODE");
        StringAssert.Contains(bootstrap, "--update-failed");
        StringAssert.Contains(bootstrap, "Get-FileHash");
    }

    [TestMethod]
    public void PrepareLaunch_RejectsExecutableOutsidePayload()
    {
        var package = CreatePackage();
        var outsideExecutable = Path.Combine(_temporaryRoot, "ActionsRing.exe");
        File.WriteAllText(outsideExecutable, "outside");
        package = package with { ExecutablePath = outsideExecutable };

        Assert.ThrowsException<InvalidDataException>(() =>
            new UpdateInstallationLauncher().PrepareLaunch(
                package,
                12,
                Path.Combine(_temporaryRoot, "system"),
                Path.Combine(_temporaryRoot, "local"),
                CreateFallbackExecutable(),
                134_172_894_000_000_000L));
    }

    [TestMethod]
    public void PrepareLaunch_RejectsArchiveWhoseHashChanged()
    {
        var package = CreatePackage();
        File.AppendAllText(package.ArchivePath, "changed");

        Assert.ThrowsException<InvalidDataException>(() => PrepareWithSystemFiles(package));
    }

    [TestMethod]
    public void PrepareLaunch_RejectsPayloadChangedAfterExtraction()
    {
        var package = CreatePackage();
        File.AppendAllText(package.ExecutablePath, "changed");

        Assert.ThrowsException<InvalidDataException>(() => PrepareWithSystemFiles(package));
    }

    [TestMethod]
    public void PrepareLaunch_GeneratesValidWindowsPowerShellScript()
    {
        var package = CreatePackage();
        PrepareWithSystemFiles(package);
        var bootstrapPath = Path.Combine(package.RootDirectory, "ApplyActionsRingUpdate.ps1");
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["ACTIONSRING_BOOTSTRAP_TO_PARSE"] = bootstrapPath;
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$tokens=$null; $errors=$null; "
            + "[System.Management.Automation.Language.Parser]::ParseFile($env:ACTIONSRING_BOOTSTRAP_TO_PARSE,[ref]$tokens,[ref]$errors) | Out-Null; "
            + "if ($errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        Assert.IsTrue(process.WaitForExit(10_000), "PowerShell parser did not finish in time.");
        var standardError = process.StandardError.ReadToEnd();
        Assert.AreEqual(0, process.ExitCode, standardError);
    }

    [TestMethod]
    public void GeneratedBootstrap_RestartsPreviousVersionWhenInstallerFails()
    {
        var package = CreatePackage("exit 17");
        var markerPath = Path.Combine(_temporaryRoot, "fallback-started.txt");
        var fallbackPath = Path.Combine(_temporaryRoot, "fallback.cmd");
        File.WriteAllText(
            fallbackPath,
            $"@echo off{Environment.NewLine}>\"{markerPath}\" echo %~1{Environment.NewLine}");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var localApplicationData = Path.Combine(_temporaryRoot, "Local App Data");
        Directory.CreateDirectory(localApplicationData);
        var (startInfo, _) = new UpdateInstallationLauncher().PrepareLaunch(
            package,
            int.MaxValue,
            systemDirectory,
            localApplicationData,
            fallbackPath,
            134_172_894_000_000_000L);
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        Assert.IsTrue(process.WaitForExit(15_000), "Update bootstrap did not finish in time.");
        var standardError = process.StandardError.ReadToEnd();
        Assert.AreEqual(1, process.ExitCode, standardError);
        Assert.IsTrue(
            SpinWait.SpinUntil(() => File.Exists(markerPath), TimeSpan.FromSeconds(5)),
            "Previous-version fallback was not started.");
        Assert.AreEqual("--update-failed", File.ReadAllText(markerPath).Trim());
    }

    private StagedUpdatePackage CreatePackage(string installerContents = "# installer")
    {
        var root = Path.Combine(_temporaryRoot, "stage");
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(payload);
        var installer = Path.Combine(payload, "Install.ps1");
        var uninstaller = Path.Combine(payload, "Uninstall.ps1");
        var executable = Path.Combine(payload, "ActionsRing.exe");
        var archive = Path.Combine(root, "package.zip");
        File.WriteAllText(installer, installerContents);
        File.WriteAllText(uninstaller, "# uninstaller");
        File.WriteAllText(executable, "executable");
        ZipFile.CreateFromDirectory(payload, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        return new StagedUpdatePackage(
            SemanticVersion.Parse("2.1.0"),
            root,
            payload,
            archive,
            installer,
            executable,
            sha256);
    }

    private void PrepareWithSystemFiles(StagedUpdatePackage package)
    {
        var systemDirectory = Path.Combine(_temporaryRoot, "Windows", "System32");
        var powershellPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(powershellPath)!);
        File.WriteAllText(powershellPath, "placeholder");
        var localApplicationData = Path.Combine(_temporaryRoot, "Local App Data");
        Directory.CreateDirectory(localApplicationData);
        _ = new UpdateInstallationLauncher().PrepareLaunch(
            package,
            1234,
            systemDirectory,
            localApplicationData,
            CreateFallbackExecutable(),
            134_172_894_000_000_000L);
    }

    private string CreateFallbackExecutable()
    {
        var fallbackExecutable = Path.Combine(_temporaryRoot, "previous", "ActionsRing.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackExecutable)!);
        File.WriteAllText(fallbackExecutable, "previous version");
        return fallbackExecutable;
    }
}
