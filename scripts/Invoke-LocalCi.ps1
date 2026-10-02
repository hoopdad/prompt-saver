[CmdletBinding()]
param(
    [switch] $IncludePackages,

    [ValidateSet("all", "win-x64", "win-arm64")]
    [string] $RuntimeIdentifier = "all",

    [string] $Version
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solution = Join-Path $repositoryRoot "PromptSaver.sln"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Command,

        [Parameter(Mandatory)]
        [string] $FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

Push-Location $repositoryRoot
try {
    Invoke-Checked {
        dotnet restore $solution --locked-mode
    } "Locked restore failed."
    Invoke-Checked {
        dotnet format $solution --verify-no-changes --no-restore
    } "Formatting verification failed."
    Invoke-Checked {
        dotnet build $solution -c Release --no-restore -p:TreatWarningsAsErrors=true
    } "Release build failed."

    $testProjects = @(
        "tests\PromptSaver.Domain.Tests\PromptSaver.Domain.Tests.csproj",
        "tests\PromptSaver.Application.Tests\PromptSaver.Application.Tests.csproj",
        "tests\PromptSaver.Infrastructure.Tests\PromptSaver.Infrastructure.Tests.csproj",
        "tests\PromptSaver.Desktop.Tests\PromptSaver.Desktop.Tests.csproj"
    )
    foreach ($project in $testProjects) {
        Invoke-Checked {
            dotnet test --project $project -c Release --no-build
        } "Tests failed for $project."
    }

    if ($IncludePackages) {
        if ([string]::IsNullOrWhiteSpace($Version)) {
            [xml] $desktopProject = Get-Content `
                (Join-Path $repositoryRoot "src\PromptSaver.Desktop\PromptSaver.Desktop.csproj")
            $Version = [string]$desktopProject.Project.PropertyGroup.VersionPrefix
        }

        & "$PSScriptRoot\Build-WindowsPackages.ps1" `
            -RuntimeIdentifier $RuntimeIdentifier `
            -Version $Version
        if ($LASTEXITCODE -ne 0) {
            throw "Windows package validation failed."
        }
    }
}
finally {
    Pop-Location
}
