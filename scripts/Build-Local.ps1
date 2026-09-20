# Sefirah Fast Local Incremental Build Script
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Release,
    [switch]$Run,
    [switch]$Install,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

if ($Release) {
    $Configuration = "Release"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "      Sefirah Local Incremental Build   " -ForegroundColor Cyan
Write-Host "      Configuration: $Configuration     " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Resolve .NET SDK path
$localDotnet = "$env:LOCALAPPDATA\Microsoft\dotnet"
if (Test-Path "$localDotnet\dotnet.exe") {
    $env:PATH = "$localDotnet;" + $env:PATH
}

$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Error ".NET SDK not found. Please install .NET 10 SDK or run dotnet-install.ps1."
    exit 1
}

$dotnetVersion = & dotnet --version
Write-Host "Using .NET SDK: $dotnetVersion ($($dotnetCmd.Source))" -ForegroundColor Gray

$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$projectPath = Join-Path $repoRoot "src\Sefirah\Sefirah.csproj"
$binDir = Join-Path $repoRoot "src\Sefirah\bin\x64\$Configuration\net10.0-windows10.0.26100\win-x64"
$exePath = Join-Path $binDir "Sefirah.exe"

# 2. Clean if requested
if ($Clean) {
    Write-Host "`nCleaning project..." -ForegroundColor Yellow
    & dotnet clean $projectPath -p:Platform=x64 -c $Configuration -f net10.0-windows10.0.26100
}

# 3. Build incrementally
Write-Host "`nBuilding Sefirah AI ($Configuration, x64, net10.0-windows10.0.26100)..." -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$buildArgs = @(
    "build", $projectPath,
    "-p:Platform=x64",
    "-c", $Configuration,
    "-f", "net10.0-windows10.0.26100",
    "--no-restore"
)

if ($Install) {
    $buildArgs += @(
        "-p:GenerateAppxPackageOnBuild=true",
        "-p:AppxPackageSigningEnabled=true",
        "-p:PackageCertificateThumbprint=52BF55D2A14FE1EB6686EA3FF2706181075482E8",
        "-p:UapAppxPackageBuildMode=Sideloading"
    )
}

& dotnet $buildArgs

$exitCode = $LASTEXITCODE
$sw.Stop()

if ($exitCode -ne 0) {
    Write-Host "`nBuild failed in $([math]::Round($sw.Elapsed.TotalSeconds, 2))s with exit code $exitCode" -ForegroundColor Red
    exit $exitCode
}

Write-Host "`nBUILD SUCCEEDED in $([math]::Round($sw.Elapsed.TotalSeconds, 2))s!" -ForegroundColor Green
Write-Host "Output: $exePath" -ForegroundColor Gray

# 4. Install / Register if requested
if ($Install) {
    $appPackagesDir = Join-Path $repoRoot "src\Sefirah\AppPackages"
    $msix = Get-ChildItem -Path $appPackagesDir -Include "*.msix", "*.msixbundle" -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($msix) {
        Get-Process Sefirah -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
        Write-Host "`nUpgrading Sefirah AI package in-place (preserving data)..." -ForegroundColor Cyan
        Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion
        Write-Host "Sefirah AI package installed successfully!" -ForegroundColor Green
    } else {
        Write-Warning "Could not find generated .msix or .msixbundle package in $appPackagesDir."
    }
}

# 5. Run if requested
if ($Run) {
    if (Test-Path $exePath) {
        Write-Host "`nLaunching Sefirah AI..." -ForegroundColor Cyan
        Start-Process -FilePath $exePath -WorkingDirectory $binDir
    } else {
        Write-Error "Executable not found at $exePath"
    }
}
