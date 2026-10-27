[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string[]] $RuntimeIdentifiers = @('win-x64', 'linux-x64'),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.+-]{0,63}$')]
    [string] $Version = '0.1.1',
    [string] $OutputRoot,
    [string] $NuGetConfig,
    [switch] $NoArchive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    # Also support a reviewed scoped ScriptBlock invocation from the repo root,
    # without changing the machine's PowerShell script execution policy.
    [IO.Path]::GetFullPath((Get-Location).Path)
} else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
$project = Join-Path $repository 'src/Wcc.App/Wcc.App.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'Run this script from the WCC repository root.' }
$hostTargets = Join-Path $repository 'scripts/PortableAppHost.targets'
if (-not (Test-Path -LiteralPath $hostTargets -PathType Leaf)) { throw 'The portable native host target is missing.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repository 'artifacts/portable' }
$output = [IO.Path]::GetFullPath($OutputRoot)
$nugetConfigPath = $null
if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
    $nugetConfigPath = [IO.Path]::GetFullPath($NuGetConfig)
    if (-not (Test-Path -LiteralPath $nugetConfigPath -PathType Leaf)) { throw "NuGet configuration not found: $nugetConfigPath" }
}
$isLinuxHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Unix -and (Test-Path -LiteralPath '/proc/sys/kernel/ostype')
$utf8 = [Text.UTF8Encoding]::new($false)
$rids = @($RuntimeIdentifiers | Select-Object -Unique)
$requiredNotices = @('WCC.txt', 'WCC-LICENSE.txt', 'LGPL-3.0.txt', 'GPL-3.0.txt', 'Wesnoth.txt', 'Wesnoth-fonts.txt', 'Wesnoth-copyright.txt', 'Wesnoth-images.txt',
    'AvaloniaEdit.txt', 'Avalonia.txt', 'Inter-font.txt', 'Pictogrammers.txt', 'Apache-2.0.txt',
    'MicroCom.Runtime.txt', 'Tmds.DBus.txt')

function Invoke-NativeChecked([string] $file, [string[]] $arguments, [string] $operation) {
    $command = Get-Command -Name $file -CommandType Application -ErrorAction Stop
    $previousPreference = $ErrorActionPreference
    try {
        # PowerShell 5 treats redirected native stderr as ErrorRecords. Warnings
        # (including an existing NuGet advisory) must not replace the exit code.
        $ErrorActionPreference = 'Continue'
        & $command.Source @arguments
        $nativeExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($nativeExitCode -ne 0) { throw "$operation failed with exit code $nativeExitCode. Partial staging output was retained." }
}

function Copy-PackageNotices([string] $destination, [string] $assetsPath, [string] $rid) {
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $noticeRoot = Join-Path $destination 'ThirdPartyNotices/Packages'
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $package = $library.Value
        if ($null -eq $package.PSObject.Properties['path'] -or $null -eq $package.PSObject.Properties['files']) {
            throw "Restored package metadata is missing path/files: $($library.Name)"
        }
        $packageDirectory = $null
        foreach ($packageRoot in $packageRoots) {
            $candidate = Join-Path $packageRoot $package.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if ($null -eq $packageDirectory) { throw "Cannot locate restored package $($library.Name) for its notices." }
        $target = Join-Path $noticeRoot $package.path
        foreach ($relative in $package.files) {
            if ($relative -notmatch '(?i)(^|/)(licen[cs]e|copying|copyright|notice|third[-_]party[-_]notices?)(\.[^/]*)?$' -and
                $relative -notmatch '(?i)\.nuspec$') { continue }
            $source = Join-Path $packageDirectory $relative
            $notice = Join-Path $target $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($notice)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $notice
        }
    }

    # Self-contained runtime packs are downloaded separately by the SDK and may
    # not appear in the regular package library list above.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $destination 'Wcc.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
    if ($runtime.Count -ne 1) { throw 'The output is not a self-contained Microsoft.NETCore.App deployment.' }
    $runtimeRoot = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot ("microsoft.netcore.app.runtime.$rid/" + $runtime[0].version)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $runtimeRoot = $candidate; break }
    }
    if ($null -eq $runtimeRoot) { throw "Cannot locate the restored $rid runtime pack for its license and notices." }
    $runtimeTarget = Join-Path $destination 'ThirdPartyNotices/DotNet'
    [IO.Directory]::CreateDirectory($runtimeTarget) | Out-Null
    $runtimeNotices = @(Get-ChildItem -LiteralPath $runtimeRoot -File | Where-Object Name -Match '(?i)license|notice')
    if ($runtimeNotices.Count -eq 0) { throw 'The runtime pack has no discoverable license/notice files.' }
    foreach ($notice in $runtimeNotices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $runtimeTarget $notice.Name) }
}

