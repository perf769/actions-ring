#Requires -Version 5.1

<#
.SYNOPSIS
Builds, tests, publishes, and packages Actions Ring for 64-bit Windows.

.DESCRIPTION
Runs the .NET 10 restore/build/test/publish pipeline, publishes a self-contained
single-file executable to artifacts\publish, and creates
dist\ActionsRing-portable.zip. The archive also contains the per-user install
and uninstall helpers.

.PARAMETER Configuration
The build configuration. Release is the production default.

.PARAMETER SkipTests
Skips the test step. Intended only for an explicitly shortened local build.

.PARAMETER Clean
Runs dotnet clean before the normal build pipeline.

.PARAMETER Quiet
Suppresses progress messages and selects quiet dotnet verbosity. Errors are
always reported.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    [switch]$Clean,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Quiet) {
    $ProgressPreference = 'SilentlyContinue'
}

$script:RuntimeIdentifier = 'win-x64'
$script:ApplicationFileName = 'ActionsRing.exe'

function ConvertTo-NormalizedFullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path was empty.'
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $root.Length) {
        $fullPath = $fullPath.TrimEnd([char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar))
    }

    return $fullPath
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

    $normalizedActual = ConvertTo-NormalizedFullPath -Path $Actual
    $normalizedExpected = ConvertTo-NormalizedFullPath -Path $Expected
    if (-not [string]::Equals(
            $normalizedActual,
            $normalizedExpected,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label resolved to an unexpected path. Expected '$normalizedExpected'; got '$normalizedActual'."
    }
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

    $normalizedPath = ConvertTo-NormalizedFullPath -Path $Path
    $normalizedParent = ConvertTo-NormalizedFullPath -Path $Parent
    $parentPrefix = $normalizedParent
    if (-not $parentPrefix.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $parentPrefix += [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $normalizedPath.StartsWith(
            $parentPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a child of '$normalizedParent'; got '$normalizedPath'."
    }
}

function Test-ReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileSystemInfo]$Item
    )

    return (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
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
    if (-not [string]::Equals(
            $destinationPath,
            $normalizedDestinationRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Assert-ChildPath -Path $destinationPath -Parent $normalizedDestinationRoot -Label 'Package destination'
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $sourcePath -Force)) {
        if (Test-ReparsePoint -Item $child) {
            throw "Refusing to package reparse point '$($child.FullName)'."
        }

        $destinationChild = Join-Path $destinationPath $child.Name
        Assert-ChildPath -Path $destinationChild -Parent $DestinationRoot -Label 'Package entry'
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

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$CommandArguments,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Status $Description
    & $script:DotNetPath @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed during '$Description' with exit code $LASTEXITCODE."
    }
}

$scriptDirectory = ConvertTo-NormalizedFullPath -Path $PSScriptRoot
$repositoryRoot = ConvertTo-NormalizedFullPath -Path (Join-Path $scriptDirectory '..')
$expectedBuildDirectory = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'build')
Assert-ExactPath -Actual $scriptDirectory -Expected $expectedBuildDirectory -Label 'Build script directory'

$applicationProject = ConvertTo-NormalizedFullPath -Path (
    Join-Path $repositoryRoot 'src\ActionsRing.App\ActionsRing.App.csproj')
$testTarget = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'ActionsRing.slnx')
$globalJson = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'global.json')
$artifactsRoot = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'artifacts')
$publishDirectory = ConvertTo-NormalizedFullPath -Path (Join-Path $artifactsRoot 'publish')
$distributionRoot = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'dist')
$archivePath = ConvertTo-NormalizedFullPath -Path (
    Join-Path $distributionRoot 'ActionsRing-portable.zip')
$installerDirectory = ConvertTo-NormalizedFullPath -Path (Join-Path $repositoryRoot 'installer')

Assert-ExactPath `
    -Actual $publishDirectory `
    -Expected (Join-Path $repositoryRoot 'artifacts\publish') `
    -Label 'Publish directory'
Assert-ExactPath `
    -Actual $archivePath `
    -Expected (Join-Path $repositoryRoot 'dist\ActionsRing-portable.zip') `
    -Label 'Distribution archive'
Assert-ChildPath -Path $publishDirectory -Parent $repositoryRoot -Label 'Publish directory'
Assert-ChildPath -Path $archivePath -Parent $repositoryRoot -Label 'Distribution archive'

