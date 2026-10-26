# 用 UI Automation 读取 Solar Launcher 窗口的控件树，便于验证交互。
# 用法: pwsh -File scripts\uia-probe.ps1 [-Invoke "按钮名"] [-SetText "输入框名=值"]
param(
    [string]$Invoke,
    [string]$SetText,
    [switch]$List
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$proc = Get-Process SolarLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Solar Launcher 未运行" }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if (-not $root) { throw "无法获取窗口自动化元素" }

function Get-AllControls {
    param($parent, $depth = 0)
    if ($depth -gt 12) { return }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($parent)
    while ($child) {
        [pscustomobject]@{
            Depth = $depth
            Type  = $child.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            Name  = $child.Current.Name
            Id    = $child.Current.AutomationId
        }
        Get-AllControls $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

if ($List -or (-not $Invoke -and -not $SetText)) {
    Get-AllControls $root | Where-Object { $_.Name -or $_.Id } | Format-Table -AutoSize
}

if ($SetText) {
    $name, $value = $SetText.Split('=', 2)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $element) { throw "找不到控件: $name" }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
    "已设置 $name = $value"
}

if ($Invoke) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Invoke)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $element) { throw "找不到控件: $Invoke" }
    # 按钮用 Invoke；导航项等列表项用 SelectionItem
    $obj = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$obj)) {
        $obj.Invoke()
        "已点击 $Invoke"
        return
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$obj)) {
        $obj.Select()
        "已选中 $Invoke"
        return
    }

    throw "控件不支持点击: $Invoke"
}
