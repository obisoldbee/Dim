# Generates 3 simple colored .ico files for the tray (Work / Away / Unknown).
# Uses System.Drawing (built into Windows PowerShell 5.1).
# Run once to (re)generate assets:
#   powershell -ExecutionPolicy Bypass -File generate-icons.ps1
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
Save-Icon 'W' ([System.Drawing.Color]::FromArgb(255, 0, 176, 80))    "$dir\icon-work.ico"
Save-Icon 'A' ([System.Drawing.Color]::FromArgb(255, 224, 112, 32))  "$dir\icon-away.ico"
Save-Icon '?' ([System.Drawing.Color]::FromArgb(255, 128, 128, 128)) "$dir\icon-unknown.ico"
Write-Host "Done."
