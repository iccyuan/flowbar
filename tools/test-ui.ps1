# Drives the running FlowBar widget with synthetic mouse input and saves screenshots.
# Usage: pwsh tools/test-ui.ps1 -Out <dir> [-Launch] [-Action click|right|none]
param(
    [string]$Out = "$env:TEMP\flowbar-test",
    [switch]$Launch,
    [ValidateSet('click', 'right', 'none')] [string]$Action = 'none',
    [int]$SettleMs = 1500
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public class FB {
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(System.Drawing.Point p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string t);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 public struct RECT { public int L,T,R,B; }
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
 public static List<string> Dump(IntPtr p){ var list=new List<string>(); EnumChildWindows(p,(h,l)=>{var sb=new StringBuilder(256);GetClassName(h,sb,256);RECT r;GetWindowRect(h,out r);uint pid;GetWindowThreadProcessId(h,out pid);list.Add(string.Format("{0,-40} pid={1,-6} vis={2} [{3},{4}-{5},{6}]",sb,pid,IsWindowVisible(h),r.L,r.T,r.R,r.B));return true;},IntPtr.Zero); return list;}
 public static RECT Find(IntPtr p, uint pid){ RECT found=new RECT(); EnumChildWindows(p,(h,l)=>{uint q;GetWindowThreadProcessId(h,out q); if(q==pid){GetWindowRect(h,out found); return false;} return true;},IntPtr.Zero); return found; }
 public static void Click(int x,int y,bool right){ SetCursorPos(x,y); System.Threading.Thread.Sleep(80); if(right){mouse_event(8,0,0,0,UIntPtr.Zero);mouse_event(16,0,0,0,UIntPtr.Zero);} else {mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);} }
}
"@
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $PSScriptRoot "..\FlowBar\bin\Release\net10.0-windows\FlowBar.exe"
if ($Launch) { & $exe --exit; Start-Sleep 1.5; Start-Process $exe; Start-Sleep 4 }

function Shot($x, $y, $w, $h, $name, $zoom = 1) {
    $b = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($x, $y, 0, 0, $b.Size); $g.Dispose()
    if ($zoom -ne 1) {
        $z = New-Object System.Drawing.Bitmap ($w * $zoom), ($h * $zoom)
        $g = [System.Drawing.Graphics]::FromImage($z); $g.InterpolationMode = 'NearestNeighbor'
        $g.DrawImage($b, 0, 0, $w * $zoom, $h * $zoom); $g.Dispose(); $z.Save("$Out\$name.png")
    } else { $b.Save("$Out\$name.png") }
}

$proc = Get-Process FlowBar -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output "FlowBar not running"; exit 1 }
$tray = [FB]::FindWindow("Shell_TrayWnd", $null)
$tr = New-Object FB+RECT; [FB]::GetWindowRect($tray, [ref]$tr) | Out-Null
$r = [FB]::Find($tray, $proc.Id)
Write-Output ("widget [{0},{1}-{2},{3}]  taskbar [{4},{5}-{6},{7}]  WS={8}MB" -f $r.L, $r.T, $r.R, $r.B, $tr.L, $tr.T, $tr.R, $tr.B, [int]($proc.WorkingSet64 / 1MB))
[FB]::Dump($tray) | Where-Object { $_ -match 'ReBar|TrayNotify|HwndWrapper' } | ForEach-Object { Write-Output $_ }

$cx = [int](($r.L + $r.R) / 2); $cy = [int](($r.T + $r.B) / 2)
Shot ($r.L - 60) $tr.T ($r.R - $r.L + 120) ($tr.B - $tr.T) "widget" 3

if ($Action -ne 'none') {
    $pid2 = 0; $h = [FB]::WindowFromPoint([System.Drawing.Point]::new($cx, $cy)); [FB]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    if ($pid2 -ne $proc.Id) { Write-Output "point ($cx,$cy) is not FlowBar (pid $pid2); not clicking"; exit 2 }
    [FB]::Click($cx, $cy, ($Action -eq 'right'))
    Start-Sleep -Milliseconds $SettleMs
    $below = ($tr.T - 0) -lt 200
    if ($below) { Shot ($cx - 360) $tr.B 720 620 $Action } else { Shot ($cx - 360) ($tr.T - 620) 720 620 $Action }
    Write-Output "captured $Action"
}
