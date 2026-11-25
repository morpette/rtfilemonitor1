param([string]$Dotnet = "dotnet")

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot
$package = Join-Path $workspace "dist"
$staging = Join-Path $workspace (".build/publish-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $package, $staging -Force | Out-Null

$archive = "$staging.zip"
$lockPath = Join-Path $workspace ".build/publish.lock"
$publishLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $nextVersion = 3
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Filter "RtFileMonitor-win-x64-v*.zip") {
        if ($file.Name -match '^RtFileMonitor-win-x64-v([0-9]+)\.zip$') {
            $nextVersion = [Math]::Max($nextVersion, [int]$Matches[1] + 1)
        }
    }

    & $Dotnet publish (Join-Path $workspace "RtFileMonitor.csproj") -c Release -r win-x64 --self-contained true `
        "-p:PackageVersionLabel=v$nextVersion" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $staging
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath (Join-Path $workspace "LICENSE") -Destination $staging
    $assets = Get-Content -Raw -LiteralPath (Join-Path $workspace "obj/project.assets.json") | ConvertFrom-Json
    $framework = $assets.project.frameworks.PSObject.Properties.Value
    $runtimePacks = $framework.downloadDependencies | Where-Object {
        $_.name -in "Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64"
    }
    foreach ($dependency in $runtimePacks) {
        $version = ($dependency.version -split '[\[\], ]+' | Where-Object { $_ })[0]
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $runtimePack = Join-Path $folder ($dependency.name.ToLowerInvariant() + "/" + $version)
            if (Test-Path -LiteralPath $runtimePack) {
                $notices = New-Item -ItemType Directory -Path (Join-Path $staging $dependency.name)
                Get-ChildItem -LiteralPath $runtimePack -File |
                    Where-Object Name -In "LICENSE", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT" |
                    Copy-Item -Destination $notices.FullName
                break
            }
        }
    }

    $destination = Join-Path $package "RtFileMonitor-win-x64-v$nextVersion.zip"
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $archive
    [IO.File]::Move($archive, $destination)
    Write-Output $destination
}
finally {
    $publishLock.Dispose()
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive
    }
}
