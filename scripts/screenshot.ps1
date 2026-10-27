# 截取 Solar Launcher 窗口，并可选地先导航到某个页面。
# 用法:
#   pwsh -File scripts\screenshot.ps1 -Out docs\images\home.png
#   pwsh -File scripts\screenshot.ps1 -Navigate 下载 -Out docs\images\download.png
#   pwsh -File scripts\screenshot.ps1 -Invoke 创建实例 -Out ...
param(
    [string]$Navigate,
    [string]$Invoke,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Width = 1440,
    [int]$Height = 900,
    [int]$WaitMs = 1200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class SolarWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$proc = Get-Process SolarLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Solar Launcher 未运行" }
$hwnd = $proc.MainWindowHandle
if (-not $hwnd) { throw "拿不到主窗口句柄" }

# SW_RESTORE = 9，窗口最小化时也能截图
[SolarWin]::ShowWindow($hwnd, 9) | Out-Null
# 固定窗口尺寸，保证所有截图比例一致
[SolarWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 100, 100, $Width, $Height, 0x0004) | Out-Null
[SolarWin]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
if (-not $root) { throw "无法获取窗口自动化元素" }

function Find-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $element) { throw "找不到控件: $name" }
    return $element
}

if ($Navigate) {
    $item = Find-ByName $Navigate
    $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds $WaitMs
}

if ($Invoke) {
    $button = Find-ByName $Invoke
    $obj = $null
    if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$obj)) {
        $obj.Invoke()
    }
    elseif ($button.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$obj)) {
        $obj.Select()
    }
    else { throw "控件不支持点击: $Invoke" }
    Start-Sleep -Milliseconds $WaitMs
}

# 只截客户区（不含标题栏与调整大小的描边），否则桌面会从边框漏进来
$client = New-Object SolarWin+RECT
[SolarWin]::GetClientRect($hwnd, [ref]$client) | Out-Null
$origin = New-Object SolarWin+POINT
[SolarWin]::ClientToScreen($hwnd, [ref]$origin) | Out-Null
$w = $client.Right - $client.Left
$h = $client.Bottom - $client.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
# 窗口被别的窗口挡住时 CopyFromScreen 会截到遮挡物，所以先置顶并等待重绘
[SolarWin]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400
$graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$graphics.Dispose()

$full = [System.IO.Path]::GetFullPath($Out)
$dir = [System.IO.Path]::GetDirectoryName($full)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

"已保存 $full ($w x $h)"
