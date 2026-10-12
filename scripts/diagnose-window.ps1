<#
.SYNOPSIS
  Lists how a program's window looks to UI Automation, to tell which of its fields and
  buttons the Receita Facil Agent can mark and how (AutomationId, label, Shift, OCR, image).

.DESCRIPTION
  Structure only by default: control type, AutomationId, class, framework, position, size,
  supported patterns and depth. Element NAMES and VALUES are NOT written - in a pharmacy
  system they may hold patient or prescription data. Pass -IncludeNames only on a test
  screen without real data. Nothing is sent anywhere: the report is a local text file.

.PARAMETER Process
  Process name of the program (e.g. "sistema" for sistema.exe). Without it, the open
  windows are listed so you can pick one.

.PARAMETER WindowTitle
  Part of the window title, when the process has several windows.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\diagnose-window.ps1
  powershell -ExecutionPolicy Bypass -File scripts\diagnose-window.ps1 -Process sistema
#>
param(
    [string]$Process,
    [string]$WindowTitle,
    [int]$MaxElements = 3000,
    [switch]$IncludeNames,
    [string]$Output = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'diagnostico-janela.txt')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

$windows = foreach ($w in $children) {
    try {
        $p = Get-Process -Id $w.Current.ProcessId -ErrorAction Stop
        if ($w.Current.Name) { [pscustomobject]@{ Element = $w; Process = $p.ProcessName; Pid = $p.Id; Title = $w.Current.Name } }
    } catch { }
}

if (-not $Process) {
    Write-Output "Janelas abertas (processo / PID / titulo). Rode de novo com -Process <nome>:"
    $windows | Sort-Object Process | ForEach-Object { "  {0,-30} {1,7}  {2}" -f $_.Process, $_.Pid, $_.Title }
    return
}

$target = $windows | Where-Object { $_.Process -ieq $Process -and (-not $WindowTitle -or $_.Title -like "*$WindowTitle*") }
if (-not $target) { throw "Nenhuma janela do processo '$Process' encontrada. Rode sem parametros para ver a lista." }
if (@($target).Count -gt 1) {
    Write-Output "Mais de uma janela; use -WindowTitle para escolher:"
    $target | ForEach-Object { "  {0}" -f $_.Title }
    return
}

$window = $target.Element
$wb = $window.Current.BoundingRectangle
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Diagnostico de janela - Receita Facil Agent - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$lines.Add("Processo: $($target.Process).exe   Framework: $($window.Current.FrameworkId)   Classe: $($window.Current.ClassName)")
# Inside a method call, commas would split the -f arguments into method arguments: format first.
$header = "Janela: {0}x{1}   Nomes/valores incluidos: {2}" -f @([int]$wb.Width, [int]$wb.Height, [bool]$IncludeNames)
$lines.Add($header)
$lines.Add("")
$lines.Add("prof | tipo | AutomationId | classe | framework | x,y (relativo) | LxA | padroes" + ($(if ($IncludeNames) { " | nome" } else { "" })))

$patternNames = @{
    'ValuePatternIdentifiers.Pattern' = 'Value'; 'TextPatternIdentifiers.Pattern' = 'Text'
    'InvokePatternIdentifiers.Pattern' = 'Invoke'; 'SelectionPatternIdentifiers.Pattern' = 'Selection'
    'SelectionItemPatternIdentifiers.Pattern' = 'SelItem'; 'TogglePatternIdentifiers.Pattern' = 'Toggle'
    'GridPatternIdentifiers.Pattern' = 'Grid'; 'TablePatternIdentifiers.Pattern' = 'Table'
    'ExpandCollapsePatternIdentifiers.Pattern' = 'Expand'; 'ScrollPatternIdentifiers.Pattern' = 'Scroll'
    'LegacyIAccessiblePatternIdentifiers.Pattern' = 'Legacy'
}

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$stats = @{ Total = 0; WithId = 0; Edits = 0; EditsWithId = 0; Buttons = 0; ButtonsWithId = 0; Images = 0; Panes = 0 }
$stack = New-Object System.Collections.Stack
$stack.Push(@($window, 0))
while ($stack.Count -gt 0 -and $stats.Total -lt $MaxElements) {
    $entry = $stack.Pop(); $e = $entry[0]; $depth = $entry[1]
    try {
        $c = $e.Current
        $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        $b = $c.BoundingRectangle
        $patterns = ($e.GetSupportedPatterns() | ForEach-Object { $patternNames[$_.ProgrammaticName] } | Where-Object { $_ }) -join ','
        $pos = if ($b.IsEmpty) { '-' } else { "{0},{1}" -f @([int]($b.X - $wb.X), [int]($b.Y - $wb.Y)) }
        $size = if ($b.IsEmpty) { '-' } else { "{0}x{1}" -f @([int]$b.Width, [int]$b.Height) }
        $line = "{0,4} | {1} | {2} | {3} | {4} | {5} | {6} | {7}" -f @($depth, $type, $c.AutomationId, $c.ClassName, $c.FrameworkId, $pos, $size, $patterns)
        if ($IncludeNames) { $line += " | " + $c.Name }
        $lines.Add((' ' * [Math]::Min($depth, 20)) + $line)

        $stats.Total++
        if ($c.AutomationId) { $stats.WithId++ }
        if ($type -eq 'Edit') { $stats.Edits++; if ($c.AutomationId) { $stats.EditsWithId++ } }
        if ($type -eq 'Button') { $stats.Buttons++; if ($c.AutomationId) { $stats.ButtonsWithId++ } }
        if ($type -eq 'Image') { $stats.Images++ }
        if ($type -eq 'Pane') { $stats.Panes++ }

        $kids = New-Object System.Collections.Generic.List[object]
        $child = $walker.GetFirstChild($e)
        while ($child) { $kids.Add($child); $child = $walker.GetNextSibling($child) }
        # Parentheses matter: "a, b + 1" is "(a, b) + 1" in PowerShell.
        for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@($kids[$i], ($depth + 1))) }
    } catch [System.Windows.Automation.ElementNotAvailableException] { }
}

$limitNote = if ($stats.Total -ge $MaxElements) { " (limite de $MaxElements atingido)" } else { "" }
$summary = "Resumo: {0} elementos ({1} com AutomationId); campos de texto {2} ({3} com AutomationId); botoes {4} ({5} com AutomationId); imagens {6}; paineis {7}{8}" -f @(
    $stats.Total, $stats.WithId, $stats.Edits, $stats.EditsWithId, $stats.Buttons, $stats.ButtonsWithId, $stats.Images, $stats.Panes, $limitNote)
$lines.Insert(4, $summary)
$lines.Insert(5, "")
[IO.File]::WriteAllLines($Output, $lines, [Text.UTF8Encoding]::new($true))
Write-Output "Relatorio salvo em: $Output"
Write-Output $lines[4]
