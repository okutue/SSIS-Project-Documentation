# setup-ssis-refs.ps1
# Copies SSIS runtime DLLs into lib/ssis/ so the project builds without
# machine-specific absolute paths in the .csproj.
#
# IMPORTANT: Run this script as Administrator. It reads from protected
# Windows GAC and Program Files locations that require elevated access.
#
#   Right-click PowerShell -> "Run as administrator", then:
#   Set-Location "<repo root>"
#   .\setup-ssis-refs.ps1
#
#   Or from a normal terminal (will auto-elevate):
#   powershell -ExecutionPolicy Bypass -File setup-ssis-refs.ps1
#
# Supported installs (searched in order, newest wins):
#   - SQL Server 2022 SSIS 16.x
#   - SQL Server 2019 SSIS 15.x
#   - SQL Server 2017 SSIS 14.x
#   - SQL Server 2012 SSIS 11.x
#   - Visual Studio 2022/2019/2017 SSIS extension (any edition)
#   - SQL Server Management Studio (SSMS 19/20/21/22)
#   - SQL Server DTS\Binn install folders

# ── Admin check ────────────────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "This script requires administrator privileges to read from the Windows GAC." -ForegroundColor Yellow
    Write-Host "Relaunching as administrator..." -ForegroundColor Yellow
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}
# ───────────────────────────────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"

$dest = Join-Path $PSScriptRoot "lib\ssis"
New-Item -ItemType Directory -Force $dest | Out-Null

$token = "89845dcd8080cc91"

# GAC version candidates — newest first so the highest available version wins.
# Covers SQL Server 2022 (16), 2019 (15), 2017 (14), 2012 (11).
$gacVersions = @(
    "v4.0_16.0.0.0__$token",
    "v4.0_15.0.0.0__$token",
    "v4.0_14.0.0.0__$token",
    "v4.0_11.0.0.0__$token"
)

# GAC sub-stores to probe per assembly.
# DTSRuntimeWrap ships as both 32-bit and 64-bit depending on the installer;
# check both GAC_64 and GAC_32.
$gacBases = @{
    "Microsoft.SqlServer.ManagedDTS.dll"     = @("GAC_MSIL")
    "Microsoft.SqlServer.DTSPipelineWrap.dll" = @("GAC_MSIL")
    "Microsoft.SqlServer.DTSRuntimeWrap.dll" = @("GAC_64", "GAC_32")
    "Microsoft.SqlServer.SqlTDiagM.dll"      = @("GAC_MSIL")
}

# Visual Studio SSIS extension paths — covers VS 2022, 2019, 2017 all editions,
# and SSIS versions 170 (SSMS 22), 160, 150, 140.
$vsEditions  = @("Enterprise", "Professional", "Community", "BuildTools", "Preview")
$ssisVersions = @("170", "160", "150", "140", "130")
$vsBases = @(
    "C:\Program Files\Microsoft Visual Studio",
    "C:\Program Files (x86)\Microsoft Visual Studio"
)
$vsYears = @("2022", "2019", "2017")

$vsFolders = foreach ($base in $vsBases) {
    foreach ($year in $vsYears) {
        foreach ($ed in $vsEditions) {
            foreach ($ver in $ssisVersions) {
                # Common paths for all versions
                "$base\$year\$ed\Common7\IDE\CommonExtensions\Microsoft\SSIS\$ver\Binn"
                "$base\$year\$ed\Common7\IDE\CommonExtensions\Microsoft\SSIS\$ver"
                "$base\$year\$ed\Common7\IDE\PublicAssemblies\SSIS\$ver"

                # SqlTDiagM v160 lives in a SQLCommon subfolder under the SSIS\160 tree
                if ($ver -eq "160") {
                    "$base\$year\$ed\Common7\IDE\CommonExtensions\Microsoft\SSIS\160\SQLCommon"
                }

                # SqlTDiagM v130-v150 use the older Extensions\SQLCommon\$ver layout
                if ($ver -in @("130", "140", "150")) {
                    "$base\$year\$ed\Common7\IDE\Extensions\Microsoft\SQLCommon\$ver"
                }
            }
        }
    }
}

