<#
.SYNOPSIS
    Captures the screenshots used in the README and the docs.

.DESCRIPTION
    Starts Panelith once per page with demo sensors and a throwaway settings
    folder, opens it on that page through session.json, and saves a PNG of the
    window to docs\images. Your own settings are not read or changed.

    Quit any running Panelith first: only one copy can run at a time.

.EXAMPLE
    ./build/capture-screenshots.ps1
#>
[CmdletBinding()]
param(
    [string] $App = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\app\Panelith.exe'),
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\images'),
    [int] $SettleSeconds = 14
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $App)) { throw "$App was not found. Run build\publish.ps1 -SkipInstaller first." }
if (Get-Process Panelith -ErrorAction SilentlyContinue) { throw 'Panelith is running. Quit it from the notification area first.' }

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);

    public static Bitmap Capture(IntPtr window)
    {
        Rect outer, frame;
        GetWindowRect(window, out outer);
        // The visible frame, without the invisible resize border Windows 10 and 11 add.
        if (DwmGetWindowAttribute(window, 9, out frame, Marshal.SizeOf(typeof(Rect))) != 0) { frame = outer; }

        var full = new Bitmap(outer.Right - outer.Left, outer.Bottom - outer.Top);
        using (var g = Graphics.FromImage(full))
        {
            var dc = g.GetHdc();
            PrintWindow(window, dc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(dc);
        }

        var crop = new Rectangle(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        var result = full.Clone(crop, full.PixelFormat);
        full.Dispose();
        return result;
    }
}
'@ -ReferencedAssemblies System.Drawing

$settingsHome = Join-Path ([IO.Path]::GetTempPath()) ("panelith-screens-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $settingsHome, $OutputDirectory | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $settingsHome 'app.json'), '{ "demoSensors": true, "closeToTray": false, "hotkeysEnabled": false, "checkForUpdates": false }', $utf8)

$pages = [ordered]@{
    'dashboard' = 'Dashboard'
    'screens'   = 'Screens'
    'designer'  = 'Designer'
    'profiles'  = 'Profiles'
    'sensors'   = 'Sensors'
    'settings'  = 'Settings'
}

$previousHome = $env:PANELITH_HOME
$env:PANELITH_HOME = $settingsHome
try {
    foreach ($entry in $pages.GetEnumerator()) {
        $session = @{
            window         = @{ x = 80; y = 40; width = 1320; height = 860; maximized = $false }
            page           = $entry.Value
            designerScreen = 'rig'
            sensorFilter   = 'gpu'
        } | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $settingsHome 'session.json'), $session, $utf8)

        $process = Start-Process $App -PassThru
        try {
            $deadline = (Get-Date).AddSeconds(30)
            while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 300
                $process.Refresh()
            }
            if ($process.MainWindowHandle -eq 0) { throw "Panelith did not open a window for the $($entry.Value) page." }

            [WindowCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            Start-Sleep -Seconds $SettleSeconds
            $bitmap = [WindowCapture]::Capture($process.MainWindowHandle)
            $file = Join-Path $OutputDirectory "$($entry.Key).png"
            $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
            Write-Host "Saved $file"
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(10000) | Out-Null
            }
            Start-Sleep -Seconds 2
        }
    }
}
finally {
    $env:PANELITH_HOME = $previousHome
    Remove-Item $settingsHome -Recurse -Force -ErrorAction SilentlyContinue
}

# What each bundled screen looks like on a 1920x480 panel, drawn with demo sensors.
$root = Split-Path -Parent $PSScriptRoot
$screensDirectory = Join-Path $OutputDirectory 'screens'
New-Item -ItemType Directory -Force -Path $screensDirectory | Out-Null
foreach ($theme in Get-ChildItem (Join-Path $root 'themes') -Directory) {
    dotnet run --project (Join-Path $root 'src\Panelith.Cli') --verbosity quiet -- `
        preview --theme $theme.Name --width 1920 --height 480 --output (Join-Path $screensDirectory "$($theme.Name).png")
    if ($LASTEXITCODE -ne 0) { throw "Could not render the $($theme.Name) screen." }
}
