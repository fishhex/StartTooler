# Build StartTooler upload-relay Go binary for Linux (amd64 + arm64).
#
# Windows version of build-relay.sh. Cross-compiles from Windows Go toolchain.
# Idempotent: only rebuilds when .go sources are newer than binaries,
# or when binaries don't exist.

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$SrcDir = Join-Path $RepoRoot "tools\upload-relay"
$OutDir = Join-Path $RepoRoot "StartTooler\Resources\relay-binaries"

if (-not (Get-Command "go" -ErrorAction SilentlyContinue)) {
    Write-Host "[build-relay] go not found in PATH; skipping"
    exit 0
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Sync HTML: single source of truth = Resources/upload.html
$canonicalHtml = Join-Path $RepoRoot "StartTooler\Resources\upload.html"
$embedHtml = Join-Path $SrcDir "web\index.html"
if (Test-Path $canonicalHtml) {
    if (-not (Test-Path $embedHtml) -or ((Get-Item $canonicalHtml).LastWriteTimeUtc -gt (Get-Item $embedHtml).LastWriteTimeUtc)) {
        New-Item -ItemType Directory -Force -Path (Join-Path $SrcDir "web") | Out-Null
        Copy-Item -Force $canonicalHtml $embedHtml
        Write-Host "[build-relay] synced upload.html -> tools\upload-relay\web\index.html"
    }
}

$targets = @("amd64", "arm64")

# Compute newest source mtime
$goMtime = 0
if (Test-Path $SrcDir) {
    $goMtime = (Get-ChildItem -Path $SrcDir -Recurse -Include "*.go","go.mod" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.LastWriteTimeUtc.ToFileTime() } |
        Measure-Object -Maximum).Maximum
    if ($null -eq $goMtime) { $goMtime = 0 }
}
$htmlPath = Join-Path $SrcDir "web\index.html"
$htmlMtime = (Test-Path $htmlPath) ? (Get-Item $htmlPath).LastWriteTimeUtc.ToFileTime() : 0
if ($htmlMtime -gt $goMtime) { $goMtime = $htmlMtime }

function Needs-Rebuild {
    param([string]$bin)
    if (-not (Test-Path $bin)) { return $true }
    $binMtime = (Get-Item $bin).LastWriteTimeUtc.ToFileTime()
    return $binMtime -lt $goMtime
}

$rebuild = $false
foreach ($arch in $targets) {
    $bin = Join-Path $OutDir "upload-relay-linux-$arch.exe"
    if (Needs-Rebuild $bin) { $rebuild = $true; break }
}

if (-not $rebuild) {
    Write-Host "[build-relay] all binaries up-to-date"
    exit 0
}

Push-Location $SrcDir
try {
    foreach ($arch in $targets) {
        $bin = Join-Path $OutDir "upload-relay-linux-$arch.exe"
        if (Needs-Rebuild $bin) {
            Write-Host "[build-relay] building windows/$arch -> $bin"
            $env:GOOS = "linux"
            $env:GOARCH = $arch
            $env:CGO_ENABLED = "0"
            go build -trimpath -ldflags="-s -w" -o $bin .
        }
    }
} finally {
    Pop-Location
}

Write-Host "[build-relay] done"
