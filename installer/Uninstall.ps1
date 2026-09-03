#Requires -Version 5.1

<#
.SYNOPSIS
Uninstalls Actions Ring for the current Windows user.

.DESCRIPTION
Removes the per-user application files, owned Start Menu shortcut, and an
Actions Ring autostart value only when it points to the installed executable.
User settings in %LOCALAPPDATA%\ActionsRing are preserved by default.

.PARAMETER RemoveUserConfig
Also removes %LOCALAPPDATA%\ActionsRing, including settings and backups.

.PARAMETER Quiet
Suppresses informational output. Errors are always reported.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$RemoveUserConfig,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Quiet) {
    $ProgressPreference = 'SilentlyContinue'
}

$script:ApplicationFileName = 'ActionsRing.exe'
$script:ShortcutFileName = 'Actions Ring.lnk'
$script:RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$script:StartupApprovedRunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$script:AutostartValueNames = @('ActionsRing', 'Actions Ring')
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

function Assert-OwnedInstallDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $markerPath = Join-Path $Path $script:InstallMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Refusing to remove '$Path' because it has no Actions Ring installer ownership marker."
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
        throw "Refusing to remove '$Path' because its ownership marker is invalid."
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

function Get-ExecutableFromCommand {
    param([string]$Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $null
    }

    $trimmed = $Command.TrimStart()
    if ($trimmed.StartsWith('"')) {
        $closingQuote = $trimmed.IndexOf('"', 1)
        if ($closingQuote -gt 1) {
            return $trimmed.Substring(1, $closingQuote - 1)
        }
        return $null
    }

    $separator = $trimmed.IndexOfAny([char[]]@(' ', "`t"))
    if ($separator -lt 0) {
        return $trimmed
    }
    return $trimmed.Substring(0, $separator)
}

