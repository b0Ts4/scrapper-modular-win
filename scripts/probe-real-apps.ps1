<#
  Diagnostic only: which real Windows apps exist on this machine, and how their windows and
  UI Automation trees look (top-level window process vs content process, AutomationIds).
  Prints process names, PIDs, titles and AutomationIds - never any typed content.
#>
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

"calc.exe in System32: $(Test-Path "$env:windir\System32\calc.exe")"
"notepad.exe in System32: $(Test-Path "$env:windir\System32\notepad.exe")"
"mspaint.exe in System32: $(Test-Path "$env:windir\System32\mspaint.exe")"
"Store packages:"; foreach ($pattern in "*Calculator*", "*Notepad*", "*Paint*") { Get-AppxPackage -Name $pattern | ForEach-Object { "  $($_.Name) $($_.Version)" } }

function Describe-TopLevel([string]$label) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        $p = $w.Current.ProcessId
        $name = try { (Get-Process -Id $p -ErrorAction Stop).ProcessName } catch { "?" }
        if ($name -match $label) { "  top-level: pid=$p process=$name title='$($w.Current.Name)' class='$($w.Current.ClassName)'"; $w }
    }
}

foreach ($app in @(@{ exe = "calc.exe"; match = "Calculator|ApplicationFrameHost|calc" }, @{ exe = "notepad.exe"; match = "notepad|Notepad" })) {
    "=== $($app.exe) ==="
    try { Start-Process $app.exe } catch { "  cannot start: $_"; continue }
    Start-Sleep -Seconds 6
    Get-Process | Where-Object { $_.ProcessName -match $app.match } | ForEach-Object { "  process: $($_.ProcessName) pid=$($_.Id) mainTitle='$($_.MainWindowTitle)'" }
    $windows = @(Describe-TopLevel $app.match | Where-Object { $_ -is [System.Windows.Automation.AutomationElement] })
    Describe-TopLevel $app.match | Where-Object { $_ -is [string] }
    foreach ($w in $windows) {
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $byProcess = @{}
        $ids = New-Object System.Collections.Generic.List[string]
        foreach ($e in $all) {
            $pid2 = $e.Current.ProcessId
            $pname = try { (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch { "?" }
            $byProcess[$pname] = 1 + [int]$byProcess[$pname]
            if ($e.Current.AutomationId) { $ids.Add("$($e.Current.AutomationId)[$($e.Current.ControlType.ProgrammaticName)]") }
        }
        "  descendants by process: $(($byProcess.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
        "  AutomationIds: $(($ids | Select-Object -First 60) -join ' ')"
    }
}
Get-Process CalculatorApp, Calculator, calc, win32calc, notepad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
