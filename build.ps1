<# Modified by 360Fabriek for the patched Cesium for Unity redistribution.
   See NOTICE and MODIFICATIONS.md. #>

[CmdletBinding()]
param(
    [string] $UnityExecutable,
    [string] $UnityVersion,
    [string] $UnityBasePath,
    [string[]] $Platform,
    [switch] $IncludeUwp,
    [string] $ArtifactSuffix
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $PSCommandPath

if (-not $UnityExecutable) {
    if ($IsWindows) {
        $UnityExecutable = "C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Unity.exe"
    } else {
        $UnityExecutable = "/Volumes/Expansion/Unity/Hub/Editor/6000.3.13f1/Unity.app/Contents/MacOS/Unity"
    }
}

if (-not $env:EZVCPKG_BASEDIR) {
    $env:EZVCPKG_BASEDIR = if ($IsWindows) { "C:\BuildCaches\ezvcpkg" } else { "/Volumes/Expansion/BuildCaches/ezvcpkg" }
}

if (-not $env:VCPKG_DEFAULT_BINARY_CACHE) {
    $env:VCPKG_DEFAULT_BINARY_CACHE = if ($IsWindows) { "C:\BuildCaches\vcpkg-archives" } else { "/Volumes/Expansion/BuildCaches/vcpkg-archives" }
}

if (-not $env:DOTNET_ROLL_FORWARD) {
    $env:DOTNET_ROLL_FORWARD = "Major"
}

if ($Platform -and $Platform.Count -gt 0) {
    $Platforms = @($Platform)
} elseif ($IncludeUwp) {
    $Platforms = @("Editor", "Android", "iOS", "Linux", "macOS", "UWP", "Web", "Windows")
} else {
    $Platforms = @("Editor", "Android", "iOS", "Linux", "macOS", "Web", "Windows")
}

$PackageArgs = @("package")

if ($UnityVersion) {
    $PackageArgs += @("--unity-version", $UnityVersion)
} else {
    if (-not (Test-Path -LiteralPath $UnityExecutable)) {
        throw "Unity executable not found: $UnityExecutable"
    }
    $PackageArgs += @("--unity-executable", $UnityExecutable)
}

if ($UnityBasePath) {
    $PackageArgs += @("--unity-base-path", $UnityBasePath)
}

foreach ($TargetPlatform in $Platforms) {
    $PackageArgs += @("--platform", $TargetPlatform)
}

Push-Location $ScriptDir
try {
    Write-Host "Using package root: $ScriptDir"
    Write-Host "Using EZVCPKG_BASEDIR: $env:EZVCPKG_BASEDIR"
    Write-Host "Using VCPKG_DEFAULT_BINARY_CACHE: $env:VCPKG_DEFAULT_BINARY_CACHE"
    Write-Host "Building platforms: $($Platforms -join ' ')"

    dotnet run --project Build~ -- @PackageArgs

    if ($ArtifactSuffix) {
        $PackageJson = Get-Content -LiteralPath (Join-Path $ScriptDir "package.json") -Raw | ConvertFrom-Json
        $ProjectRoot = Resolve-Path -LiteralPath (Join-Path $ScriptDir ".." "..")
        $SourcePackage = Join-Path $ProjectRoot "com.cesium.unity-$($PackageJson.version).tgz"
        $PatchedPackage = Join-Path $ProjectRoot "com.cesium.unity-$($PackageJson.version)$ArtifactSuffix.tgz"

        if (-not (Test-Path -LiteralPath $SourcePackage)) {
            throw "Expected package not found: $SourcePackage"
        }

        Copy-Item -LiteralPath $SourcePackage -Destination $PatchedPackage -Force
        Write-Host "Copied patched package: $PatchedPackage"
    }
} finally {
    Pop-Location
}
