param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release",
    [string]$Version = "",
    [ValidateSet("stable", "beta")][string]$Channel = "stable",
    [ValidateRange(1, 100)][int]$Rollout = 100,
    [bool]$TrimLauncher = $true,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
if (Test-Path Variable:\PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repo = "RealBibus/Aerisflow"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $root "Aerisflow\Aerisflow.csproj"
$launcherProject = Join-Path $root "Aerisflow\Launcher\Launcher\Launcher.csproj"
$installerProject = Join-Path $root "Installer\Aerisflow.Installer\Aerisflow.Installer.csproj"

$build = Join-Path $root "build"
$payload = Join-Path $build "payload"
$dist = Join-Path $root "build\dist"
$resources = Join-Path $root "Installer\Aerisflow.Installer\Resources"
$payloadZip = Join-Path $resources "payload.zip"
$signProject = Join-Path $root "Aerisflow\Tools\Aerisflow.Sign\Aerisflow.Sign.csproj"
$privateKey = Join-Path $root "Aerisflow\Keys\private.pem"
$publicKey = Join-Path $root "Aerisflow\Keys\public.txt"

if (-not (Test-Path $appProject)) { throw "app csproj not found: $appProject" }
if (-not (Test-Path $launcherProject)) { throw "launcher csproj not found: $launcherProject" }
if (-not (Test-Path $installerProject)) { throw "installer csproj not found: $installerProject" }
if (-not (Test-Path $signProject)) { throw "sign tool not found: $signProject" }
if (-not (Test-Path $privateKey)) { throw "Update signing key not found: $privateKey" }

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& dotnet msbuild $appProject -getProperty:Version -nologo | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0) { throw "Could not read <Version> from $appProject." }
}
$Version = "$Version".Trim().TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version is empty. Set <Version> in $appProject or pass -Version 1.1.3."
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version '$Version' must be numeric like 1.1.3."
}

$appFolder = "app-$Version"
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
$trimLauncher = $TrimLauncher -and (-not $FrameworkDependent)

Write-Host "Aerisflow installer build" -ForegroundColor Cyan
Write-Host "  configuration : $Configuration"
Write-Host "  version       : $Version"
Write-Host "  channel       : $Channel"
Write-Host "  rollout       : $Rollout%"
Write-Host "  runtime       : win-x64 ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))"
Write-Host "  launcher trim : $trimLauncher"
Write-Host "  changelog     : GitHub release body"

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $payload | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null
New-Item -ItemType Directory -Force -Path $resources | Out-Null

$commonArgs = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", $selfContained,
    "--nologo",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:SatelliteResourceLanguages=en",
    "-p:Version=$Version",
    "-p:FileVersion=$Version",
    "-p:InformationalVersion=$Version"
)

$appOutput = Join-Path $payload $appFolder

Write-Host "`n[1/6] Publishing Aerisflow -> $appOutput" -ForegroundColor Yellow
& dotnet publish $appProject @commonArgs -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "Aerisflow publish failed." }
if (-not (Test-Path (Join-Path $appOutput "Aerisflow.exe"))) {
    throw "Aerisflow.exe missing after publish. Check $appProject and its OutputType/AssemblyName."
}

foreach ($stray in @("healthy.ok", "staged.ok", "settings.json", "device.json", "current.json", "CHANGELOG.txt")) {
    $path = Join-Path $appOutput $stray
    if (Test-Path $path) { Remove-Item $path -Force }
}
Get-ChildItem $appOutput -Recurse -File -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "`n[2/6] Publishing launcher -> $payload" -ForegroundColor Yellow
$launcherArgs = $commonArgs
if ($trimLauncher) {
    $launcherArgs = $commonArgs + @(
        "-p:PublishTrimmed=true",
        "-p:TrimMode=full",
        "-p:InvariantGlobalization=true"
    )
}
& dotnet publish $launcherProject @launcherArgs -o $payload
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }
if (-not (Test-Path (Join-Path $payload "Launcher.exe"))) {
    throw "Launcher.exe missing after publish. Check $launcherProject."
}

$currentJson = [ordered]@{
    version  = $Version
    dir      = $appFolder
    previous = $null
} | ConvertTo-Json -Compress
Set-Content -Path (Join-Path $payload "current.json") -Value $currentJson -Encoding UTF8

Write-Host "`n[3/6] Packing installer payload..." -ForegroundColor Yellow
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $payload, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
if (-not (Test-Path $payloadZip)) { throw "payload.zip was not created." }

Write-Host "`n[4/6] Building AerisflowSetup.exe -> $dist" -ForegroundColor Yellow
& dotnet publish $installerProject @commonArgs -o $dist
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }

$installerExe = Join-Path $dist "Aerisflow.Installer.exe"
if (-not (Test-Path $installerExe)) { throw "Aerisflow.Installer.exe missing in $dist" }
$setup = Join-Path $dist "AerisflowSetup.exe"
Copy-Item $installerExe $setup -Force

$readme = @"
Aerisflow Setup $Version

Run AerisflowSetup.exe to install Aerisflow for the current Windows user.
No administrator rights are required.