function Assert-CleanDistribution([string] $directory) {
    foreach ($stateRoot in @($directory, (Join-Path $directory 'bin'))) {
        foreach ($mutable in @('Library', 'Cache', 'settings.json')) {
            if (Test-Path -LiteralPath (Join-Path $stateRoot $mutable)) { throw "Mutable user state leaked into the release: $mutable" }
        }
        if (@(Get-ChildItem -LiteralPath $stateRoot -Force | Where-Object Name -Like '.wcc-*').Count -ne 0) {
            throw 'Portable location/startup files leaked into the release.'
        }
    }
    foreach ($notice in $requiredNotices) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory "ThirdPartyNotices/$notice") -PathType Leaf)) { throw "Missing bundled notice: $notice" }
    }
    foreach ($stateRoot in @($directory, (Join-Path $directory 'bin'), (Join-Path $directory 'ThirdPartyNotices'))) {
        if (Test-Path -LiteralPath (Join-Path $stateRoot 'copied-images.txt')) { throw 'Internal image inventory leaked into the distribution.' }
    }
}

function Assert-DistributionChild([string] $directory, [string] $path) {
    $prefix = [IO.Path]::GetFullPath($directory).TrimEnd([char[]]@('/', '\')) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($path)
    $comparison = if ($isLinuxHost) { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }
    if (-not $fullPath.StartsWith($prefix, $comparison)) { throw "Release operation escaped its staging directory: $fullPath" }
    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected linked release entry: $fullPath" }
    }
}

function Set-PortableLayout([string] $directory, [string] $nativeHost, [string] $rid) {
    $executableName = if ($rid -eq 'win-x64') { 'WCC.exe' } else { 'WCC' }
    $stockName = if ($rid -eq 'win-x64') { 'Wcc.App.exe' } else { 'Wcc.App' }
    $stockHost = Join-Path $directory $stockName
    if (-not (Test-Path -LiteralPath $nativeHost -PathType Leaf) -or
        -not (Test-Path -LiteralPath $stockHost -PathType Leaf)) { throw "Native apphost creation was incomplete for $rid." }
    # Preserve the stock SDK host outside the release as publication evidence.
    # Only the custom SDK host understands the bin/ managed entry-point path.
    Assert-DistributionChild $directory $stockHost
    $stockEvidence = Join-Path ([IO.Path]::GetDirectoryName($nativeHost)) ("stock-$stockName")
    Assert-DistributionChild ([IO.Path]::GetDirectoryName($nativeHost)) $stockEvidence
    [IO.File]::Move($stockHost, $stockEvidence)

    $entries = @(Get-ChildItem -LiteralPath $directory -Force | Where-Object Name -ne 'ThirdPartyNotices')
    $bin = Join-Path $directory 'bin'
    [IO.Directory]::CreateDirectory($bin) | Out-Null
    foreach ($entry in $entries) {
        $target = Join-Path $bin $entry.Name
        Assert-DistributionChild $directory $entry.FullName
        Assert-DistributionChild $directory $target
        if ($entry.PSIsContainer) { [IO.Directory]::Move($entry.FullName, $target) }
        else { [IO.File]::Move($entry.FullName, $target) }
    }
    $executable = Join-Path $directory $executableName
    Assert-DistributionChild ([IO.Path]::GetDirectoryName($nativeHost)) $nativeHost
    Assert-DistributionChild $directory $executable
    [IO.File]::Move($nativeHost, $executable)
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        if ($entry.Name -notin @('ThirdPartyNotices', 'bin', $executableName)) { throw "Unexpected release root entry: $($entry.Name)" }
    }
    return $executable
}

[IO.Directory]::CreateDirectory($output) | Out-Null
foreach ($rid in $rids) {
    $leaf = "WCC-$Version-$rid"
    foreach ($path in @((Join-Path $output $leaf), (Join-Path $output "$leaf.zip"), (Join-Path $output "$leaf.tar.gz"))) {
        if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing release output: $path" }
    }
}

