[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,

    [Parameter(Mandatory)]
    [string] $OutputFile
)

$ErrorActionPreference = "Stop"
$publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory)
$outputPath = [System.IO.Path]::GetFullPath($OutputFile)

if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Publish directory does not exist: $publishRoot"
}

$files = @(
    Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
        Where-Object FullName -ne $outputPath |
        Sort-Object { [System.IO.Path]::GetRelativePath($publishRoot, $_.FullName).Replace("\", "/") }
)

if ($files.Count -eq 0) {
    throw "Publish directory contains no files: $publishRoot"
}

function Get-StableGuid {
    param([Parameter(Mandatory)][string] $Value)

    $namespace = [Guid]::Parse("6E2B5F18-1FC4-4FEC-9278-B4676F559E19").ToByteArray()
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

function Get-StableId {
    param(
        [Parameter(Mandatory)][string] $Prefix,
        [Parameter(Mandatory)][string] $Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    return "${Prefix}_$($hash.Substring(0, 24))"
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.IndentChars = "  "
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)

$parent = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporaryPath = "$outputPath.tmp"
$writer = [System.Xml.XmlWriter]::Create($temporaryPath, $settings)

try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")
    $writer.WriteStartElement("Fragment")
    $writer.WriteStartElement("DirectoryRef")
    $writer.WriteAttributeString("Id", "INSTALLFOLDER")

    $componentIds = [System.Collections.Generic.List[string]]::new()
    $rootNode = @{
        Children = @{}
        Files = [System.Collections.Generic.List[object]]::new()
        Path = ""
    }

    foreach ($file in $files) {
        $relativePath = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace("\", "/")
        $segments = $relativePath.Split("/")
        $node = $rootNode
        for ($index = 0; $index -lt $segments.Length - 1; $index++) {
            $segment = $segments[$index]
            if (-not $node.Children.ContainsKey($segment)) {
                $childPath = if ([string]::IsNullOrEmpty($node.Path)) { $segment } else { "$($node.Path)/$segment" }
                $node.Children[$segment] = @{
                    Children = @{}
                    Files = [System.Collections.Generic.List[object]]::new()
                    Path = $childPath
                }
            }

            $node = $node.Children[$segment]
        }

        $node.Files.Add($file)
    }

    function Write-Node {
        param([Parameter(Mandatory)][hashtable] $Node)

        foreach ($file in $Node.Files | Sort-Object Name) {
            $relativePath = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace("\", "/")
            $componentId = Get-StableId -Prefix "CMP" -Value $relativePath
            $fileId = Get-StableId -Prefix "FIL" -Value $relativePath
            $writer.WriteStartElement("Component")
            $writer.WriteAttributeString("Id", $componentId)
            $writer.WriteAttributeString("Guid", (Get-StableGuid -Value "component/$relativePath"))
            $writer.WriteAttributeString("Bitness", "always64")
            $writer.WriteStartElement("File")
            $writer.WriteAttributeString("Id", $fileId)
            $writer.WriteAttributeString("Source", $file.FullName)
            $writer.WriteAttributeString("Name", $file.Name)
            $writer.WriteAttributeString("KeyPath", "yes")
            $writer.WriteEndElement()
            $writer.WriteEndElement()
            $componentIds.Add($componentId)
        }

        foreach ($entry in $Node.Children.GetEnumerator() | Sort-Object Key) {
            $child = $entry.Value
            $writer.WriteStartElement("Directory")
            $writer.WriteAttributeString("Id", (Get-StableId -Prefix "DIR" -Value $child.Path))
            $writer.WriteAttributeString("Name", $entry.Key)
            Write-Node -Node $child
            $writer.WriteEndElement()
        }
    }

    Write-Node -Node $rootNode

    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteStartElement("Fragment")
    $writer.WriteStartElement("ComponentGroup")
    $writer.WriteAttributeString("Id", "PublishedFiles")
    foreach ($componentId in $componentIds | Sort-Object) {
        $writer.WriteStartElement("ComponentRef")
        $writer.WriteAttributeString("Id", $componentId)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
Write-Host "Generated deterministic WiX source for $($files.Count) files: $outputPath"
