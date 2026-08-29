<#
.SYNOPSIS
    Regenerates assets\obdim.ico, the single tray icon.

.DESCRIPTION
    v1.0.7: this script USED to generate three colour-coded icons (W/A/?) so the tray
    could show the current mode at a glance. That is no longer the design. The three
    files were replaced by byte-identical copies of obdim.ico back in 3d9ab2b — a
    deliberate branding decision (one unified OB Dim mark) — which left the old script
    in a position to silently undo it: anyone running it would have re-created three
    different icons and quietly reverted the brand.

    DO NOT reintroduce per-mode icons here. Mode is reported by the tooltip, the context
    menu and the balloon notification instead. If the OB Dim mark ever changes, replace
    assets\obdim.ico (it is also the EXE icon, via <ApplicationIcon> in the csproj) and
    only re-run this script after editing $Letter / $Color below.

    Uses System.Drawing (built into Windows PowerShell 5.1).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File generate-icons.ps1
#>
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
    Write-Host "Wrote $path"
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Save-Icon 'O' ([System.Drawing.Color]::FromArgb(255, 0, 120, 96)) "$dir\obdim.ico"
Write-Host "Done. Rebuild so the new icon is embedded: dotnet build -c Release"