Push-Location $repository
try {
    foreach ($rid in $rids) {
        $leaf = "WCC-$Version-$rid"
        $stagingParent = Join-Path $output ('.wcc-publish-' + [Guid]::NewGuid().ToString('N'))
        $staging = Join-Path $stagingParent $leaf
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        $nativeHost = Join-Path $stagingParent $(if ($rid -eq 'win-x64') { 'WCC.exe' } else { 'WCC' })
        # Version labels the review archive; changing assembly metadata here would
        # make packaged App/Core bytes differ from the binaries already tested.
        $publishArguments = @('publish', $project, '--configuration', 'Release', '--runtime', $rid, '--self-contained', 'true',
            '--output', $staging, '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:PublishAot=false',
            '-p:WccPortablePublish=true', "-p:CustomAfterMicrosoftCommonTargets=$hostTargets", "-p:WccPortableHostPath=$nativeHost")
        if ($null -ne $nugetConfigPath) { $publishArguments += "-p:RestoreConfigFile=$nugetConfigPath" }
        Invoke-NativeChecked 'dotnet' $publishArguments "Publish $rid"
        Copy-PackageNotices $staging (Join-Path $repository 'src/Wcc.App/obj/project.assets.json') $rid
        $executable = Set-PortableLayout $staging $nativeHost $rid
        Assert-CleanDistribution $staging
        $launch = if ($rid -eq 'win-x64') { 'Run WCC.exe.' } else { 'Run ./WCC from a desktop Linux session (X11 or XWayland).' }
        $readme = @"
WCC $Version ($rid)

$launch
This folder includes .NET; no separate .NET installation is needed. Extract the
whole archive into a writable folder before starting. Keep all bundled files.

WCC stores Library/, Cache/ and settings.json beside its executable.
Close WCC before moving/copying this entire folder. Installed Wesnoth and campaign
paths remain external; select the correct local paths after changing computers.
The release archive contains no personal Library, settings or analysis caches.

Folder layout

WCC.exe (Windows) or WCC (Linux) is the native application entry point.
bin/ contains application assemblies, .NET, Avalonia and native support files.
Keep bin/ intact; do not start its DLLs directly or split these files further.
ThirdPartyNotices/WCC.txt lists WCC's license, components and bundled assets.
ThirdPartyNotices/ also contains their license texts and package metadata.
ThirdPartyNotices/Wesnoth-images.txt lists the bundled Wesnoth image filenames.
ThirdPartyNotices/SHA256SUMS records package files relative to this root folder.

Linux requirements and checked saving

Linux uses an X11 desktop session or XWayland. The OS must provide X11, ICE, SM and
fontconfig, plus the .NET dependencies for that distribution: glibc, GCC/stdc++, ICU,
SSL, zlib, Brotli, Kerberos, timezone and certificate libraries. Self-contained means
that .NET is included; these operating-system libraries are not bundled.
https://docs.avaloniaui.net/docs/deployment/linux
https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install

Checked saving needs a filesystem supporting statx identities and renameat2 atomic
exchange/no-replace operations. Source routes with symbolic links are refused.
Extended POSIX access ACLs are refused until exact preservation is supported.
Use an ordinary local case-sensitive filesystem for Linux testing. File locks are
advisory: other processes can still rename directory entries. Detected namespace
interference retains recovery evidence instead of attempting automatic rollback.

After a Linux replacement, hidden .wcc-stage-*.tmp files retain the displaced
baseline. A completed save can report a cleanup warning for these retained files.
They provide recovery evidence; this is not automatic transactional rollback.
Close WCC and back up the campaign before manually inspecting/removing old stages.

"@
        [IO.File]::WriteAllText((Join-Path $staging 'README.txt'), $readme, $utf8)
        if ($rid -eq 'linux-x64' -and $isLinuxHost) {
            Invoke-NativeChecked 'chmod' @('755', '--', $executable) 'Set Linux apphost executable mode'
            $dumpHelper = Join-Path $staging 'bin/createdump'
            if (Test-Path -LiteralPath $dumpHelper -PathType Leaf) {
                Invoke-NativeChecked 'chmod' @('755', '--', $dumpHelper) 'Set Linux runtime dump helper executable mode'
            }
        }
        $prefix = $staging.TrimEnd([char[]]@('/', '\')) + [IO.Path]::DirectorySeparatorChar
        $hashes = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object FullName | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + ' *' + $_.FullName.Substring($prefix.Length).Replace('\', '/')
        })
        [IO.File]::WriteAllLines((Join-Path $staging 'ThirdPartyNotices/SHA256SUMS'), [string[]]$hashes, $utf8)
        $published = Join-Path $output $leaf
        Assert-DistributionChild $output $staging
        Assert-DistributionChild $output $published
        [IO.Directory]::Move($staging, $published)
        if (-not $NoArchive) {
            if ($rid -eq 'win-x64') { Compress-Archive -LiteralPath $published -DestinationPath (Join-Path $output "$leaf.zip") }
            elseif ($isLinuxHost) {
                Invoke-NativeChecked 'tar' @('--sort=name', '--mtime=@0', '--owner=0', '--group=0', '--numeric-owner', '-czf',
                    (Join-Path $output "$leaf.tar.gz"), '-C', $output, $leaf) 'Archive Linux distribution'
            }
            else { Write-Warning "Linux folder published at $published. Create its tar.gz on Linux after chmod 755 WCC; see docs/portable-builds.md." }
        }
        Write-Output $published
    }
}
finally { Pop-Location }