foreach ($outputRoot in @($artifactsRoot, $distributionRoot)) {
    Assert-ChildPath -Path $outputRoot -Parent $repositoryRoot -Label 'Build output root'
    if (Test-Path -LiteralPath $outputRoot -PathType Leaf) {
        throw "Build output root is occupied by a file: '$outputRoot'."
    }
    if (Test-Path -LiteralPath $outputRoot -PathType Container) {
        $outputRootItem = Get-Item -LiteralPath $outputRoot -Force
        if (Test-ReparsePoint -Item $outputRootItem) {
            throw "Refusing to write through reparse-point build output root '$outputRoot'."
        }
    }
}

foreach ($requiredFile in @(
        $applicationProject,
        $testTarget,
        $globalJson,
        (Join-Path $installerDirectory 'Install.ps1'),
        (Join-Path $installerDirectory 'Uninstall.ps1'),
        (Join-Path $installerDirectory 'README.md'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file was not found: '$requiredFile'."
    }
}

$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $dotnetCommand) {
    throw 'The .NET SDK was not found on PATH. Install the .NET 10 SDK and try again.'
}
$script:DotNetPath = $dotnetCommand.Source

$sdkVersionOutput = @(& $script:DotNetPath --version)
if ($LASTEXITCODE -ne 0 -or $sdkVersionOutput.Count -eq 0) {
    throw 'Unable to determine the active .NET SDK version.'
}
$sdkVersion = [string]$sdkVersionOutput[-1]
if ($sdkVersion -notmatch '^10\.') {
    throw "The active SDK is '$sdkVersion'. Actions Ring requires .NET SDK 10.x."
}
Write-Status "Using .NET SDK $sdkVersion."

$verbosity = 'minimal'
if ($Quiet) {
    $verbosity = 'quiet'
}

Push-Location -LiteralPath $repositoryRoot
try {
    if ($Clean) {
        Invoke-DotNet -Description 'Cleaning the application project...' -CommandArguments @(
            'clean', $applicationProject,
            '--configuration', $Configuration,
            '--runtime', $script:RuntimeIdentifier,
            '--nologo',
            '--verbosity', $verbosity)
        Invoke-DotNet -Description 'Cleaning the solution...' -CommandArguments @(
            'clean', $testTarget,
            '--configuration', $Configuration,
            '--nologo',
            '--verbosity', $verbosity)
    }

    Invoke-DotNet -Description 'Restoring the application...' -CommandArguments @(
        'restore', $applicationProject,
        '--runtime', $script:RuntimeIdentifier,
        '--nologo',
        '--verbosity', $verbosity)

    if (-not $SkipTests) {
        Invoke-DotNet -Description 'Restoring the solution tests...' -CommandArguments @(
            'restore', $testTarget,
            '--nologo',
            '--verbosity', $verbosity)
    }

    Invoke-DotNet -Description 'Building Actions Ring...' -CommandArguments @(
        'build', $applicationProject,
        '--configuration', $Configuration,
        '--runtime', $script:RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--nologo',
        '--verbosity', $verbosity,
        '-p:ContinuousIntegrationBuild=true')

    if (-not $SkipTests) {
        Invoke-DotNet -Description 'Running the test suite...' -CommandArguments @(
            'test', $testTarget,
            '--configuration', $Configuration,
            '--no-restore',
            '--nologo',
            '--verbosity', $verbosity,
            '-p:ContinuousIntegrationBuild=true')
    }

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-SafeDirectoryTree -Path $publishDirectory -RequiredParent $artifactsRoot
    }
    [void](New-Item -ItemType Directory -Path $publishDirectory -Force)

    Invoke-DotNet -Description 'Publishing the self-contained single-file application...' -CommandArguments @(
        'publish', $applicationProject,
        '--configuration', $Configuration,
        '--runtime', $script:RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '--no-restore',
        '--nologo',
        '--verbosity', $verbosity,
        '-p:ContinuousIntegrationBuild=true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=true',
        '-p:GenerateDocumentationFile=false',
        '-p:DebugType=embedded')
}
finally {
    Pop-Location
}

$publishedApplication = Join-Path $publishDirectory $script:ApplicationFileName
if (-not (Test-Path -LiteralPath $publishedApplication -PathType Leaf)) {
    throw "Publish completed without the expected executable '$publishedApplication'."
}
if ((Get-Item -LiteralPath $publishedApplication).Length -le 0) {
    throw "Published executable '$publishedApplication' is empty."
}

$unexpectedPublishedItems = @(Get-ChildItem -LiteralPath $publishDirectory -Force |
        Where-Object {
            -not ($_.PSIsContainer -eq $false -and
                [string]::Equals(
                    $_.Name,
                    $script:ApplicationFileName,
                    [System.StringComparison]::OrdinalIgnoreCase))
        })
