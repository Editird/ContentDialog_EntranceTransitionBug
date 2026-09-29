# Opens Task Manager's "End task" confirmation ContentDialog for a dummy PING.EXE (Details page),
# then presses Cancel (never confirms). Repeats N times. Logs QPC timestamps for frame alignment.
param([int]$times = 3, [string]$log, [int]$targetPid)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
$AE = [Windows.Automation.AutomationElement]
$sw = [Diagnostics.Stopwatch]
function Qpc { "{0:F1}" -f ($sw::GetTimestamp() * 1000.0 / $sw::Frequency) }
function Cond($prop, $val) { New-Object Windows.Automation.PropertyCondition($prop, $val) }
$tm = $AE::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, (Cond $AE::NameProperty "Task Manager"))
if (-not $tm) { throw "Task Manager window not found" }
function Find($cond, $what) {
    for ($i = 0; $i -lt 50; $i++) { $e = $tm.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
    throw "not found: $what"
}
function Button($name) {
    Find (New-Object Windows.Automation.AndCondition((Cond $AE::NameProperty $name), (Cond $AE::ControlTypeProperty ([Windows.Automation.ControlType]::Button)))) "button $name"
}
function Invoke($e) { $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }

# Filter the Details list down to the dummy process only.
$search = Find (Cond $AE::ControlTypeProperty ([Windows.Automation.ControlType]::Edit)) "search box"
$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("$targetPid")
Start-Sleep -Milliseconds 1500
$list = Find (Cond $AE::ClassNameProperty "WdcListView") "process list"

$lines = @()
for ($k = 1; $k -le $times; $k++) {
    $list.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("{HOME}")
    Start-Sleep -Milliseconds 700
    $end = Button "End task"
    if (-not $end.Current.IsEnabled) { throw "End task not enabled (row not selected)" }
    $lines += "$(Qpc),ShowAsync,endtask-$k"
    Invoke $end
    Start-Sleep -Milliseconds 1800
    $title = $tm.FindFirst([Windows.Automation.TreeScope]::Descendants, (Cond $AE::ControlTypeProperty ([Windows.Automation.ControlType]::Text)))
    $lines += "$(Qpc),Hide,cancel-$k"
    Invoke (Button "Cancel")
    Start-Sleep -Milliseconds 1800
}
$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("")
$lines | % { $p = $_.Split(','); "0,$($p[1]),$($p[2]),$($p[0])" } | Set-Content $log
"done"
