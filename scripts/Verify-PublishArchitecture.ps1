[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "win-arm64")]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string] $PublishDirectory
)

$expectedFiles = @(
    "PromptSaver.Desktop.exe",
    "e_sqlite3.dll"
)

$expectedMachine = switch ($RuntimeIdentifier) {
    "win-x64" { 0x8664 }
    "win-arm64" { 0xAA64 }
}

foreach ($expectedFile in $expectedFiles) {
    $path = Join-Path $PublishDirectory $expectedFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected $RuntimeIdentifier publish file was not found: $path"
    }

    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "File does not have a valid DOS header: $path"
        }

        $stream.Position = 0x3C
        $peHeaderOffset = $reader.ReadInt32()
        $stream.Position = $peHeaderOffset

        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "File does not have a valid PE header: $path"
        }

        $actualMachine = $reader.ReadUInt16()
        if ($actualMachine -ne $expectedMachine) {
            throw (
                "Architecture mismatch for {0}: expected 0x{1:X4}, found 0x{2:X4}." -f
                $path, $expectedMachine, $actualMachine
            )
        }
    }
    finally {
        $stream.Dispose()
    }
}

Write-Host "Verified PE architecture for required $RuntimeIdentifier files in $PublishDirectory."
