# Lists the Microsoft components (and their versions) that "dotnet publish" bundled into SmartMacro.exe,
# then checks that release-docs\THIRD-PARTY-NOTICES.txt mentions every one of them.
# Called by tools\publish.cmd after a successful publish.
#
# Exit codes: 0 = notices match the build, 1 = deps.json missing / unreadable, 2 = notices are out of date.
param(
    [Parameter(Mandatory = $true)][string]$DepsJson,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Notices = ""
)

if (-not (Test-Path $DepsJson)) {
    "Could not find $DepsJson - bundle versions were not listed." | Set-Content -Path $Out -Encoding UTF8
    exit 1
}

$deps = Get-Content -Path $DepsJson -Raw | ConvertFrom-Json
$seen = @{}
$found = @()   # one entry per bundled third-party file: pack, file name, version
$lines = @("Bundled components, read from $DepsJson on $(Get-Date -Format 'yyyy-MM-dd HH:mm')", "")

foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($library in $target.Value.PSObject.Properties) {
        # Runtime packs (.NET, WPF, Windows SDK projection) are the only third-party parts SmartMacro ships.
        if ($library.Name -notlike 'runtimepack.*') { continue }
        if ($seen.ContainsKey($library.Name)) { continue }
        $seen[$library.Name] = $true
        $lines += $library.Name
        foreach ($kind in 'native', 'runtime') {
            $files = $library.Value.$kind
            if ($null -eq $files) { continue }
            foreach ($file in $files.PSObject.Properties) {
                # Only list native files and the SDK projection DLLs; the managed runtime has hundreds of files.
                if ($kind -eq 'runtime' -and $library.Name -notlike '*Windows.SDK.NET*') { continue }
                $version = $file.Value.fileVersion
                if (-not $version) { $version = $file.Value.assemblyVersion }
                $lines += "  $($file.Name)  $version"
                $found += [pscustomobject]@{ Pack = $library.Name; Name = $file.Name; Version = $version }
            }
        }
        $lines += ""
    }
}

$problems = @()
if ($Notices -ne "") {
    if (-not (Test-Path $Notices)) {
        $problems += "Notices file not found: $Notices"
    } else {
        $text = Get-Content -Path $Notices -Raw

        # Every runtime pack version (e.g. "10.0.11") must be named in the notices.
        foreach ($pack in $seen.Keys) {
            $packVersion = ($pack -split '/')[-1]
            if (-not $text.Contains($packVersion)) { $problems += "Pack version not in notices: $pack" }
        }

        # The .NET runtime's own native files share one version and are covered as a group ("managed libraries
        # <version>" + "coreclr" in item 1/4). Every other file must be named with its exact version.
        $coreclr = $found | Where-Object { $_.Name -eq 'coreclr.dll' } | Select-Object -First 1
        foreach ($f in $found) {
            if (-not $text.Contains($f.Version)) { $problems += "Version not in notices: $($f.Name) $($f.Version)"; continue }
            $coveredAsGroup = $coreclr -and $f.Pack -like '*NETCore.App.Runtime*' -and $f.Version -eq $coreclr.Version
            if (-not $coveredAsGroup -and -not $text.Contains($f.Name)) { $problems += "File not named in notices: $($f.Name) $($f.Version)" }
        }
    }

    $lines += "Check against ${Notices}:"
    if ($problems.Count -eq 0) { $lines += "  OK - every bundled component and version is listed." }
    else { $problems | ForEach-Object { $lines += "  PROBLEM: $_" } }
}

$lines | Set-Content -Path $Out -Encoding UTF8
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  PROBLEM: $_" }
    exit 2
}
exit 0
