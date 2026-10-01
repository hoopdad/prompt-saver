[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactsDirectory,

    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$manifestDirectory = Join-Path $root "manifests"
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null

$packageFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $root "installers") -Filter "*.msi" -File -Recurse |
        Sort-Object FullName
)
if ($packageFiles.Count -eq 0) {
    throw "No MSI packages were found under $root."
}

$checksumLines = foreach ($file in $packageFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllLines(
    (Join-Path $manifestDirectory "SHA256SUMS.txt"),
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

foreach ($rid in @("win-x64", "win-arm64")) {
    $overrideName = "PROMPTSAVER_PUBLISH_$($rid.Replace('-', '_').ToUpperInvariant())"
    $publishDirectory = [Environment]::GetEnvironmentVariable($overrideName, "Process")
    if ([string]::IsNullOrWhiteSpace($publishDirectory)) {
        $publishDirectory = Join-Path $root "publish\$rid"
    }
    if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) {
        continue
    }

    $files = @(
        Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
            Sort-Object { [System.IO.Path]::GetRelativePath($publishDirectory, $_.FullName) } |
            ForEach-Object {
                $relativePath = [System.IO.Path]::GetRelativePath($publishDirectory, $_.FullName).Replace("\", "/")
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                [ordered]@{
                    SPDXID = "SPDXRef-File-$([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($relativePath))).Substring(0, 16))"
                    fileName = $relativePath
                    checksums = @(@{ algorithm = "SHA256"; checksumValue = $hash })
                }
            }
    )

    $document = [ordered]@{
        spdxVersion = "SPDX-2.3"
        dataLicense = "CC0-1.0"
        SPDXID = "SPDXRef-DOCUMENT"
        name = "PromptSaver-$Version-$rid"
        documentNamespace = "https://github.com/hoopdad/prompt-saver/sbom/$Version/$rid"
        creationInfo = @{
            created = "1970-01-01T00:00:00Z"
            creators = @("Tool: scripts/New-WindowsPackageManifest.ps1")
        }
        packages = @(
            @{
                SPDXID = "SPDXRef-Package-PromptSaver"
                name = "Prompt Saver"
                versionInfo = $Version
                downloadLocation = "NOASSERTION"
                filesAnalyzed = $true
                licenseConcluded = "MIT"
                licenseDeclared = "MIT"
                copyrightText = "Copyright (c) 2026 hoopdad"
            }
        )
        files = $files
        relationships = @(
            @{
                spdxElementId = "SPDXRef-DOCUMENT"
                relationshipType = "DESCRIBES"
                relatedSpdxElement = "SPDXRef-Package-PromptSaver"
            }
        ) + @(
            $files | ForEach-Object {
                @{
                    spdxElementId = "SPDXRef-Package-PromptSaver"
                    relationshipType = "CONTAINS"
                    relatedSpdxElement = $_.SPDXID
                }
            }
        )
    }

    $json = $document | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        (Join-Path $manifestDirectory "PromptSaver-$Version-$rid.spdx.json"),
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Generated SHA-256 checksums and SPDX manifests in $manifestDirectory."