function Test-OwnedUninstallRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedInstallDirectory
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:UninstallKeyPath, $false)
    if ($null -eq $key) {
        return $false
    }

    try {
        $schema = $key.GetValue('InstallerSchema', $null)
        $location = [string]$key.GetValue(
            'InstallLocation',
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return ([int]$schema -eq 1 -and
            -not [string]::IsNullOrWhiteSpace($location) -and
            (Test-PathsEqual -Left $location -Right $ExpectedInstallDirectory))
    }
    catch {
        return $false
    }
    finally {
        $key.Dispose()
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Actions Ring can only be uninstalled on Windows.'
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Actions Ring requires 64-bit Windows (win-x64).'
    }
    if (-not [Environment]::Is64BitProcess) {
        throw 'Run the uninstaller with 64-bit Windows PowerShell. Actions Ring targets win-x64.'
    }

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
    $configurationDirectory = ConvertTo-NormalizedFullPath -Path (
        Join-Path $localApplicationData 'ActionsRing')

    Assert-ExactPath `
        -Actual $installDirectory `
        -Expected (Join-Path $localApplicationData 'Programs\ActionsRing') `
        -Label 'Install directory'
    Assert-ExactPath `
        -Actual $configurationDirectory `
        -Expected (Join-Path $localApplicationData 'ActionsRing') `
        -Label 'Configuration directory'
    Assert-ChildPath -Path $installDirectory -Parent $programsDirectory -Label 'Install directory'
    Assert-ChildPath -Path $configurationDirectory -Parent $localApplicationData -Label 'Configuration directory'

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

    if (Test-Path -LiteralPath $installDirectory -PathType Leaf) {
        throw "Install path is occupied by a file: '$installDirectory'."
    }
    $installExists = Test-Path -LiteralPath $installDirectory -PathType Container
    if ($installExists) {
        $installItem = Get-Item -LiteralPath $installDirectory -Force
        if (Test-ReparsePoint -Item $installItem) {
            throw "Refusing to remove reparse-point install directory '$installDirectory'."
        }
        Assert-OwnedInstallDirectory -Path $installDirectory
    }

    if ($RemoveUserConfig -and (Test-Path -LiteralPath $configurationDirectory -PathType Leaf)) {
        throw "Configuration path is occupied by a file: '$configurationDirectory'."
    }
    if ($RemoveUserConfig -and (Test-Path -LiteralPath $configurationDirectory -PathType Container)) {
        $configurationItem = Get-Item -LiteralPath $configurationDirectory -Force
        if (Test-ReparsePoint -Item $configurationItem) {
            throw "Refusing to remove reparse-point configuration directory '$configurationDirectory'."
        }
    }

    if ($installExists) {
        $runningProcesses = @(Get-Process -Name 'ActionsRing' -ErrorAction SilentlyContinue)
        foreach ($process in $runningProcesses) {
            $processPath = $null
            try {
                $processPath = $process.Path
            }
            catch {
                throw 'An ActionsRing process is running but its path cannot be verified. Exit it, then run the uninstaller again.'
            }

            if ([string]::IsNullOrWhiteSpace($processPath)) {
                throw 'An ActionsRing process is running but its path cannot be verified. Exit it, then run the uninstaller again.'
            }
            if (Test-PathsEqual -Left $processPath -Right $installedExecutable) {
                throw 'Actions Ring is running. Exit it from the notification area, then run the uninstaller again.'
            }
        }
    }

    $removeShortcut = $false
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        try {
            $shortcutTarget = Get-ShortcutTarget -ShortcutPath $shortcutPath
            if (-not [string]::IsNullOrWhiteSpace($shortcutTarget) -and
                (Test-PathsEqual -Left $shortcutTarget -Right $installedExecutable)) {
                $removeShortcut = $true
            }
            else {
                Write-Warning "The shortcut '$shortcutPath' points elsewhere and will be preserved."
            }
        }
        catch {
            Write-Warning "The shortcut '$shortcutPath' could not be verified and will be preserved: $($_.Exception.Message)"
        }
    }

    $autostartValuesToRemove = New-Object System.Collections.Generic.List[string]
    $readOnlyRunKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RunKeyPath, $false)
    if ($null -ne $readOnlyRunKey) {
        try {
            foreach ($valueName in $script:AutostartValueNames) {
                $command = $readOnlyRunKey.GetValue(
                    $valueName,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $autostartExecutable = Get-ExecutableFromCommand -Command ([string]$command)
                if (-not [string]::IsNullOrWhiteSpace($autostartExecutable) -and
                    (Test-PathsEqual -Left $autostartExecutable -Right $installedExecutable)) {
                    [void]$autostartValuesToRemove.Add($valueName)
                }
            }
        }
        finally {
            $readOnlyRunKey.Dispose()
        }
    }

    $registrationProbe = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        $script:UninstallKeyPath,
        $false)
    $uninstallRegistrationExists = $null -ne $registrationProbe
    if ($null -ne $registrationProbe) {
        $registrationProbe.Dispose()
    }
    $removeUninstallRegistration = Test-OwnedUninstallRegistration `
        -ExpectedInstallDirectory $installDirectory
    if ($uninstallRegistrationExists -and -not $removeUninstallRegistration) {
        Write-Warning "The Windows uninstall registration 'HKCU\$($script:UninstallKeyPath)' is not owned by this installation and will be preserved."
    }

    $description = 'Uninstall Actions Ring while preserving current-user settings'
    if ($RemoveUserConfig) {
        $description = 'Uninstall Actions Ring and permanently remove current-user settings'
    }
    if (-not $PSCmdlet.ShouldProcess($installDirectory, $description)) {
        return
    }

    $uninstallErrors = New-Object System.Collections.Generic.List[string]
    $quarantineDirectory = $null
    if ($installExists) {
        $quarantineDirectory = ConvertTo-NormalizedFullPath -Path (
            Join-Path $programsDirectory ('.ActionsRing-uninstall-' + [guid]::NewGuid().ToString('N')))
        Assert-ChildPath -Path $quarantineDirectory -Parent $programsDirectory -Label 'Uninstall quarantine'

        $currentDirectory = ConvertTo-NormalizedFullPath -Path ([Environment]::CurrentDirectory)
        $currentLocationPath = $null
        try {
            $currentLocation = Get-Location
            if ($null -ne $currentLocation.Provider -and
                [string]::Equals(
                    $currentLocation.Provider.Name,
                    'FileSystem',
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $currentLocationPath = ConvertTo-NormalizedFullPath -Path $currentLocation.ProviderPath
            }
        }
        catch {
            $currentLocationPath = $null
        }

        $environmentDirectoryIsInsideInstall =
            (Test-PathsEqual -Left $currentDirectory -Right $installDirectory) -or
            (Test-IsChildPath -Path $currentDirectory -Parent $installDirectory)
        $providerLocationIsInsideInstall =
            -not [string]::IsNullOrWhiteSpace($currentLocationPath) -and
            ((Test-PathsEqual -Left $currentLocationPath -Right $installDirectory) -or
                (Test-IsChildPath -Path $currentLocationPath -Parent $installDirectory))
        if ($environmentDirectoryIsInsideInstall -or $providerLocationIsInsideInstall) {
            $safeWorkingDirectory = ConvertTo-NormalizedFullPath -Path ([System.IO.Path]::GetTempPath())
            [Environment]::CurrentDirectory = $safeWorkingDirectory
            if ($providerLocationIsInsideInstall) {
                Set-Location -LiteralPath $safeWorkingDirectory
            }
        }

        # Stage removal with a same-volume rename. If the rename or cleanup
        # fails, no shortcut, registry, or configuration state has been changed.
        $retryUninstallerPath = $PSCommandPath
        if ([string]::IsNullOrWhiteSpace($retryUninstallerPath) -or
            -not (Test-Path -LiteralPath $retryUninstallerPath -PathType Leaf)) {
            $retryUninstallerPath = Join-Path $installDirectory 'Uninstall.ps1'
        }
        if ([string]::IsNullOrWhiteSpace($retryUninstallerPath) -or
            -not (Test-Path -LiteralPath $retryUninstallerPath -PathType Leaf)) {
            throw 'A retry-capable Uninstall.ps1 could not be read before staging removal.'
        }
        $retryUninstallerBytes = [System.IO.File]::ReadAllBytes($retryUninstallerPath)
        $installMarkerBytes = [System.IO.File]::ReadAllBytes(
            (Join-Path $installDirectory $script:InstallMarkerName))

        Move-Item -LiteralPath $installDirectory -Destination $quarantineDirectory

        try {
            Remove-SafeDirectoryTree -Path $quarantineDirectory -RequiredParent $programsDirectory
            Write-Status "Removed application directory '$installDirectory'."
        }
        catch {
            $cleanupError = $_
            $rollbackErrors = New-Object System.Collections.Generic.List[string]
            if (Test-Path -LiteralPath $quarantineDirectory -PathType Container) {
                try {
                    $quarantineUninstaller = Join-Path $quarantineDirectory 'Uninstall.ps1'
                    if (Test-Path -LiteralPath $quarantineUninstaller -PathType Leaf) {
                        [System.IO.File]::SetAttributes(
                            $quarantineUninstaller,
                            [System.IO.FileAttributes]::Normal)
                    }
                    [System.IO.File]::WriteAllBytes($quarantineUninstaller, $retryUninstallerBytes)
                    [System.IO.File]::WriteAllBytes(
                        (Join-Path $quarantineDirectory $script:InstallMarkerName),
                        $installMarkerBytes)
                }
                catch {
                    [void]$rollbackErrors.Add(
                        "Could not restore retry files in '$quarantineDirectory': $($_.Exception.Message)")
                }

                if (-not (Test-Path -LiteralPath $installDirectory)) {
                    try {
                        Move-Item -LiteralPath $quarantineDirectory -Destination $installDirectory
                    }
                    catch {
                        [void]$rollbackErrors.Add(
                            "Could not return the residual installation to '$installDirectory': $($_.Exception.Message)")
                    }
                }
                else {
                    [void]$rollbackErrors.Add(
                        "Could not return the residual installation because '$installDirectory' is occupied.")
                }
            }

            if ($rollbackErrors.Count -gt 0) {
                throw (New-Object System.InvalidOperationException(
                        "Application cleanup failed: $($cleanupError.Exception.Message)$([Environment]::NewLine)Rollback issues:$([Environment]::NewLine)$($rollbackErrors -join [Environment]::NewLine)",
                        $cleanupError.Exception))
            }
            throw $cleanupError
        }
    }
    else {
        Write-Status 'Actions Ring application files were already absent.'
    }

    if ($removeShortcut) {
        try {
            [System.IO.File]::Delete($shortcutPath)
            Write-Status "Removed Start Menu shortcut '$shortcutPath'."
        }
        catch {
            [void]$uninstallErrors.Add(
                "Could not remove Start Menu shortcut '$shortcutPath': $($_.Exception.Message)")
        }
    }

    if ($autostartValuesToRemove.Count -gt 0) {
        $writableRunKey = $null
        $startupApprovedKey = $null
        try {
            $writableRunKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RunKeyPath, $true)
            if ($null -eq $writableRunKey) {
                throw 'The current-user Run registry key could not be opened for writing.'
            }
            foreach ($valueName in $autostartValuesToRemove) {
                $writableRunKey.DeleteValue($valueName, $false)
                if ($null -eq $startupApprovedKey) {
                    $startupApprovedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
                        $script:StartupApprovedRunKeyPath,
                        $true)
                }
                if ($null -ne $startupApprovedKey) {
                    $startupApprovedKey.DeleteValue($valueName, $false)
                }
                Write-Status "Removed autostart value '$valueName'."
            }
        }
        catch {
            [void]$uninstallErrors.Add(
                "Could not remove an Actions Ring autostart value: $($_.Exception.Message)")
        }
        finally {
            if ($null -ne $writableRunKey) {
                $writableRunKey.Dispose()
            }
            if ($null -ne $startupApprovedKey) {
                $startupApprovedKey.Dispose()
            }
        }
    }

    if ($removeUninstallRegistration) {
        try {
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(
                $script:UninstallKeyPath,
                $false)
            Write-Status 'Removed the current-user Windows Installed apps registration.'
        }
        catch {
            [void]$uninstallErrors.Add(
                "Could not remove the Windows uninstall registration: $($_.Exception.Message)")
        }
    }

    if ($RemoveUserConfig) {
        if (Test-Path -LiteralPath $configurationDirectory -PathType Container) {
            try {
                Remove-SafeDirectoryTree `
                    -Path $configurationDirectory `
                    -RequiredParent $localApplicationData
                Write-Status "Removed user configuration '$configurationDirectory'."
            }
            catch {
                [void]$uninstallErrors.Add(
                    "Could not remove user configuration '$configurationDirectory': $($_.Exception.Message)")
            }
        }
    }
    else {
        Write-Status "User configuration was preserved in '$configurationDirectory'."
    }

    if ($uninstallErrors.Count -gt 0) {
        throw (New-Object System.InvalidOperationException(
                ('Uninstall completed with cleanup errors:' + [Environment]::NewLine +
                    ($uninstallErrors -join [Environment]::NewLine))))
    }

    Write-Status 'Actions Ring was uninstalled for the current user.'
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
