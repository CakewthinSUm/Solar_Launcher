# 用 UI Automation 切换 ToggleSwitch 并读回状态。
param([string]$Name = "显示快照版本")

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$proc = Get-Process SolarLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Solar Launcher 未运行" }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

# ToggleSwitch 在 UIA 里是 Button 类型、名字含状态后缀，所以按前缀匹配。
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$toggle = $null
foreach ($e in $all) {
    if ($e.Current.Name -like "$Name*" -and
        $e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$null)) {
        $toggle = $e
        break
    }
}

if (-not $toggle) { throw "找不到开关: $Name" }

$pattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
"切换前: $($pattern.Current.ToggleState)"
$pattern.Toggle()
Start-Sleep -Milliseconds 800
"切换后: $($pattern.Current.ToggleState)"
