#Requires -Version 5.1

<#
.SYNOPSIS
Installs Actions Ring for the current Windows user without elevation.

.DESCRIPTION
Copies a published Actions Ring payload to
%LOCALAPPDATA%\Programs\ActionsRing and creates a current-user Start Menu
shortcut. The application remains solely responsible for its autostart setting;
this installer never creates or enables an autostart entry.

.PARAMETER SourceDirectory
Directory containing ActionsRing.exe. When omitted, the script first checks its
own directory (the portable archive layout), then ..\artifacts\publish (the
repository layout).

.PARAMETER Quiet
Suppresses informational output. Errors are always reported.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$SourceDirectory,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Quiet) {
    $ProgressPreference = 'SilentlyContinue'
}

$script:ApplicationFileName = 'ActionsRing.exe'
$script:ShortcutFileName = 'Actions Ring.lnk'
$script:InstallMarkerName = '.actionsring-install'
$script:InstallMarkerContent = 'ActionsRing|installer-schema=1'
$script:UninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ActionsRing'

function ConvertTo-NormalizedFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path was empty.'
    }

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    $fullPath = [System.IO.Path]::GetFullPath($expandedPath)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $root.Length) {
        $fullPath = $fullPath.TrimEnd([char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar))
    }

    return $fullPath
}

function Test-PathsEqual {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    try {
        return [string]::Equals(
            (ConvertTo-NormalizedFullPath -Path $Left),
            (ConvertTo-NormalizedFullPath -Path $Right),
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Assert-ExactPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-PathsEqual -Left $Actual -Right $Expected)) {
        throw "$Label resolved to an unexpected path. Expected '$Expected'; got '$Actual'."
    }
}

function Test-IsChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Parent
    )

    $normalizedPath = ConvertTo-NormalizedFullPath -Path $Path
    $normalizedParent = ConvertTo-NormalizedFullPath -Path $Parent
    $parentPrefix = $normalizedParent
    if (-not $parentPrefix.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $parentPrefix += [System.IO.Path]::DirectorySeparatorChar
    }

    return $normalizedPath.StartsWith(
        $parentPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-IsChildPath -Path $Path -Parent $Parent)) {
        throw "$Label must be a child of '$Parent'; got '$Path'."
    }
}

function Test-ReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileSystemInfo]$Item
    )

    return (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparsePointAncestors {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $current = New-Object System.IO.DirectoryInfo(
        (ConvertTo-NormalizedFullPath -Path $Path))
    while ($null -ne $current) {
        if (Test-ReparsePoint -Item $current) {
            throw "$Label traverses reparse-point directory '$($current.FullName)'."
        }
        $current = $current.Parent
    }
}

function Initialize-NativePathResolver {
    if ($null -ne ('ActionsRing.Installer.NativePath' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ActionsRing.Installer
{
    public static class NativePath
    {
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle handle,
            StringBuilder path,
            uint pathLength,
            uint flags);

        public static string GetFinalPath(string path)
        {
            using (SafeFileHandle handle = CreateFileW(
                path,
                0,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not open path for canonicalization: " + path);
                }

                uint capacity = 512;
                while (true)
                {
                    var builder = new StringBuilder((int)capacity);
                    uint length = GetFinalPathNameByHandleW(handle, builder, capacity, 0);
                    if (length == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "Could not canonicalize path: " + path);
                    }
                    if (length < capacity)
                    {
                        string result = builder.ToString();
                        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                        {
                            return @"\\" + result.Substring(8);
                        }
                        if (result.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                        {
                            return result.Substring(4);
                        }
                        return result;
                    }
                    capacity = length + 1;
                }
            }
        }
    }
}
'@
}

function Get-FinalDirectoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return ConvertTo-NormalizedFullPath -Path (
        [ActionsRing.Installer.NativePath]::GetFinalPath($Path))
}

function Assert-OwnedInstallDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $markerPath = Join-Path $Path $script:InstallMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Refusing to replace '$Path' because it has no Actions Ring installer ownership marker."
    }

    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if (Test-ReparsePoint -Item $markerItem) {
        throw "Refusing to trust reparse-point ownership marker '$markerPath'."
    }
    if ($markerItem.Length -gt 256) {
        throw "Refusing to trust oversized ownership marker '$markerPath'."
    }

    $content = [System.IO.File]::ReadAllText($markerPath).Trim()
    if (-not [string]::Equals(
            $content,
            $script:InstallMarkerContent,
            [System.StringComparison]::Ordinal)) {
        throw "Refusing to replace '$Path' because its ownership marker is invalid."
    }
}

