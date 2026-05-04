<#
.SYNOPSIS
    SledCoopMod installer for Windows. Installs BepInEx 6 IL2CPP into the
    Sledding Game folder, drops SledCoopMod.dll into BepInEx/plugins, and
    disables the BepInEx console window so it doesn't pop up over the game.

.DESCRIPTION
    Native PowerShell port of install_sledcoopmod.py. Works on a stock
    Windows 10/11 machine with no Python installed; PowerShell 5.1 (the
    Windows-bundled version) is enough.

    Run this directly from PowerShell:
        powershell -ExecutionPolicy Bypass -File .\install_sledcoopmod.ps1

    Or just double-click install_sledcoopmod.bat which wraps this file.

    Unlike the Linux/Proton installer, no Steam launch option is needed on
    Windows — BepInEx loads winhttp.dll directly without WINEDLLOVERRIDES.
#>

[CmdletBinding()]
param(
    [string]$GamePath = "",
    [switch]$NonInteractive,
    [switch]$SkipBepInEx,
    [switch]$SkipMod,
    [switch]$SkipConsoleDisable
)

$ErrorActionPreference = 'Stop'

# ─── Constants ────────────────────────────────────────────────────────────────

$AppVersion        = '1.1.0'
$ModDll            = 'SledCoopMod.dll'
$GameFolderName    = 'Sledding Game'
$BepInExMarker     = 'BepInEx\core\BepInEx.Core.dll'
$BepInExLog        = 'BepInEx\LogOutput.log'
$BepInExCfgPath    = 'BepInEx\config\BepInEx.cfg'
$BepInExBuildIndex = 'https://builds.bepinex.dev/projects/bepinex_be'
$BepInExFallback   = 'https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip'
$BepInExMinBuild   = 755

$GameMarkers = @(
    'Sledding Game.exe',
    'Sledding Game_Data\app.info',
    'EOSBootstrapper.ini'
)

$BepInExCfgDefaults = @"
## SledCoopMod installer pre-seeded BepInEx config.
## BepInEx will fill in any other defaults on first launch — values you set
## here are preserved across launches.

[Logging.Console]

## Enables showing a console for log output.
# Setting type: Boolean
# Default value: false (disabled by SledCoopMod installer)
Enabled = false

[Logging.Disk]

## Enables writing log messages to disk.
# Setting type: Boolean
# Default value: true
Enabled = true

## Include unity log messages in log file output.
# Setting type: Boolean
# Default value: false
WriteUnityLog = false
"@

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "▶ $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-Warn2 {
    param([string]$Message)
    Write-Host "  ! $Message" -ForegroundColor Yellow
}

function Write-Err2 {
    param([string]$Message)
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

function Test-IsGameDir {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    foreach ($marker in $GameMarkers) {
        if (Test-Path -LiteralPath (Join-Path $Path $marker)) { return $true }
    }
    return $false
}

function Find-SteamLibraryFolders {
    # Find every Steam library root, including extra drives configured via
    # libraryfolders.vdf. Returns full library roots (the dir that contains
    # steamapps/).
    $roots = New-Object System.Collections.Generic.List[string]

    $primary = $null
    try {
        $primary = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name 'SteamPath' -ErrorAction Stop).SteamPath
    } catch { }
    if (-not $primary) {
        try {
            $primary = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -Name 'InstallPath' -ErrorAction Stop).InstallPath
        } catch { }
    }
    if (-not $primary) {
        $primary = 'C:\Program Files (x86)\Steam'
    }

    $primary = $primary -replace '/', '\'
    if (Test-Path -LiteralPath $primary) {
        $roots.Add($primary) | Out-Null
    }

    $vdf = Join-Path $primary 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        try {
            $text = Get-Content -LiteralPath $vdf -Raw -ErrorAction Stop
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $p = $m.Groups[1].Value -replace '\\\\', '\'
                if ((Test-Path -LiteralPath $p) -and -not ($roots -contains $p)) {
                    $roots.Add($p) | Out-Null
                }
            }
        } catch { }
    }

    return $roots
}

