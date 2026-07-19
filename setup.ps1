#grabs ffmpeg + libmpv since theyre way too big to put on github
#run: powershell -ExecutionPolicy Bypass -File setup.ps1
param(
    [switch]$Force,
    [string]$ProjectDir = (Join-Path $PSScriptRoot "ClipMixer")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" #progress bar makes downloads like 10x slower lol
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip"
$sevenZipUrl = "https://www.7-zip.org/a/7zr.exe"

$tools = Join-Path $ProjectDir "tools"
$mpvDir = Join-Path $ProjectDir "mpv"
$tmp = Join-Path $env:TEMP ("clipmixer-setup-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $tools, $mpvDir, $tmp | Out-Null

try {
    #ffmpeg
    if ($Force -or -not (Test-Path (Join-Path $tools "ffmpeg.exe")) -or -not (Test-Path (Join-Path $tools "ffprobe.exe"))) {
        Write-Host "downloading ffmpeg..."
        $zip = Join-Path $tmp "ffmpeg.zip"
        Invoke-WebRequest $ffmpegUrl -OutFile $zip -UseBasicParsing
        Expand-Archive $zip -DestinationPath (Join-Path $tmp "ffmpeg")
        $bin = Get-ChildItem (Join-Path $tmp "ffmpeg") -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
        Copy-Item (Join-Path $bin.DirectoryName "ffmpeg.exe") $tools -Force
        Copy-Item (Join-Path $bin.DirectoryName "ffprobe.exe") $tools -Force
        Write-Host "ffmpeg done"
    } else {
        Write-Host "ffmpeg already there, skipping (use -Force to redownload)"
    }

    #libmpv, shinchiro deletes old builds so just get the newest one
    if ($Force -or -not (Test-Path (Join-Path $mpvDir "mpv-1.dll"))) {
        Write-Host "finding latest libmpv build..."
        $rel = Invoke-RestMethod "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest" -UseBasicParsing
        $asset = $rel.assets | Where-Object { $_.name -match '^mpv-dev-x86_64-\d+-git-.+\.7z$' } | Select-Object -First 1
        if (-not $asset) { throw "couldnt find a mpv-dev-x86_64 build in $($rel.tag_name)" }

        Write-Host "downloading $($asset.name)..."
        $archive = Join-Path $tmp $asset.name
        Invoke-WebRequest $asset.browser_download_url -OutFile $archive -UseBasicParsing
        $7zr = Join-Path $tmp "7zr.exe"
        Invoke-WebRequest $sevenZipUrl -OutFile $7zr -UseBasicParsing #windows cant always open .7z on its own
        & $7zr x $archive ("-o" + (Join-Path $tmp "mpv")) -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7zr failed to extract $($asset.name)" }

        #newer builds call it libmpv-2.dll, app loads mpv-1.dll
        $dll = Get-ChildItem (Join-Path $tmp "mpv") -Recurse -Include "libmpv-2.dll", "mpv-2.dll", "mpv-1.dll" | Select-Object -First 1
        if (-not $dll) { throw "no libmpv dll in $($asset.name)" }
        Copy-Item $dll.FullName (Join-Path $mpvDir "mpv-1.dll") -Force
        Write-Host "libmpv done ($($rel.tag_name))"
    } else {
        Write-Host "libmpv already there, skipping (use -Force to redownload)"
    }

    Write-Host "all set, build ClipMixer.sln and run it"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