function Remove-SafeDirectoryTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RequiredParent
    )

    $fullPath = ConvertTo-NormalizedFullPath -Path $Path
    Assert-ChildPath -Path $fullPath -Parent $RequiredParent -Label 'Directory selected for removal'
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $rootItem = Get-Item -LiteralPath $fullPath -Force
    if (-not $rootItem.PSIsContainer) {
        throw "Refusing to recursively remove '$fullPath' because it is not a directory."
    }
    if (Test-ReparsePoint -Item $rootItem) {
        throw "Refusing to recursively remove reparse-point directory '$fullPath'."
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $fullPath -Force)) {
        Assert-ChildPath -Path $child.FullName -Parent $fullPath -Label 'Removal entry'
        if ($child.PSIsContainer -and -not (Test-ReparsePoint -Item $child)) {
            Remove-SafeDirectoryTree -Path $child.FullName -RequiredParent $fullPath
            continue
        }

        if ($child.PSIsContainer) {
            [System.IO.Directory]::Delete($child.FullName)
        }
        else {
            [System.IO.File]::SetAttributes($child.FullName, [System.IO.FileAttributes]::Normal)
            [System.IO.File]::Delete($child.FullName)
        }
    }

    [System.IO.File]::SetAttributes($fullPath, [System.IO.FileAttributes]::Directory)
    [System.IO.Directory]::Delete($fullPath)
}