function Find-GameFolder {
    foreach ($root in (Find-SteamLibraryFolders)) {
        $candidate = Join-Path $root ("steamapps\common\" + $GameFolderName)
        if (Test-IsGameDir -Path $candidate) { return $candidate }
    }
    return $null
}

function Resolve-BepInExUrl {
    Write-Host "  Looking up the latest BepInEx IL2CPP build..."
    try {
        $resp = Invoke-WebRequest -Uri $BepInExBuildIndex -UseBasicParsing -TimeoutSec 12
        $matches = [regex]::Matches(
            $resp.Content,
            'href="(/projects/bepinex_be/(\d+)/[^"]*Unity\.IL2CPP[^"]*win[^"]*x64[^"]*\.zip[^"]*)"',
            'IgnoreCase'
        )
        if ($matches.Count -gt 0) {
            $best = $matches |
                ForEach-Object { [pscustomobject]@{ Path = $_.Groups[1].Value; Build = [int]$_.Groups[2].Value } } |
                Sort-Object Build -Descending |
                Select-Object -First 1
            $url = "https://builds.bepinex.dev$($best.Path)"
            Write-Host "  Found build #$($best.Build)."
            return $url
        }
        Write-Warn2 "Build server returned no matching asset; using fallback."
    } catch {
        Write-Warn2 "Build server unavailable ($($_.Exception.Message)); using fallback."
    }
    return $BepInExFallback
}

function Get-BepInExVersion {
    param([string]$GameDir)
    $log = Join-Path $GameDir $BepInExLog
    if (-not (Test-Path -LiteralPath $log)) { return '' }
    try {
        $head = (Get-Content -LiteralPath $log -TotalCount 50 -ErrorAction Stop) -join "`n"
        $m = [regex]::Match($head, 'BepInEx (6[\d.\-a-z+]+) -')
        if ($m.Success) { return $m.Groups[1].Value.Trim() }
    } catch { }
    return ''
}

function Test-BepInExOutdated {
    param([string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) { return $false }
    $m = [regex]::Match($Version, 'be\.(\d+)')
    if ($m.Success -and ([int]$m.Groups[1].Value) -lt $BepInExMinBuild) { return $true }
    if ([regex]::IsMatch($Version, 'pre\.[12]\b')) { return $true }
    return $false
}

function Install-BepInEx {
    param([string]$GameDir)
    Write-Step "Installing BepInEx 6 IL2CPP..."
    $url = Resolve-BepInExUrl
    $zip = Join-Path $GameDir '_sledcoopmod_bepinex.zip'
    Write-Host "  Downloading $url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 300
    } catch {
        throw "BepInEx download failed: $($_.Exception.Message)"
    }
    $kb = [int]((Get-Item -LiteralPath $zip).Length / 1024)
    Write-Host "  Downloaded $($zip | Split-Path -Leaf) ($kb KB)"

    Write-Host "  Extracting into $GameDir"
    try {
        Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    } catch {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        throw "Extraction failed: $($_.Exception.Message)"
    }
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Write-Ok "BepInEx installed."
}

function Disable-BepInExConsole {
    param([string]$GameDir)
    Write-Step "Disabling BepInEx console log window..."
    $cfg = Join-Path $GameDir $BepInExCfgPath
    $cfgDir = Split-Path -Parent $cfg
    if (-not (Test-Path -LiteralPath $cfgDir)) {
        New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
    }

    if (Test-Path -LiteralPath $cfg) {
        $text = Get-Content -LiteralPath $cfg -Raw -ErrorAction Stop
        $sectionMatch = [regex]::Match(
            $text,
            '(?ms)\[Logging\.Console\][^\[]*?^\s*Enabled\s*=\s*(\w+)'
        )
        if ($sectionMatch.Success) {
            $current = $sectionMatch.Groups[1].Value
            if ($current.ToLowerInvariant() -eq 'false') {
                Write-Host "  · Already disabled in BepInEx.cfg."
                return
            }
            $valueGroup = $sectionMatch.Groups[1]
            $new = $text.Substring(0, $valueGroup.Index) + 'false' + $text.Substring($valueGroup.Index + $valueGroup.Length)
            [System.IO.File]::WriteAllText($cfg, $new, (New-Object System.Text.UTF8Encoding($false)))
            Write-Ok "Set [Logging.Console] Enabled = false in existing BepInEx.cfg."
            return
        }
        if (-not $text.EndsWith("`n")) { $text += "`n" }
        $text += "`n[Logging.Console]`nEnabled = false`n"
        [System.IO.File]::WriteAllText($cfg, $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Ok "Appended [Logging.Console] Enabled = false to existing BepInEx.cfg."
        return
    }

    [System.IO.File]::WriteAllText($cfg, $BepInExCfgDefaults, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok "Wrote BepInEx.cfg with the console log window disabled."
}

function Find-ModDll {
    $scriptDir = Split-Path -Parent $PSCommandPath
    $candidates = @(
        (Join-Path $scriptDir $ModDll),
        (Join-Path $scriptDir "BepInEx\plugins\$ModDll"),
        (Join-Path $scriptDir "SledCoopMod\bin\Release\net6.0\$ModDll"),
        (Join-Path $scriptDir "SledCoopMod\bin\Debug\net6.0\$ModDll")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }
    return $null
}

function Install-ModDll {
    param([string]$GameDir)
    Write-Step "Installing $ModDll..."
    $src = Find-ModDll
    if (-not $src) {
        throw "Could not find $ModDll. Place it next to this installer and re-run."
    }
    $pluginsDir = Join-Path $GameDir 'BepInEx\plugins'
    if (-not (Test-Path -LiteralPath $pluginsDir)) {
        New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
    }
    $dest = Join-Path $pluginsDir $ModDll
    Copy-Item -LiteralPath $src -Destination $dest -Force
    $kb = [int]((Get-Item -LiteralPath $dest).Length / 1024)
    Write-Host "  $src"
    Write-Host "  → $dest ($kb KB)"
    Write-Ok "$ModDll installed."
}

function Read-GamePathFromUser {
    while ($true) {
        Write-Host ""
        $entered = Read-Host "Enter the path to the Sledding Game folder (or 'q' to quit)"
        if ([string]::IsNullOrWhiteSpace($entered)) { continue }
        if ($entered -ieq 'q') { return $null }
        $entered = $entered.Trim('"').Trim()
        if (Test-IsGameDir -Path $entered) { return (Resolve-Path -LiteralPath $entered).Path }
        Write-Warn2 "That folder doesn't look right (no '$($GameMarkers[0])'). Try again."
    }
}

# ─── Main ─────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host " SledCoopMod Installer  v$AppVersion  (Windows / native)" -ForegroundColor Magenta
Write-Host "══════════════════════════════════════════════════════════════════" -ForegroundColor Magenta

# 1. Resolve the game folder.
$resolved = $null
if ($GamePath) {
    if (Test-IsGameDir -Path $GamePath) {
        $resolved = (Resolve-Path -LiteralPath $GamePath).Path
    } else {
        Write-Err2 "Provided -GamePath '$GamePath' isn't a Sledding Game folder."
        exit 1
    }
} else {
    $here = Split-Path -Parent $PSCommandPath
    if (Test-IsGameDir -Path $here) {
        $resolved = (Resolve-Path -LiteralPath $here).Path
        Write-Host "  Detected game folder (installer is inside it): $resolved"
    } else {
        $found = Find-GameFolder
        if ($found) {
            $resolved = $found
            Write-Host "  Auto-detected game folder via Steam library: $resolved"
        } elseif ($NonInteractive) {
            Write-Err2 "Could not auto-detect the game folder. Re-run with -GamePath '<path>'."
            exit 1
        } else {
            Write-Warn2 "Could not auto-detect the Sledding Game install."
            $resolved = Read-GamePathFromUser
            if (-not $resolved) {
                Write-Host "Cancelled."
                exit 0
            }
        }
    }
}

Write-Host ""
Write-Host "Game folder: $resolved" -ForegroundColor White

# 2. Status summary.
$bepInstalled = Test-Path -LiteralPath (Join-Path $resolved $BepInExMarker)
$bepVersion   = Get-BepInExVersion -GameDir $resolved
$bepOutdated  = Test-BepInExOutdated -Version $bepVersion
$modInstalled = Test-Path -LiteralPath (Join-Path $resolved "BepInEx\plugins\$ModDll")

if ($bepInstalled -and -not $bepOutdated) {
    Write-Host ("  BepInEx 6 IL2CPP : installed " + ($(if ($bepVersion) { "($bepVersion)" } else { "" }))) -ForegroundColor Green
} elseif ($bepInstalled -and $bepOutdated) {
    Write-Host "  BepInEx 6 IL2CPP : outdated ($bepVersion) — needs be.$BepInExMinBuild+ for Unity 6" -ForegroundColor Yellow
} else {
    Write-Host "  BepInEx 6 IL2CPP : not installed" -ForegroundColor Yellow
}
if ($modInstalled) {
    Write-Host "  $ModDll  : installed" -ForegroundColor Green
} else {
    Write-Host "  $ModDll  : not installed" -ForegroundColor Yellow
}

# 3. Confirm.
if (-not $NonInteractive) {
    Write-Host ""
    $go = Read-Host "Proceed with install? [Y/n]"
    if ($go -and $go.Trim().ToLowerInvariant() -notin @('y', 'yes', '')) {
        Write-Host "Cancelled."
        exit 0
    }
}

# 4. Run the steps.
try {
    if (-not $SkipBepInEx) {
        if ($bepInstalled -and -not $bepOutdated) {
            Write-Host ""
            Write-Host "  · BepInEx already up-to-date — skipping reinstall." -ForegroundColor Gray
        } else {
            Install-BepInEx -GameDir $resolved
        }
    }

    if (-not $SkipConsoleDisable) {
        Disable-BepInExConsole -GameDir $resolved
    }

    if (-not $SkipMod) {
        Install-ModDll -GameDir $resolved
    }

    Write-Host ""
    Write-Host "✅ Done." -ForegroundColor Green
    Write-Host ""
    Write-Host "Launch Sledding Game from Steam normally. BepInEx will initialise on"
    Write-Host "first launch. Watch BepInEx\LogOutput.log to confirm SledCoopMod loaded."
    Write-Host ""
    Write-Host "If something seems wrong:"
    Write-Host "  • Re-enable the BepInEx console: edit BepInEx\config\BepInEx.cfg and"
    Write-Host "    set [Logging.Console] Enabled = true."
    Write-Host "  • Disable the mod for vanilla play: see the README for the toggle steps."
    Write-Host ""
} catch {
    Write-Host ""
    Write-Err2 $_.Exception.Message
    exit 1
}

if (-not $NonInteractive) {
    Read-Host "Press Enter to close" | Out-Null
}