# SSMS install paths (SSMS 18/19 = v18/v19 folder, SSMS 20/21/22 use Release subfolder)
$ssmsFolders = @(
    "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\CommonExtensions\Microsoft\SSIS\170",
    "C:\Program Files\Microsoft SQL Server Management Studio 20\Common7\IDE\CommonExtensions\Microsoft\SSIS\160",
    "C:\Program Files\Microsoft SQL Server Management Studio 19\Common7\IDE\CommonExtensions\Microsoft\SSIS\160",
    "C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\CommonExtensions\Microsoft\SSIS\150"
)

# SQL Server DTS\Binn install folders (full SQL Server engine install)
$dtsFolders = foreach ($ver in @("160", "150", "140", "110")) {
    "C:\Program Files\Microsoft SQL Server\$ver\DTS\Binn"
    "C:\Program Files (x86)\Microsoft SQL Server\$ver\DTS\Binn"
}

function Find-Assembly($name) {
    $nameNoExt = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $bases = $gacBases[$name]

    # 1. Windows GAC — try every version + every applicable sub-store
    foreach ($base in $bases) {
        foreach ($ver in $gacVersions) {
            $path = "C:\Windows\Microsoft.NET\assembly\$base\$nameNoExt\$ver\$name"
            if (Test-Path $path) { return $path }
        }
    }

    # 2. Visual Studio SSIS extension folders
    foreach ($folder in $vsFolders) {
        $path = Join-Path $folder $name
        if (Test-Path $path) { return $path }
    }

    # 3. SSMS install folders
    foreach ($folder in $ssmsFolders) {
        $path = Join-Path $folder $name
        if (Test-Path $path) { return $path }
    }

    # 4. SQL Server DTS\Binn folders
    foreach ($folder in $dtsFolders) {
        $path = Join-Path $folder $name
        if (Test-Path $path) { return $path }
    }

    return $null
}

Write-Host "`nSearching for SSIS assemblies..." -ForegroundColor Cyan

$allFound = $true

foreach ($name in $gacBases.Keys) {
    $src = Find-Assembly $name
    if ($src) {
        Copy-Item $src (Join-Path $dest $name) -Force
        Write-Host "  OK       $name" -ForegroundColor Green
        Write-Host "           $src" -ForegroundColor DarkGray
    } else {
        Write-Host "  MISSING  $name" -ForegroundColor Red
        $allFound = $false
    }
}

Write-Host ""
if ($allFound) {
    Write-Host "All SSIS assemblies copied to lib\ssis\." -ForegroundColor Cyan
    Write-Host "You can now build the solution:  dotnet build SsisLineage.slnx" -ForegroundColor Cyan
} else {
    Write-Host "One or more assemblies were not found." -ForegroundColor Yellow
    Write-Host "Searched locations:" -ForegroundColor Yellow
    Write-Host "  - Windows GAC (versions 11/14/15/16, stores GAC_MSIL/GAC_64/GAC_32)" -ForegroundColor Yellow
    Write-Host "  - Visual Studio 2017/2019/2022 SSIS extension folders (all editions)" -ForegroundColor Yellow
    Write-Host "  - SSMS 18/19/20/22 CommonExtensions SSIS folders" -ForegroundColor Yellow
    Write-Host "  - SQL Server DTS\Binn folders (versions 110/140/150/160)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If SSIS is installed but still not found, locate the DLLs manually:" -ForegroundColor Yellow
    Write-Host "  Get-ChildItem -Path 'C:\Program Files','C:\Program Files (x86)' ``" -ForegroundColor Gray
    Write-Host "    -Recurse -Filter 'Microsoft.SqlServer.ManagedDTS.dll' -ErrorAction SilentlyContinue" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Then copy all 4 DLLs directly into the lib\ssis\ folder and build." -ForegroundColor Yellow
    Write-Host "See CONTRIBUTING.md for details." -ForegroundColor Yellow
    exit 1
}