if ($unexpectedPublishedItems.Count -gt 0) {
    $names = ($unexpectedPublishedItems | ForEach-Object Name) -join ', '
    throw "Publish was expected to contain exactly one executable, but additional items remain: $names"
}

[void](New-Item -ItemType Directory -Path $distributionRoot -Force)
if (Test-Path -LiteralPath $archivePath -PathType Container) {
    throw "Refusing to replace '$archivePath' because it is a directory."
}
if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    Assert-ExactPath `
        -Actual $archivePath `
        -Expected (Join-Path $repositoryRoot 'dist\ActionsRing-portable.zip') `
        -Label 'Archive selected for replacement'
    $archiveItem = Get-Item -LiteralPath $archivePath -Force
    if (Test-ReparsePoint -Item $archiveItem) {
        throw "Refusing to replace reparse-point archive '$archivePath'."
    }
}

$packageStage = ConvertTo-NormalizedFullPath -Path (
    Join-Path $artifactsRoot ('.ActionsRing-package-' + [guid]::NewGuid().ToString('N')))
$temporaryArchive = ConvertTo-NormalizedFullPath -Path (
    Join-Path $distributionRoot ('.ActionsRing-portable-' + [guid]::NewGuid().ToString('N') + '.tmp'))
$backupArchive = ConvertTo-NormalizedFullPath -Path (
    Join-Path $distributionRoot ('.ActionsRing-portable-' + [guid]::NewGuid().ToString('N') + '.bak'))
Assert-ChildPath -Path $packageStage -Parent $artifactsRoot -Label 'Package staging directory'
Assert-ChildPath -Path $temporaryArchive -Parent $distributionRoot -Label 'Temporary archive'
Assert-ChildPath -Path $backupArchive -Parent $distributionRoot -Label 'Backup archive'

try {
    [void](New-Item -ItemType Directory -Path $packageStage)
    Copy-SafeDirectoryContent `
        -Source $publishDirectory `
        -Destination $packageStage `
        -DestinationRoot $packageStage

    Copy-Item `
        -LiteralPath (Join-Path $installerDirectory 'Install.ps1') `
        -Destination (Join-Path $packageStage 'Install.ps1')
    Copy-Item `
        -LiteralPath (Join-Path $installerDirectory 'Uninstall.ps1') `
        -Destination (Join-Path $packageStage 'Uninstall.ps1')
    Copy-Item `
        -LiteralPath (Join-Path $installerDirectory 'README.md') `
        -Destination (Join-Path $packageStage 'README.md')

    $licensesDirectory = Join-Path $packageStage 'licenses'
    [void](New-Item -ItemType Directory -Path $licensesDirectory)
    foreach ($licenseName in @('NOTICE.md', 'LUCIDE-LICENSE.txt', 'SIMPLE-ICONS-LICENSE.txt', 'SIMPLE-ICONS-DISCLAIMER.md', 'simple-icons-attribution.json')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot ('src\ActionsRing.App\Assets\Icons\' + $licenseName)) -Destination (Join-Path $licensesDirectory $licenseName)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $packageStage,
        $temporaryArchive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    if (-not (Test-Path -LiteralPath $temporaryArchive -PathType Leaf) -or
        (Get-Item -LiteralPath $temporaryArchive).Length -le 0) {
        throw 'Portable archive creation did not produce a valid file.'
    }

    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        [System.IO.File]::Replace($temporaryArchive, $archivePath, $backupArchive, $true)
        [System.IO.File]::Delete($backupArchive)
    }
    else {
        Move-Item -LiteralPath $temporaryArchive -Destination $archivePath
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryArchive -PathType Leaf) {
        Assert-ChildPath -Path $temporaryArchive -Parent $distributionRoot -Label 'Temporary archive cleanup'
        [System.IO.File]::Delete($temporaryArchive)
    }
    if (Test-Path -LiteralPath $backupArchive -PathType Leaf) {
        Assert-ChildPath -Path $backupArchive -Parent $distributionRoot -Label 'Backup archive cleanup'
        [System.IO.File]::Delete($backupArchive)
    }
    if (Test-Path -LiteralPath $packageStage) {
        Remove-SafeDirectoryTree -Path $packageStage -RequiredParent $artifactsRoot
    }
}

Write-Status "Published: $publishDirectory"
Write-Status "Packaged:  $archivePath"