function Copy-SafeDirectoryContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot
    )

    $sourcePath = ConvertTo-NormalizedFullPath -Path $Source
    $destinationPath = ConvertTo-NormalizedFullPath -Path $Destination
    $normalizedDestinationRoot = ConvertTo-NormalizedFullPath -Path $DestinationRoot
    if (-not (Test-PathsEqual -Left $destinationPath -Right $normalizedDestinationRoot)) {
        Assert-ChildPath -Path $destinationPath -Parent $normalizedDestinationRoot -Label 'Payload destination'
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $sourcePath -Force)) {
        if (Test-ReparsePoint -Item $child) {
            throw "Refusing to install reparse point '$($child.FullName)'."
        }

        $destinationChild = Join-Path $destinationPath $child.Name
        Assert-ChildPath -Path $destinationChild -Parent $DestinationRoot -Label 'Payload entry'
        if ($child.PSIsContainer) {
            [void](New-Item -ItemType Directory -Path $destinationChild -Force)
            Copy-SafeDirectoryContent `
                -Source $child.FullName `
                -Destination $destinationChild `
                -DestinationRoot $DestinationRoot
        }
        else {
            Copy-Item -LiteralPath $child.FullName -Destination $destinationChild -Force
        }
    }
}

function Write-Status {
    param([string]$Message)

    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Get-ShortcutTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath
    )

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        return [string]$shortcut.TargetPath
    }
    finally {
        if ($null -ne $shortcut) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ($null -ne $shell) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

function Set-ApplicationShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $ExecutablePath
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = 'Actions Ring'
        $shortcut.IconLocation = "$ExecutablePath,0"
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shortcut) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ($null -ne $shell) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

function Get-UninstallRegistrationSnapshot {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:UninstallKeyPath, $false)
    if ($null -eq $key) {
        return $null
    }

    try {
        $values = New-Object System.Collections.Generic.List[object]
        foreach ($name in $key.GetValueNames()) {
            [void]$values.Add([pscustomobject]@{
                    Name = $name
                    Kind = $key.GetValueKind($name)
                    Value = $key.GetValue(
                        $name,
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                })
        }
        return [pscustomobject]@{ Values = $values.ToArray() }
    }
    finally {
        $key.Dispose()
    }
}

function Assert-OwnedUninstallRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedInstallDirectory
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:UninstallKeyPath, $false)
    if ($null -eq $key) {
        return
    }

    try {
        $schema = $key.GetValue('InstallerSchema', $null)
        $location = [string]$key.GetValue(
            'InstallLocation',
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ([int]$schema -ne 1 -or
            [string]::IsNullOrWhiteSpace($location) -or
            -not (Test-PathsEqual -Left $location -Right $ExpectedInstallDirectory)) {
            throw "Refusing to replace an uninstall registration not owned by this Actions Ring installer: 'HKCU\$($script:UninstallKeyPath)'."
        }
    }
    finally {
        $key.Dispose()
    }
}

function Restore-UninstallRegistration {
    param($Snapshot)

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(
        $script:UninstallKeyPath,
        $false)
    if ($null -eq $Snapshot) {
        return
    }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey(
        $script:UninstallKeyPath,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
    if ($null -eq $key) {
        throw 'Could not restore the previous Actions Ring uninstall registration.'
    }
    try {
        foreach ($entry in $Snapshot.Values) {
            $key.SetValue($entry.Name, $entry.Value, $entry.Kind)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Set-UninstallRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    $uninstallScript = Join-Path $InstallDirectory 'Uninstall.ps1'
    if (-not (Test-Path -LiteralPath $uninstallScript -PathType Leaf)) {
        throw "Installed uninstaller is missing: '$uninstallScript'."
    }

    $windowsPowerShell = [Environment]::ExpandEnvironmentVariables(
        '%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe')
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
        $windowsPowerShellCommand = Get-Command powershell.exe -CommandType Application -ErrorAction Stop |
            Select-Object -First 1
        $windowsPowerShell = $windowsPowerShellCommand.Source
    }
    $windowsPowerShell = ConvertTo-NormalizedFullPath -Path $windowsPowerShell

    $uninstallCommand = '"{0}" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "{1}"' -f `
        $windowsPowerShell, $uninstallScript
    $quietUninstallCommand = $uninstallCommand + ' -Quiet'

    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = '1.0.0'
    }

    [long]$installedBytes = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $InstallDirectory -File -Force -Recurse)) {
        $installedBytes += $file.Length
    }
    [long]$estimatedKilobytes = [Math]::Ceiling($installedBytes / 1KB)
    if ($estimatedKilobytes -gt [int]::MaxValue) {
        $estimatedKilobytes = [int]::MaxValue
    }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey(
        $script:UninstallKeyPath,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
    if ($null -eq $key) {
        throw 'Could not create the current-user uninstall registration.'
    }
    try {
        $key.SetValue('DisplayName', 'Actions Ring', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('DisplayVersion', $version, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('Publisher', 'Actions Ring', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('InstallLocation', $InstallDirectory, [Microsoft.Win32.RegistryValueKind]::String)
        $displayIcon = '"{0}",0' -f $ExecutablePath
        $key.SetValue('DisplayIcon', $displayIcon, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('UninstallString', $uninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('QuietUninstallString', $quietUninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('EstimatedSize', [int]$estimatedKilobytes, [Microsoft.Win32.RegistryValueKind]::DWord)
        # Written last: ownership is asserted only after every public value succeeds.
        $key.SetValue('InstallerSchema', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    finally {
        $key.Dispose()
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Actions Ring can only be installed on Windows.'
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Actions Ring requires 64-bit Windows (win-x64).'
    }
    if (-not [Environment]::Is64BitProcess) {
        throw 'Run the installer with 64-bit Windows PowerShell. Actions Ring targets win-x64.'
    }
    Initialize-NativePathResolver

    $localApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw 'Windows did not provide a Local Application Data directory.'
    }
    $localApplicationData = ConvertTo-NormalizedFullPath -Path $localApplicationData

    $programsDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $localApplicationData 'Programs')
    $installDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $programsDirectory 'ActionsRing')
    $installedExecutable = ConvertTo-NormalizedFullPath -Path (
        Join-Path $installDirectory $script:ApplicationFileName)

    Assert-ExactPath `
        -Actual $installDirectory `
        -Expected (Join-Path $localApplicationData 'Programs\ActionsRing') `
        -Label 'Install directory'
    Assert-ChildPath -Path $installDirectory -Parent $programsDirectory -Label 'Install directory'

    $startMenuPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    if ([string]::IsNullOrWhiteSpace($startMenuPrograms)) {
        throw 'Windows did not provide a current-user Start Menu Programs directory.'
    }
    $startMenuPrograms = ConvertTo-NormalizedFullPath -Path $startMenuPrograms
    $shortcutPath = ConvertTo-NormalizedFullPath -Path (
        Join-Path $startMenuPrograms $script:ShortcutFileName)
    Assert-ChildPath -Path $shortcutPath -Parent $startMenuPrograms -Label 'Start Menu shortcut'
    if (Test-Path -LiteralPath $shortcutPath -PathType Container) {
        throw "Start Menu shortcut path is occupied by a directory: '$shortcutPath'."
    }

    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        $portableSource = ConvertTo-NormalizedFullPath -Path $PSScriptRoot
        $repositorySource = ConvertTo-NormalizedFullPath -Path (
            Join-Path $PSScriptRoot '..\artifacts\publish')
        if (Test-Path -LiteralPath (Join-Path $portableSource $script:ApplicationFileName) -PathType Leaf) {
            $SourceDirectory = $portableSource
        }
        elseif (Test-Path -LiteralPath (Join-Path $repositorySource $script:ApplicationFileName) -PathType Leaf) {
            $SourceDirectory = $repositorySource
        }
        else {
            throw "Could not find $($script:ApplicationFileName). Extract the portable ZIP or pass -SourceDirectory."
        }
    }

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Source directory does not exist: '$SourceDirectory'."
    }
    $sourceDirectoryPath = ConvertTo-NormalizedFullPath -Path (
        (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath)
    $sourceDirectoryItem = Get-Item -LiteralPath $sourceDirectoryPath -Force
    if (Test-ReparsePoint -Item $sourceDirectoryItem) {
        throw "Refusing to install from reparse-point source directory '$sourceDirectoryPath'."
    }
    $sourceDirectoryPath = ConvertTo-NormalizedFullPath -Path $sourceDirectoryItem.FullName
    Assert-NoReparsePointAncestors -Path $sourceDirectoryPath -Label 'Source directory'

    $sourceExecutable = Join-Path $sourceDirectoryPath $script:ApplicationFileName
    if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
        throw "The source directory does not contain $($script:ApplicationFileName): '$sourceDirectoryPath'."
    }
    $sourceExecutableItem = Get-Item -LiteralPath $sourceExecutable -Force
    if (Test-ReparsePoint -Item $sourceExecutableItem) {
        throw "Refusing to install a reparse-point executable: '$sourceExecutable'."
    }
    if ($sourceExecutableItem.Length -le 0) {
        throw "Source executable is empty: '$sourceExecutable'."
    }

    $finalSourceDirectory = Get-FinalDirectoryPath -Path $sourceDirectoryPath
    $finalLocalApplicationData = Get-FinalDirectoryPath -Path $localApplicationData
    $finalProgramsDirectory = Join-Path $finalLocalApplicationData 'Programs'
    if (Test-Path -LiteralPath $programsDirectory -PathType Container) {
        $finalProgramsDirectory = Get-FinalDirectoryPath -Path $programsDirectory
    }
    $finalInstallDirectory = Join-Path $finalProgramsDirectory 'ActionsRing'
    if (Test-Path -LiteralPath $installDirectory -PathType Container) {
        $finalInstallDirectory = Get-FinalDirectoryPath -Path $installDirectory
    }

    if ((Test-PathsEqual -Left $finalSourceDirectory -Right $finalInstallDirectory) -or
        (Test-IsChildPath -Path $finalInstallDirectory -Parent $finalSourceDirectory)) {
        throw 'The source directory cannot be the install directory or one of its parents.'
    }

    if (Test-Path -LiteralPath $installDirectory -PathType Leaf) {
        throw "Install path is occupied by a file: '$installDirectory'."
    }
    $existingInstall = Test-Path -LiteralPath $installDirectory -PathType Container
    if ($existingInstall) {
        $existingInstallItem = Get-Item -LiteralPath $installDirectory -Force
        if (Test-ReparsePoint -Item $existingInstallItem) {
            throw "Refusing to replace reparse-point install directory '$installDirectory'."
        }
        Assert-OwnedInstallDirectory -Path $installDirectory
    }

    Assert-OwnedUninstallRegistration -ExpectedInstallDirectory $installDirectory
    $uninstallRegistrationSnapshot = Get-UninstallRegistrationSnapshot

    $runningProcesses = @(Get-Process -Name 'ActionsRing' -ErrorAction SilentlyContinue)
    foreach ($process in $runningProcesses) {
        $processPath = $null
        try {
            $processPath = $process.Path
        }
        catch {
            if ($existingInstall) {
                throw 'An ActionsRing process is running but its path cannot be verified. Exit it, then run the installer again.'
            }
        }

        if ($existingInstall -and [string]::IsNullOrWhiteSpace($processPath)) {
            throw 'An ActionsRing process is running but its path cannot be verified. Exit it, then run the installer again.'
        }
        if (-not [string]::IsNullOrWhiteSpace($processPath) -and
            (Test-PathsEqual -Left $processPath -Right $installedExecutable)) {
            throw 'Actions Ring is running. Exit it from the notification area, then run the installer again.'
        }
    }

    $shortcutExisted = Test-Path -LiteralPath $shortcutPath -PathType Leaf
    if ($shortcutExisted) {
        $existingTarget = Get-ShortcutTarget -ShortcutPath $shortcutPath
        if ([string]::IsNullOrWhiteSpace($existingTarget) -or
            -not (Test-PathsEqual -Left $existingTarget -Right $installedExecutable)) {
            throw "Refusing to overwrite a Start Menu shortcut not owned by Actions Ring: '$shortcutPath'."
        }
    }

    if (-not $PSCmdlet.ShouldProcess(
            $installDirectory,
            'Install Actions Ring for the current user and create its Start Menu shortcut')) {
        return
    }

    [void](New-Item -ItemType Directory -Path $programsDirectory -Force)
    [void](New-Item -ItemType Directory -Path $startMenuPrograms -Force)

    $stagingDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $programsDirectory ('.ActionsRing-install-' + [guid]::NewGuid().ToString('N')))
    $backupDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $programsDirectory ('.ActionsRing-backup-' + [guid]::NewGuid().ToString('N')))
    $failedDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $programsDirectory ('.ActionsRing-failed-' + [guid]::NewGuid().ToString('N')))
    Assert-ChildPath -Path $stagingDirectory -Parent $programsDirectory -Label 'Install staging directory'
    Assert-ChildPath -Path $backupDirectory -Parent $programsDirectory -Label 'Install backup directory'
    Assert-ChildPath -Path $failedDirectory -Parent $programsDirectory -Label 'Failed install quarantine'

    $oldInstallMoved = $false
    $newInstallMoved = $false
    $uninstallRegistrationTouched = $false
    $installationCompleted = $false
    try {
        [void](New-Item -ItemType Directory -Path $stagingDirectory)
        Copy-SafeDirectoryContent `
            -Source $sourceDirectoryPath `
            -Destination $stagingDirectory `
            -DestinationRoot $stagingDirectory

        $stagedUninstaller = Join-Path $stagingDirectory 'Uninstall.ps1'
        if (-not (Test-Path -LiteralPath $stagedUninstaller -PathType Leaf)) {
            $sourceUninstaller = Join-Path $PSScriptRoot 'Uninstall.ps1'
            if (-not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) {
                throw 'Uninstall.ps1 was not found beside the installer or in the package payload.'
            }
            Copy-Item -LiteralPath $sourceUninstaller -Destination $stagedUninstaller
        }

        $stagedExecutable = Join-Path $stagingDirectory $script:ApplicationFileName
        if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf) -or
            (Get-Item -LiteralPath $stagedExecutable).Length -le 0) {
            throw 'The staged application payload failed validation.'
        }

        $stagedMarker = Join-Path $stagingDirectory $script:InstallMarkerName
        $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText(
            $stagedMarker,
            $script:InstallMarkerContent + [Environment]::NewLine,
            $utf8WithoutBom)

        if (Test-Path -LiteralPath $installDirectory -PathType Container) {
            Move-Item -LiteralPath $installDirectory -Destination $backupDirectory
            $oldInstallMoved = $true
        }

        Move-Item -LiteralPath $stagingDirectory -Destination $installDirectory
        $newInstallMoved = $true

        Set-ApplicationShortcut `
            -ShortcutPath $shortcutPath `
            -ExecutablePath $installedExecutable `
            -WorkingDirectory $installDirectory

        $uninstallRegistrationTouched = $true
        Set-UninstallRegistration `
            -InstallDirectory $installDirectory `
            -ExecutablePath $installedExecutable

        $installationCompleted = $true
    }
    catch {
        $installationError = $_
        $rollbackFailures = New-Object System.Collections.Generic.List[string]

        if ($newInstallMoved -and (Test-Path -LiteralPath $installDirectory -PathType Container)) {
            try {
                Move-Item -LiteralPath $installDirectory -Destination $failedDirectory
            }
            catch {
                [void]$rollbackFailures.Add(
                    "Could not quarantine the failed new installation: $($_.Exception.Message)")
            }
        }

        if ($oldInstallMoved -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            if (Test-Path -LiteralPath $installDirectory) {
                [void]$rollbackFailures.Add(
                    "The previous installation remains at '$backupDirectory' because the failed installation still occupies '$installDirectory'.")
            }
            else {
                try {
                    Move-Item -LiteralPath $backupDirectory -Destination $installDirectory
                }
                catch {
                    [void]$rollbackFailures.Add(
                        "Could not restore the previous installation from '$backupDirectory': $($_.Exception.Message)")
                }
            }
        }

        if (-not $shortcutExisted -and (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) {
            try {
                $failedShortcutTarget = Get-ShortcutTarget -ShortcutPath $shortcutPath
                if (-not [string]::IsNullOrWhiteSpace($failedShortcutTarget) -and
                    (Test-PathsEqual -Left $failedShortcutTarget -Right $installedExecutable)) {
                    [System.IO.File]::Delete($shortcutPath)
                }
            }
            catch {
                [void]$rollbackFailures.Add(
                    "Could not clean up the failed Start Menu shortcut: $($_.Exception.Message)")
            }
        }

        if ($uninstallRegistrationTouched) {
            try {
                Restore-UninstallRegistration -Snapshot $uninstallRegistrationSnapshot
            }
            catch {
                [void]$rollbackFailures.Add(
                    "Could not restore the previous Windows uninstall registration: $($_.Exception.Message)")
            }
        }

        if (Test-Path -LiteralPath $failedDirectory -PathType Container) {
            try {
                Remove-SafeDirectoryTree -Path $failedDirectory -RequiredParent $programsDirectory
            }
            catch {
                [void]$rollbackFailures.Add(
                    "Could not remove failed-install quarantine '$failedDirectory': $($_.Exception.Message)")
            }
        }

        if ($rollbackFailures.Count -gt 0) {
            $rollbackDetails = $rollbackFailures -join [Environment]::NewLine
            throw (New-Object System.InvalidOperationException(
                    "Installation failed: $($installationError.Exception.Message)$([Environment]::NewLine)Rollback issues:$([Environment]::NewLine)$rollbackDetails",
                    $installationError.Exception))
        }
        throw $installationError
    }
    finally {
        if (Test-Path -LiteralPath $stagingDirectory -PathType Container) {
            Remove-SafeDirectoryTree -Path $stagingDirectory -RequiredParent $programsDirectory
        }
    }

    if ($installationCompleted -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
        try {
            Remove-SafeDirectoryTree -Path $backupDirectory -RequiredParent $programsDirectory
        }
        catch {
            Write-Warning "Actions Ring was installed, but the previous-version backup could not be removed: '$backupDirectory'."
        }
    }

    Write-Status "Actions Ring was installed to '$installDirectory'."
    Write-Status "Start Menu shortcut: '$shortcutPath'."
    Write-Status 'Actions Ring is registered in Windows Installed apps for the current user.'
    Write-Status 'Autostart was not enabled. Configure it from inside Actions Ring.'
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
