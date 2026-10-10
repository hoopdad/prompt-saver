[CmdletBinding()]
param(
    [ValidateSet("all", "win-x64", "win-arm64")]
    [string] $RuntimeIdentifier = "all",

    [string] $Version,

    [switch] $SkipPublish,

    [switch] $SkipPackageTests
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$rids = if ($RuntimeIdentifier -eq "all") { @("win-x64", "win-arm64") } else { @($RuntimeIdentifier) }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $desktopProject = Get-Content `
        (Join-Path $repositoryRoot "src\PromptSaver.Desktop\PromptSaver.Desktop.csproj")
    $Version = [string]$desktopProject.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be three-part SemVer for MSI: $Version"
}
$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Version exceeds Windows Installer limits (255.255.65535): $Version"
}

function Reset-OutputDirectory {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    $parent = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $Path
    $retiredPath = Join-Path $parent "$leaf.retired.$([Guid]::NewGuid().ToString('N'))"
    try {
        Move-Item -LiteralPath $Path -Destination $retiredPath -ErrorAction Stop
    }
    catch {
        Write-Warning "Output is in use and cannot be replaced: $Path"
        return $false
    }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $retiredPath -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            if ($attempt -eq 5) {
                break
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }

    Write-Warning "Retired output remains locked and could not be removed: $retiredPath"
    return $true
}

function Get-StableGuid {
    param([Parameter(Mandatory)][string] $Value)

    $namespace = [Guid]::Parse("29C12737-D7C8-4C3F-95EA-78D6F35189F3").ToByteArray()
    [Array]::Reverse($namespace, 0, 4)
    [Array]::Reverse($namespace, 4, 2)
    [Array]::Reverse($namespace, 6, 2)
    $valueBytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $data = [byte[]]::new($namespace.Length + $valueBytes.Length)
    [Array]::Copy($namespace, 0, $data, 0, $namespace.Length)
    [Array]::Copy($valueBytes, 0, $data, $namespace.Length, $valueBytes.Length)
    $hash = [System.Security.Cryptography.SHA256]::HashData($data)
    $guidBytes = [byte[]]$hash[0..15]
    $guidBytes[6] = ($guidBytes[6] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    [Array]::Reverse($guidBytes, 0, 4)
    [Array]::Reverse($guidBytes, 4, 2)
    [Array]::Reverse($guidBytes, 6, 2)
    return [Guid]::new($guidBytes).ToString().ToUpperInvariant()
}

$env:PROMPTSAVER_PACKAGE_VERSION = $Version

dotnet restore "$repositoryRoot\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj" --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Locked application restore failed."
}

dotnet restore "$repositoryRoot\packaging\wix\PromptSaver.Installer\PromptSaver.Installer.wixproj" --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Locked installer restore failed."
}

foreach ($rid in $rids) {
    $platform = if ($rid -eq "win-x64") { "x64" } else { "arm64" }
    $productCode = Get-StableGuid -Value "PromptSaver/$Version/$rid"
    $publishDirectory = Join-Path $artifactsRoot "publish\$rid"
    $versionedPublishDirectory = Join-Path $artifactsRoot "publish\$rid-$Version"
    if ($SkipPublish -and (Test-Path -LiteralPath $versionedPublishDirectory -PathType Container)) {
        $publishDirectory = $versionedPublishDirectory
    }
    $installerDirectory = Join-Path $artifactsRoot "installers\$rid"

    if (-not $SkipPublish) {
        if (-not (Reset-OutputDirectory -Path $publishDirectory)) {
            $publishDirectory = $versionedPublishDirectory
            [void](Reset-OutputDirectory -Path $publishDirectory)
        }

        dotnet publish "$repositoryRoot\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj" `
            -c Release `
            -r $rid `
            --self-contained true `
            --no-restore `
            -p:PublishProfile=$rid `
            -p:Version=$Version `
            -p:ContinuousIntegrationBuild=true `
            -o $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $rid."
        }
    }

    $publishOverrideName = "PROMPTSAVER_PUBLISH_$($rid.Replace('-', '_').ToUpperInvariant())"
    [Environment]::SetEnvironmentVariable($publishOverrideName, $publishDirectory, "Process")

    & "$PSScriptRoot\Verify-PublishArchitecture.ps1" `
        -RuntimeIdentifier $rid `
        -PublishDirectory $publishDirectory

    [void](Reset-OutputDirectory -Path $installerDirectory)

    dotnet build "$repositoryRoot\packaging\wix\PromptSaver.Installer\PromptSaver.Installer.wixproj" `
        -c Release `
        --no-restore `
        -p:InstallerPlatform=$platform `
        -p:ProductVersion=$Version `
        -p:ProductCode=$productCode `
        -p:PublishDir=$publishDirectory `
        -p:OutputPath="$installerDirectory\"
    if ($LASTEXITCODE -ne 0) {
        throw "WiX build failed for $rid."
    }
}

& "$PSScriptRoot\New-WindowsPackageManifest.ps1" -ArtifactsDirectory $artifactsRoot -Version $Version

if (-not $SkipPackageTests) {
    $env:PROMPTSAVER_ARTIFACTS = $artifactsRoot
    dotnet test --project "$repositoryRoot\tests\PromptSaver.Package.Tests\PromptSaver.Package.Tests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Package tests failed."
    }
}