The setup creates Start menu and desktop shortcuts, registers the app in
Windows Settings > Apps and can enable "Start with Windows".
Release notes are loaded live from the GitHub release inside the setup window.
"@
Set-Content -Path (Join-Path $dist "README.txt") -Value $readme -Encoding UTF8
$size = [Math]::Round((Get-Item $setup).Length / 1MB, 1)

Write-Host "`n[5/6] Creating signed update release..." -ForegroundColor Yellow
$release = Join-Path $build "release"
$releaseDir = Join-Path $release $Version
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$emptyNotes = Join-Path $release "manifest-notes.txt"
Set-Content -Path $emptyNotes -Value "" -Encoding UTF8 -NoNewline

& dotnet build $signProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Sign tool build failed." }

$manifestPath = Join-Path $releaseDir "manifest.json"
& dotnet run --project $signProject -c $Configuration --no-build -- manifest $appOutput $Version $Channel $Rollout $emptyNotes $manifestPath
if ($LASTEXITCODE -ne 0) { throw "Manifest generation failed." }

$signaturePath = Join-Path $releaseDir "manifest.sig"
& dotnet run --project $signProject -c $Configuration --no-build -- sign $manifestPath $privateKey $signaturePath
if ($LASTEXITCODE -ne 0) { throw "Manifest signing failed." }

& dotnet run --project $signProject -c $Configuration --no-build -- verify $manifestPath $signaturePath $publicKey
if ($LASTEXITCODE -ne 0) { throw "Manifest signature check failed." }

$assetNames = @()
foreach ($file in Get-ChildItem $appOutput -Recurse -File) {
    $relative = $file.FullName.Substring($appOutput.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relative -match '(^|/)(healthy\.ok|staged\.ok|settings\.json|device\.json|current\.json)$') { continue }
    if ($relative.EndsWith('.pdb', [System.StringComparison]::OrdinalIgnoreCase)) { continue }

    $asset = $relative.Replace('/', '_')
    Copy-Item $file.FullName (Join-Path $releaseDir $asset) -Force
    $assetNames += $asset
}
Copy-Item $setup (Join-Path $releaseDir "AerisflowSetup.exe") -Force

$uploadList = @("AerisflowSetup.exe", "manifest.json", "manifest.sig") + $assetNames
$uploadText = @"
Aerisflow $Version - GitHub release assets
Repository: $repo
Tag: v$Version
Channel: $Channel
Rollout: $Rollout%

Upload every file from this folder to the release:
$($uploadList | ForEach-Object { "  - $_" } | Out-String)
The release body on GitHub is the changelog. Both the setup window and the
in-app updater read it from there; no local changelog is shipped.
"@
Set-Content -Path (Join-Path $releaseDir "UPLOAD.txt") -Value $uploadText -Encoding UTF8

Write-Host "`n[6/6] Publishing to GitHub..." -ForegroundColor Yellow
if (-not $Publish) {
    Write-Host "  skipped (pass -Publish to create the release and upload assets)"
}
else {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        Write-Host "  GitHub CLI not found - upload $releaseDir manually." -ForegroundColor Yellow
    }
    else {
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"

        $tag = "v$Version"
        $notesFile = Join-Path $release "release-notes.md"
        $lines = @()

        & git rev-parse --is-inside-work-tree 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $lastTag = (& git describe --tags --abbrev=0 2>$null)
            if ($LASTEXITCODE -eq 0 -and $lastTag) {
                $lines = & git log "$lastTag..HEAD" --no-merges --pretty=format:"- %s"
            }
            else {
                $lines = & git log -40 --no-merges --pretty=format:"- %s"
            }
        }
        if (-not $lines -or $lines.Count -eq 0) { $lines = @("- Release $Version") }

        $body = "## Aerisflow $Version`n`n" + ($lines -join "`n")
        Set-Content -Path $notesFile -Value $body -Encoding UTF8

        $files = Get-ChildItem $releaseDir -File |
        Where-Object { $_.Name -ne "UPLOAD.txt" } |
        ForEach-Object { $_.FullName }

        & gh release view $tag --repo $repo 2>$null | Out-Null
        $exists = ($LASTEXITCODE -eq 0)
        $ErrorActionPreference = $previousEap

        if ($exists) {
            Write-Host "  release $tag exists - updating notes and assets"
            & gh release edit $tag --repo $repo --title "Aerisflow $Version" --notes-file $notesFile
            if ($LASTEXITCODE -ne 0) { throw "gh release edit failed." }
            & gh release upload $tag $files --repo $repo --clobber
            if ($LASTEXITCODE -ne 0) { throw "gh release upload failed." }
        }
        else {
            Write-Host "  creating release $tag"
            $ghArgs = @(
                "release", "create", $tag,
                "--repo", $repo,
                "--title", "Aerisflow $Version",
                "--notes-file", $notesFile
            )
            if ($Channel -eq "beta") { $ghArgs += "--prerelease" }
            $ghArgs += $files
            & gh @ghArgs
            if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
        }
        Write-Host "  published: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
    }
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Installer : $setup"
Write-Host "  Version   : $Version"
Write-Host "  Channel   : $Channel"
Write-Host "  Size      : $size MB"
Write-Host "  Release   : $releaseDir"