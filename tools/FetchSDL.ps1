$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = '3.4.18'
$vendorDirectory = Join-Path $projectRoot 'vendor\SDL'
$downloadDirectory = Join-Path $projectRoot 'build\downloads'
New-Item -ItemType Directory -Force -Path $vendorDirectory,$downloadDirectory | Out-Null
$releaseUrl = "https://api.github.com/repos/libsdl-org/SDL/releases/tags/release-$version"
$release = Invoke-RestMethod -Uri $releaseUrl -Headers @{ 'User-Agent' = 'Rumble-build' } -TimeoutSec 30
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = @()
foreach ($architecture in @('x64', 'x86')) {
    $name = "SDL3-$version-win32-$architecture.zip"
    $asset = $release.assets | Where-Object { $_.name -eq $name }
    if (-not $asset) { throw "Official release asset missing: $name" }
    $archivePath = Join-Path $downloadDirectory $name
    $client = New-Object System.Net.WebClient
    try { $client.DownloadFile($asset.browser_download_url, $archivePath) } finally { $client.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($asset.digest -and $asset.digest -ne "sha256:$archiveHash") { throw "Release digest mismatch: $name" }
    $destination = Join-Path $vendorDirectory $architecture
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $dllPath = Join-Path $destination 'SDL3.dll'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $dllEntry = @($archive.Entries | Where-Object { $_.Name -eq 'SDL3.dll' })
        if ($dllEntry.Count -ne 1) { throw "Expected one SDL3.dll in $name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntry[0], $dllPath, $true)
    } finally { $archive.Dispose() }
    $files += [ordered]@{
        architecture = $architecture
        url = $asset.browser_download_url
        archiveSha256 = $archiveHash
        releaseDigest = $asset.digest
        dllSha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/libsdl-org/SDL/release-$version/LICENSE.txt" -OutFile (Join-Path $vendorDirectory 'LICENSE.txt') -TimeoutSec 30
[ordered]@{ name = 'SDL'; version = $version; release = $release.html_url; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $vendorDirectory 'origin.json') -Encoding UTF8
Write-Output "Downloaded and verified SDL $version (x64, x86)."
