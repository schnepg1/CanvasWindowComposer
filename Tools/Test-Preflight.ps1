# Ensures no CanvasDesktop instance is running before tests touch native windows.
[CmdletBinding()]
param(
    [ValidateSet('Prompt', 'Fail')]
    [string]$Mode = 'Prompt'
)
$ErrorActionPreference = 'Stop'

if ($env:CI -or $env:TF_BUILD -or $env:GITHUB_ACTIONS -or $env:CANVAS_TEST_NO_PROMPT -eq '1') {
    $Mode = 'Fail'
}

function Get-CanvasProcesses {
    @(Get-Process -Name 'CanvasDesktop' -ErrorAction SilentlyContinue)
}

function Get-CanvasSummary {
    $items = foreach ($process in Get-CanvasProcesses) {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($path) { "PID $($process.Id): $path" } else { "PID $($process.Id)" }
    }
    $items -join [Environment]::NewLine
}

function Exit-Canvas {
    foreach ($process in Get-CanvasProcesses) {
        try {
            $path = $process.Path
            if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
            $child = Start-Process -FilePath $path -ArgumentList '--exit' -WindowStyle Hidden -PassThru -ErrorAction Stop
            $child.WaitForExit(10000) | Out-Null
        } catch { }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-CanvasProcesses)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return -not (Get-CanvasProcesses)
}

function Show-CanvasRunningDialog {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Canvas is running'
    $form.ClientSize = New-Object System.Drawing.Size(560, 175)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false

    $label = New-Object System.Windows.Forms.Label
    $label.SetBounds(16, 12, 528, 100)
    $label.Text = "CanvasDesktop is running.`r`n`r`nCanvas hooks, overlay and native windows make tests flaky. Close Canvas before running tests.`r`n`r`nChoose Exit Canvas for a clean shutdown, Retry after closing it yourself, or Cancel."

    $exit = New-Object System.Windows.Forms.Button
    $exit.Text = 'Exit Canvas and continue'
    $exit.SetBounds(16, 125, 190, 32)
    $exit.DialogResult = [System.Windows.Forms.DialogResult]::Yes

    $retry = New-Object System.Windows.Forms.Button
    $retry.Text = 'Retry'
    $retry.SetBounds(216, 125, 100, 32)
    $retry.DialogResult = [System.Windows.Forms.DialogResult]::No

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.SetBounds(326, 125, 100, 32)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

    $form.Controls.AddRange(@($label, $exit, $retry, $cancel))
    $form.AcceptButton = $exit
    $form.CancelButton = $cancel
    $result = $form.ShowDialog()
    $form.Dispose()

    switch ($result) {
        ([System.Windows.Forms.DialogResult]::Yes) { return 'Exit' }
        ([System.Windows.Forms.DialogResult]::No) { return 'Retry' }
        default { return 'Cancel' }
    }
}

$processes = Get-CanvasProcesses
if ($processes.Count -eq 0) { exit 0 }

if ($Mode -eq 'Fail') {
    [Console]::Error.WriteLine("CanvasDesktop is running. Close it before running tests.`r`n$([Environment]::NewLine)$(Get-CanvasSummary)")
    exit 1
}

Add-Type -AssemblyName System.Windows.Forms
while ($true) {
    if (-not (Get-CanvasProcesses)) { exit 0 }
    switch (Show-CanvasRunningDialog) {
        'Exit' {
            if (Exit-Canvas) { continue }
            [System.Windows.Forms.MessageBox]::Show(
                "CanvasDesktop is still running. Exit it from the tray menu and choose Retry.`r`n`r`n$(Get-CanvasSummary)",
                'Canvas is still running',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        }
        'Retry' { continue }
        default { exit 1 }
    }
}
