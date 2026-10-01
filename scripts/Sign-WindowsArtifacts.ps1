[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path,

    [Parameter(Mandatory)]
    [string] $CertificatePath,

    [Parameter(Mandatory)]
    [string] $CertificatePassword,

    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$signTool = Get-Command signtool.exe -ErrorAction Stop
$files = @(
    Get-ChildItem -LiteralPath $Path -File -Recurse |
        Where-Object Extension -In @(".exe", ".dll", ".msi") |
        Sort-Object FullName
)

foreach ($file in $files) {
    & $signTool.Source sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $CertificatePath /p $CertificatePassword $file.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $($file.FullName)."
    }

    & $signTool.Source verify /pa /all $file.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $($file.FullName)."
    }
}
