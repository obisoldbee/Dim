<#
.SYNOPSIS
    Regenerates assets\obdim.ico — the single tray icon and the EXE icon.

.DESCRIPTION
    v1.0.7: this script USED to generate three colour-coded icons (W/A/?) so the tray
    could show the current mode at a glance. That is no longer the design. The three
    files were replaced by byte-identical copies of obdim.ico back in 3d9ab2b — a
    deliberate branding decision (one unified OB Dim mark) — which left the old script
    in a position to silently undo it.

    v1.0.8: the script is STILL dangerous in its own right. It does not redraw anything
    the user ever approved — it writes a plain Arial letter on a solid colour square.
    Running the example in the old file header replaced the approved brand icon with a
    letter tile and left the working tree dirty, with no warning and no undo.

    So overwriting is now opt-in: without -Force this script only previews. With -Force
    it backs the existing icon up next to itself before writing.

    DO NOT reintroduce per-mode icons here. Mode is reported by the tooltip, the context
    menu and the balloon notification instead.

    Uses System.Drawing (built into Windows PowerShell 5.1).

.PARAMETER Force
    Actually write the file. Without it the script reports what it would do and exits.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File generate-icons.ps1
    # Preview only — prints the target path and whether it already exists.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File generate-icons.ps1 -Force
    # Writes obdim.ico, backing up the previous one to obdim.ico.bak.<timestamp> first.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Force
)

Add-Type -AssemblyName System.Drawing

function Save-Icon([string]$letter, [System.Drawing.Color]$color, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap 48, 48
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear($color)

    $brush = [System.Drawing.Brushes]::White
    $font = New-Object System.Drawing.Font 'Arial', 22, ([System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, 0, 48, 48
    $g.DrawString($letter, $font, $brush, $rect, $sf)

    $g.Dispose()
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $fs = [System.IO.File]::Create($path)
    $icon.Save($fs)
    $fs.Close()
    $bmp.Dispose()
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $dir 'obdim.ico'
$exists = Test-Path $target

if (-not $Force) {
    Write-Host "Preview only — nothing was written." -ForegroundColor Yellow
    Write-Host "  target : $target"
    if ($exists) {
        $existing = Get-Item $target
        Write-Host "  exists : $($existing.Length) bytes, last written $($existing.LastWriteTime)"
        Write-Host ""
        Write-Host "  This script draws a plain Arial letter on a solid colour square. It is NOT"
        Write-Host "  a redraw of the approved OB Dim mark — running it with -Force REPLACES that"
        Write-Host "  icon and leaves the working tree dirty. A timestamped backup is taken first."
        Write-Host ""
        Write-Host "  Only continue if you really intend to replace the brand icon."
        Write-Host "  Re-run with: powershell -ExecutionPolicy Bypass -File generate-icons.ps1 -Force"
    }
    else {
        Write-Host "  exists : no — a new file would be created."
        Write-Host ""
        Write-Host "  Re-run with -Force to create it."
    }
    Write-Host ""
    Write-Host "Done (no changes)."
    return
}

if ($exists -and -not $PSCmdlet.ShouldProcess($target, 'Overwrite the OB Dim icon')) {
    Write-Host "Cancelled."
    return
}

if ($exists) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $backup = "$target.bak.$stamp"
    Copy-Item -Path $target -Destination $backup -Force
    Write-Host "Backed up the existing icon to $backup"
}

Save-Icon 'O' ([System.Drawing.Color]::FromArgb(255, 0, 120, 96)) $target
Write-Host "Wrote $target"
Write-Host "Done. Rebuild so the new icon is embedded: dotnet build -c Release"
