using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ActionsRing.App.Services;

/// <summary>Starts the verified per-user installer after the current process has exited.</summary>
public sealed class UpdateInstallationLauncher
{
    private const string BootstrapFileName = "ApplyActionsRingUpdate.ps1";
    private const string ManifestFileName = "install-payload.json";
    private const string InstalledExecutableRelativePath = @"Programs\ActionsRing\ActionsRing.exe";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public string Launch(StagedUpdatePackage package, int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Обновление доступно только в Windows.");
        }
        if (IsProcessElevated())
        {
            throw new InvalidOperationException(
                "Перезапустите Actions Ring без прав администратора, чтобы установить обновление для текущего пользователя.");
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallbackExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к запущенной программе.");
        var parentProcessStartedAtFileTimeUtc = Process.GetCurrentProcess()
            .StartTime
            .ToUniversalTime()
            .ToFileTimeUtc();
        var (startInfo, installedExecutable) = PrepareLaunch(
            package,
            processId,
            systemDirectory,
            localApplicationData,
            fallbackExecutable,
            parentProcessStartedAtFileTimeUtc);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить установку обновления.");
        AppLog.Info($"Update installer scheduled for version {package.Version}.");
        return installedExecutable;
    }

    internal (ProcessStartInfo StartInfo, string InstalledExecutable) PrepareLaunch(
        StagedUpdatePackage package,
        int processId,
        string systemDirectory,
        string localApplicationData,
        string fallbackExecutable,
        long parentProcessStartedAtFileTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        if (parentProcessStartedAtFileTimeUtc <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessStartedAtFileTimeUtc));
        }
        if (string.IsNullOrWhiteSpace(systemDirectory) || string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Не удалось определить системные папки Windows.");
        }

        var rootDirectory = RequireDirectory(package.RootDirectory, nameof(package.RootDirectory));
        var payloadDirectory = RequireDirectoryInside(
            package.PayloadDirectory,
            rootDirectory,
            nameof(package.PayloadDirectory));
        var archivePath = RequireFileInside(
            package.ArchivePath,
            rootDirectory,
            "package.zip",
            nameof(package.ArchivePath));
        var installerPath = RequireFileInside(
            package.InstallerPath,
            payloadDirectory,
            "Install.ps1",
            nameof(package.InstallerPath));
        _ = RequireFileInside(
            Path.Combine(payloadDirectory, "Uninstall.ps1"),
            payloadDirectory,
            "Uninstall.ps1",
            nameof(package.PayloadDirectory));
        _ = RequireFileInside(
            package.ExecutablePath,
            payloadDirectory,
            "ActionsRing.exe",
            nameof(package.ExecutablePath));
        var normalizedFallbackExecutable = RequireFile(fallbackExecutable, nameof(fallbackExecutable));

        var expectedArchiveSha256 = NormalizeSha256(package.Sha256);
        var payloadManifest = CreatePayloadManifest(
            archivePath,
            payloadDirectory,
            expectedArchiveSha256);
        var manifestPath = Path.Combine(rootDirectory, ManifestFileName);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(payloadManifest, ManifestJsonOptions);
        WriteBytesAtomically(manifestPath, manifestBytes);
        var expectedManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));

        var powershellPath = Path.GetFullPath(Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"));
        if (!File.Exists(powershellPath)
            || File.GetAttributes(powershellPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FileNotFoundException("Не найден системный компонент установки Windows.", powershellPath);
        }

        var installedExecutable = Path.GetFullPath(
            Path.Combine(localApplicationData, InstalledExecutableRelativePath));
        var bootstrapPath = Path.Combine(rootDirectory, BootstrapFileName);
        WriteBytesAtomically(
            bootstrapPath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(BootstrapScript));

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            WorkingDirectory = rootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // Windows PowerShell must not inherit incompatible PowerShell 7 modules.
        startInfo.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershellPath)!, "Modules");
        AddArgument(startInfo, "-NoLogo");
        AddArgument(startInfo, "-NoProfile");
        AddArgument(startInfo, "-NonInteractive");
        AddArgument(startInfo, "-ExecutionPolicy");
        AddArgument(startInfo, "Bypass");
        AddArgument(startInfo, "-File");
        AddArgument(startInfo, bootstrapPath);
        AddParameter(startInfo, "ParentProcessId", processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddParameter(
            startInfo,
            "ParentProcessStartedAtFileTimeUtc",
            parentProcessStartedAtFileTimeUtc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddParameter(startInfo, "ArchivePath", archivePath);
        AddParameter(startInfo, "ExpectedArchiveSha256", expectedArchiveSha256);
        AddParameter(startInfo, "ManifestPath", manifestPath);
        AddParameter(startInfo, "ExpectedManifestSha256", expectedManifestSha256);
        AddParameter(startInfo, "InstallerPath", installerPath);
        AddParameter(startInfo, "PayloadDirectory", payloadDirectory);
        AddParameter(startInfo, "InstalledExecutable", installedExecutable);
        AddParameter(startInfo, "FallbackExecutable", normalizedFallbackExecutable);
        return (startInfo, installedExecutable);
    }

    private static InstallPayloadManifest CreatePayloadManifest(
        string archivePath,
        string payloadDirectory,
        string expectedArchiveSha256)
    {
        var actualArchiveSha256 = ComputeSha256(archivePath);
        if (!string.Equals(actualArchiveSha256, expectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма подготовленного обновления не совпала.");
        }

        var entries = new List<InstallPayloadEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0)
                {
                    continue;
                }
                if (IsSymbolicLink(entry))
                {
                    throw new InvalidDataException("Архив обновления содержит недопустимую ссылку.");
                }

                var relativePath = NormalizeArchivePath(entry.FullName);
                if (relativePath is null || !seenPaths.Add(relativePath))
                {
                    throw new InvalidDataException("Архив обновления содержит небезопасный или повторяющийся путь.");
                }

                using var content = entry.Open();
                var digest = Convert.ToHexString(SHA256.HashData(content));
                entries.Add(new InstallPayloadEntry(relativePath, entry.Length, digest));
            }
        }

        if (!seenPaths.Contains("ActionsRing.exe")
            || !seenPaths.Contains("Install.ps1")
            || !seenPaths.Contains("Uninstall.ps1"))
        {
            throw new InvalidDataException("В пакете обновления отсутствуют обязательные файлы.");
        }

        var actualFiles = EnumeratePayloadFiles(payloadDirectory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (actualFiles.Count != entries.Count)
        {
            throw new InvalidDataException("Содержимое подготовленного обновления было изменено.");
        }

        foreach (var entry in entries)
        {
            if (!actualFiles.TryGetValue(entry.Path, out var file)
                || file.Length != entry.Length
                || !string.Equals(ComputeSha256(file.FullPath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Содержимое подготовленного обновления не совпадает с архивом.");
            }
        }

        entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return new InstallPayloadManifest(expectedArchiveSha256, entries);
    }

    private static IEnumerable<PayloadFile> EnumeratePayloadFiles(string payloadDirectory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(payloadDirectory));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Папка обновления содержит недопустимую ссылку.");
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Пакет обновления содержит недопустимую ссылку.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                var relativePath = Path.GetRelativePath(payloadDirectory, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                yield return new PayloadFile(relativePath, entry.FullName, ((FileInfo)entry).Length);
            }
        }
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

        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => string.IsNullOrEmpty(segment)
                                       || segment is "." or ".."
                                       || segment.EndsWith(' ')
                                       || segment.EndsWith('.')))
        {
            return null;
        }
        return string.Join('/', segments);
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixMode == UnixSymbolicLink
               || windowsAttributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string RequireDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь к обновлению не задан.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Папка обновления недоступна или небезопасна.");
        }
        return directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string RequireDirectoryInside(string path, string parent, string parameterName)
    {
        var directory = RequireDirectory(path, parameterName);
        EnsureInside(directory, parent);
        return directory;
    }

    private static string RequireFileInside(
        string path,
        string parent,
        string expectedFileName,
        string parameterName)
    {
        var fullPath = RequireFile(path, parameterName);
        EnsureInside(fullPath, parent);
        if (!string.Equals(Path.GetFileName(fullPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Пакет обновления не прошёл проверку файлов.");
        }
        return fullPath;
    }

    private static string RequireFile(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь к файлу не задан.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)
            || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint)
            || new FileInfo(fullPath).Length == 0)
        {
            throw new InvalidDataException("Необходимый файл недоступен или небезопасен.");
        }
        return fullPath;
    }

    private static void EnsureInside(string path, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalizedParent + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Файл обновления находится вне проверенного пакета.");
        }
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value?.Trim();
        if (normalized is null || normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Контрольная сумма обновления имеет неверный формат.");
        }
        return normalized.ToUpperInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteBytesAtomically(string destinationPath, byte[] content)
    {
        if (File.Exists(destinationPath)
            && File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Служебный файл обновления небезопасен.");
        }

        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AddParameter(ProcessStartInfo startInfo, string name, string value)
    {
        AddArgument(startInfo, "-" + name);
        AddArgument(startInfo, value);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value) =>
        startInfo.ArgumentList.Add(value);

    internal static bool IsProcessElevated()
    {
        // Membership checks duplicate a primary token to create an impersonation token.
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record InstallPayloadManifest(
        string ArchiveSha256,
        IReadOnlyList<InstallPayloadEntry> Files);

    private sealed record InstallPayloadEntry(string Path, long Length, string Sha256);

    private sealed record PayloadFile(string RelativePath, string FullPath, long Length);

    private const string BootstrapScript = """
        #Requires -Version 5.1
        [CmdletBinding()]
        param(
            [Parameter(Mandatory = $true)]
            [ValidateRange(1, 2147483647)]
            [int]$ParentProcessId,

            [Parameter(Mandatory = $true)]
            [ValidateRange(1, 9223372036854775807)]
            [long]$ParentProcessStartedAtFileTimeUtc,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$ArchivePath,

            [Parameter(Mandatory = $true)]
            [ValidatePattern('^[A-Fa-f0-9]{64}$')]
            [string]$ExpectedArchiveSha256,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$ManifestPath,

            [Parameter(Mandatory = $true)]
            [ValidatePattern('^[A-Fa-f0-9]{64}$')]
            [string]$ExpectedManifestSha256,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$InstallerPath,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$PayloadDirectory,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$InstalledExecutable,

            [Parameter(Mandatory = $true)]
            [ValidateNotNullOrEmpty()]
            [string]$FallbackExecutable
        )

        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        function Write-UpdateLog {
            param([string]$Message)
            try {
                $logPath = Join-Path $PSScriptRoot 'install.log'
                $directory = Get-Item -LiteralPath $PSScriptRoot -Force
                if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
                if (Test-Path -LiteralPath $logPath) {
                    $file = Get-Item -LiteralPath $logPath -Force
                    if ($file.PSIsContainer -or $file.Length -ge 1MB -or
                        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
                }
                $line = '[{0:o}] {1}{2}' -f [DateTimeOffset]::Now, $Message, [Environment]::NewLine
                [IO.File]::AppendAllText($logPath, $line, (New-Object Text.UTF8Encoding($false)))
            }
            catch { }
        }

        function Test-ParentProcessStillRunning {
            $candidate = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
            if ($null -eq $candidate) {
                return $false
            }
            try {
                return $candidate.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $ParentProcessStartedAtFileTimeUtc
            }
            catch {
                return $true
            }
        }

        function Start-PreviousVersion {
            if (Test-ParentProcessStillRunning) {
                return
            }
            if (Test-Path -LiteralPath $FallbackExecutable -PathType Leaf) {
                Start-Process -FilePath $FallbackExecutable -ArgumentList '--update-failed'
            }
        }

        function Get-SafePayloadFiles {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Directory
            )

            foreach ($item in @(Get-ChildItem -LiteralPath $Directory -Force)) {
                if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'The staged update contains a reparse point.'
                }
                if ($item.PSIsContainer) {
                    Get-SafePayloadFiles -Directory $item.FullName
                }
                else {
                    Write-Output -NoEnumerate $item
                }
            }
        }

        try {
            Write-UpdateLog 'Preparing update installation.'
            $deadline = [DateTime]::UtcNow.AddMinutes(2)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not (Test-ParentProcessStillRunning)) {
                    break
                }
                Start-Sleep -Milliseconds 250
            }
            if (Test-ParentProcessStillRunning) {
                throw 'Actions Ring did not exit before the update timeout.'
            }

            $payloadRootItem = Get-Item -LiteralPath $PayloadDirectory -Force
            if (-not $payloadRootItem.PSIsContainer -or
                (($payloadRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw 'The staged payload directory is unavailable or unsafe.'
            }

            $archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
            if (-not [string]::Equals($archiveHash, $ExpectedArchiveSha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The staged archive hash changed before installation.'
            }
            $manifestHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
            if (-not [string]::Equals($manifestHash, $ExpectedManifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The staged payload manifest changed before installation.'
            }

            $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not [string]::Equals(
                    [string]$manifest.archiveSha256,
                    $ExpectedArchiveSha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The staged payload manifest does not match the release archive.'
            }

            $payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\', '/')
            $payloadPrefix = $payloadRoot + [IO.Path]::DirectorySeparatorChar
            $manifestFiles = @($manifest.files)
            $expectedPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $manifestFiles) {
                $relativePath = [string]$entry.path
                if ([string]::IsNullOrWhiteSpace($relativePath) -or
                    $relativePath.IndexOf([char]0) -ge 0 -or
                    $relativePath.IndexOf(':') -ge 0 -or
                    [IO.Path]::IsPathRooted($relativePath)) {
                    throw 'The staged payload manifest contains an unsafe path.'
                }
                $targetPath = [IO.Path]::GetFullPath((Join-Path $payloadRoot $relativePath))
                if (-not $targetPath.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    -not $expectedPaths.Add($targetPath)) {
                    throw 'The staged payload manifest contains a conflicting path.'
                }

                $file = Get-Item -LiteralPath $targetPath -Force
                if ($file.PSIsContainer -or
                    (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
                    $file.Length -ne [long]$entry.length) {
                    throw 'A staged payload file changed before installation.'
                }
                $fileHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
                if (-not [string]::Equals($fileHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'A staged payload file hash changed before installation.'
                }
            }

            $actualFiles = @(Get-SafePayloadFiles -Directory $payloadRoot)
            if ($actualFiles.Count -ne $expectedPaths.Count) {
                throw 'The staged payload file set changed before installation.'
            }
            foreach ($file in $actualFiles) {
                if (-not $expectedPaths.Contains([IO.Path]::GetFullPath($file.FullName))) {
                    throw 'The staged payload contains an unexpected file.'
                }
            }

            $expectedInstallerPath = [IO.Path]::GetFullPath((Join-Path $payloadRoot 'Install.ps1'))
            if (-not [string]::Equals(
                    [IO.Path]::GetFullPath($InstallerPath),
                    $expectedInstallerPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The installer path does not match the verified payload.'
            }

            $installerHost = Join-Path $PSHOME 'powershell.exe'
            Write-UpdateLog 'Package verified. Starting installer.'
            & $installerHost -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $InstallerPath -SourceDirectory $PayloadDirectory -Quiet 2>&1 |
                ForEach-Object { Write-UpdateLog ([string]$_) }
            if ($LASTEXITCODE -ne 0) {
                throw "The update installer exited with code $LASTEXITCODE."
            }
            if (-not (Test-Path -LiteralPath $InstalledExecutable -PathType Leaf)) {
                throw 'The updated executable was not installed.'
            }
            Start-Process -FilePath $InstalledExecutable
            Write-UpdateLog 'Installation completed. Updated application started.'
            exit 0
        }
        catch {
            Write-UpdateLog ("Installation failed: " + $_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace)
            try {
                Start-PreviousVersion
            }
            catch {
                Write-UpdateLog ("Could not restart previous version: " + $_.Exception.ToString())
            }
            exit 1
        }
        """;
}
