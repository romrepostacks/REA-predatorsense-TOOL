# Fan control window for the Triton 300. Start it with FanControl.cmd.
# Closing the window returns the fans to auto. Minimizing hides it to the tray.
# Custom and Curve are re-applied every 2 s, forced to 100% at $PanicTemp, and drop to Auto on any error.
# Settings (last mode, curve, sliders) live in %APPDATA%\PredatorFanControl\settings.json.
param([switch]$Minimized)   # used by the start-with-Windows task
. $PSScriptRoot\acer.ps1
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$PanicTemp = 92   # Custom and Curve force 100% at or above this
$script:Mode = 'Auto'
$script:Points = $null
# Curve smoothing, to stop surging on short CPU spikes. Per 2 s tick:
$Smooth = 0.25    # weight of the newest reading in the averaged temperature (about 8 s to settle)
$StepUp = 10      # most a fan rises, in %
$StepDown = 3     # most a fan falls, in %
$script:Last = @{ Cpu = 100; Gpu = 100 }
$script:Avg = @{ Cpu = $null; Gpu = $null }

$Self = $PSCommandPath
$TaskName = 'PredatorFanControl'
$CfgPath = Join-Path $env:APPDATA 'PredatorFanControl\settings.json'
$script:Cfg = [pscustomobject]@{ Mode = 'Auto'; Curve = '45:25,55:35,63:50,70:65,75:85,80:100'; Cpu = 60; Gpu = 60; AllowUntested = $false }
if (Test-Path $CfgPath) {
    try { (Get-Content $CfgPath -Raw | ConvertFrom-Json).psobject.Properties | ForEach-Object { $script:Cfg.($_.Name) = $_.Value } } catch {}
}
function Save-Cfg {
    New-Item -ItemType Directory -Force (Split-Path $CfgPath) | Out-Null
    $script:Cfg | ConvertTo-Json | Set-Content $CfgPath
}
$Support = Test-AcerSupport
$script:Allowed = $false   # no fan writes at all until the compatibility check allows them

$form = New-Object Windows.Forms.Form -Property @{
    Text = 'Predator Fan Control'; ClientSize = '360,330'; FormBorderStyle = 'FixedSingle'; MaximizeBox = $false
    Font = New-Object Drawing.Font('Segoe UI', 10)
}
function Add-Control($c) { $form.Controls.Add($c); $c }

$lblCpu = Add-Control (New-Object Windows.Forms.Label -Property @{ Location = '15,12'; Size = '330,22' })
$lblGpu = Add-Control (New-Object Windows.Forms.Label -Property @{ Location = '15,36'; Size = '330,22' })

$modes = @{}
$x = 15
foreach ($m in 'Auto', 'Max', 'Custom', 'Curve') {
    $rb = Add-Control (New-Object Windows.Forms.RadioButton -Property @{ Text = $m; Location = "$x,68"; AutoSize = $true })
    $rb.Add_CheckedChanged({ if ($this.Checked) { Set-Mode $this.Text } })
    $modes[$m] = $rb
    $x += 85
}

function New-Slider($label, $y) {
    Add-Control (New-Object Windows.Forms.Label -Property @{ Text = $label; Location = "15,$($y + 8)"; Size = '45,22' }) | Out-Null
    $tb = Add-Control (New-Object Windows.Forms.TrackBar -Property @{
        Location = "60,$y"; Size = '230,45'; Minimum = 0; Maximum = 100; TickFrequency = 10; SmallChange = 5; LargeChange = 10 })
    $tb.Value = 60   # after Maximum, which defaults to 10
    $val = Add-Control (New-Object Windows.Forms.Label -Property @{ Text = '60%'; Location = "295,$($y + 8)"; Size = '50,22' })
    $tb.Add_ValueChanged({ $val.Text = "$($this.Value)%" }.GetNewClosure())
    $tb
}
$cpuSlider = New-Slider 'CPU' 105
$gpuSlider = New-Slider 'GPU' 150

Add-Control (New-Object Windows.Forms.Label -Property @{ Text = 'Curve (tempC:fan%)'; Location = '15,205'; Size = '330,20' }) | Out-Null
$txtCurve = Add-Control (New-Object Windows.Forms.TextBox -Property @{ Text = $script:Cfg.Curve; Location = '15,228'; Size = '250,25' })
$btnCurve = Add-Control (New-Object Windows.Forms.Button -Property @{ Text = 'Apply'; Location = '272,226'; Size = '73,29' })
$btnCurve.Add_Click({ $modes['Curve'].Checked = $true; Set-Mode 'Curve' })

$lblMsg = Add-Control (New-Object Windows.Forms.Label -Property @{ Location = '15,270'; Size = '250,50'; ForeColor = 'DimGray' })
$btnSettings = Add-Control (New-Object Windows.Forms.Button -Property @{ Text = 'Settings'; Location = '272,280'; Size = '73,29' })
$btnSettings.Add_Click({ Show-Settings })

function Set-Mode([string]$m) {
    try {
        switch ($m) {
            'Auto'   { if ($script:Allowed) { Set-Behavior $AUTO } }
            'Max'    { Set-Behavior $TURBO }
            'Custom' { $script:Points = $null; Set-Behavior $CUSTOM }
            'Curve'  { $script:Points = ConvertFrom-CurveString $txtCurve.Text; Set-Behavior $CUSTOM }
        }
        $script:Mode = $m
        $lblMsg.Text = "Mode: $m"
        $script:Cfg.Mode = $m; $script:Cfg.Curve = $txtCurve.Text; $script:Cfg.Cpu = $cpuSlider.Value; $script:Cfg.Gpu = $gpuSlider.Value
        Save-Cfg
        Update-Fans
    } catch {
        Stop-Manual "Error: $($_.Exception.Message)"
    }
}

# Any failure while we hold the fans hands them back to the laptop.
function Stop-Manual([string]$why) {
    $modes['Auto'].Checked = $true
    $lblMsg.Text = "$why  Switched to Auto."
}

# Follows the curve on an averaged temperature, moving the fan only a few % per tick.
function Get-SmoothFan([string]$k, [double]$temp) {
    $a = $script:Avg[$k]
    $a = if ($null -eq $a) { $temp } else { $a + $Smooth * ($temp - $a) }
    $script:Avg[$k] = $a
    $target = Get-CurvePercent $a $script:Points
    $last = $script:Last[$k]
    [int][math]::Min([math]::Max($target, $last - $StepDown), $last + $StepUp)
}

function Update-Fans {
    $s = Get-Status
    $lblCpu.Text = "CPU  $($s.CpuTemp) C   $($s.CpuRpm) rpm"
    $lblGpu.Text = "GPU  $($s.GpuTemp) C   $($s.GpuRpm) rpm"
    $tray.Text = "CPU $($s.CpuTemp)C  GPU $($s.GpuTemp)C  ($script:Mode)"
    if ($script:Mode -notin 'Custom', 'Curve') { return }
    # No temperature means flying blind.
    if ($null -eq $s.CpuTemp -or $null -eq $s.GpuTemp) { Stop-Manual 'Lost temperature reading.'; return }
    if ($script:Points) {
        $c = Get-SmoothFan 'Cpu' $s.CpuTemp; $g = Get-SmoothFan 'Gpu' $s.GpuTemp
    } else {
        $c = $cpuSlider.Value; $g = $gpuSlider.Value
    }
    if ($s.CpuTemp -ge $PanicTemp) { $c = 100 }
    if ($s.GpuTemp -ge $PanicTemp) { $g = 100 }
    Set-Speed $CPU_FAN $c; Set-Speed $GPU_FAN $g
    $script:Last = @{ Cpu = $c; Gpu = $g }
    $lblMsg.Text = "Mode: $script:Mode   CPU fan $c%   GPU fan $g%"
}

function Test-StartTask { [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) }

# Logon task with highest privileges, so the app starts elevated without a UAC prompt.
function Set-StartTask([bool]$on) {
    if (-not $on) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue; return }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $act = New-ScheduledTaskAction -Execute (Get-Process -Id $PID).Path `
        -Argument "-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -STA -File `"$Self`" -Minimized"
    Register-ScheduledTask -TaskName $TaskName -Force -Action $act `
        -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) `
        -Principal (New-ScheduledTaskPrincipal -UserId $user -RunLevel Highest -LogonType Interactive) `
        -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)) | Out-Null
}

# Greys out fan modes this laptop can't (or isn't trusted to) do.
function Update-Allowed {
    $ok = [bool]($Support.Fans -and ($Support.Tested -or $script:Cfg.AllowUntested))
    if (-not $ok -and $script:Mode -ne 'Auto') { Stop-Manual 'Fan control not allowed on this model.' }
    $script:Allowed = $ok
    foreach ($m in 'Max', 'Custom', 'Curve') { $modes[$m].Enabled = $ok }
    $btnCurve.Enabled = $ok
    $ok
}

function Show-Settings {
    $dlg = New-Object Windows.Forms.Form -Property @{
        Text = 'Settings'; ClientSize = '360,250'; FormBorderStyle = 'FixedDialog'; MaximizeBox = $false; MinimizeBox = $false
        StartPosition = 'CenterParent'; Font = $form.Font }
    $chkStart = New-Object Windows.Forms.CheckBox -Property @{ Text = 'Start with Windows (in the tray, last mode)'; Location = '15,15'; AutoSize = $true }
    $chkStart.Checked = Test-StartTask
    $chkUntested = New-Object Windows.Forms.CheckBox -Property @{ Text = 'Allow fan control on this untested model'; Location = '15,45'; AutoSize = $true }
    $chkUntested.Checked = $script:Cfg.AllowUntested
    $chkUntested.Visible = -not $Support.Tested
    $yn = { param($b) if ($b) { 'yes' } else { 'no' } }
    $lbl = New-Object Windows.Forms.Label -Property @{ Location = '15,80'; Size = '330,120'; Text = @(
        "Compatibility check",
        "Model: $($Support.Model)  (tested: $(& $yn $Support.Tested))",
        "Acer gaming WMI: $(& $yn $Support.Wmi)",
        "Temperature sensors: $(& $yn $Support.Sensors)",
        "Fan control commands: $(& $yn $Support.Fans)") -join "`n" }
    $ok = New-Object Windows.Forms.Button -Property @{ Text = 'OK'; Location = '272,210'; Size = '73,29'; DialogResult = 'OK' }
    $dlg.Controls.AddRange(@($chkStart, $chkUntested, $lbl, $ok))
    $dlg.AcceptButton = $ok
    if ($dlg.ShowDialog($form) -ne 'OK') { return }
    try { Set-StartTask $chkStart.Checked } catch { $lblMsg.Text = "Start with Windows failed: $($_.Exception.Message)" }
    $script:Cfg.AllowUntested = $chkUntested.Checked
    Save-Cfg
    Update-Allowed | Out-Null
}

$tray = New-Object Windows.Forms.NotifyIcon -Property @{
    Icon = [Drawing.SystemIcons]::Information; Visible = $true; Text = 'Predator Fan Control' }
$tray.Add_DoubleClick({ $form.Show(); $form.WindowState = 'Normal'; $form.Activate() })
$form.Add_Resize({ if ($form.WindowState -eq 'Minimized') { $form.Hide() } })

$timer = New-Object Windows.Forms.Timer -Property @{ Interval = 2000 }
$timer.Add_Tick({ try { Update-Fans } catch { Stop-Manual "Error: $($_.Exception.Message)" } })

$form.Add_FormClosing({
    $timer.Stop()
    if ($script:Allowed) { try { Set-Behavior $AUTO } catch {} }
    $tray.Dispose()
})

$form.Add_Shown({ if ($Minimized) { $form.Hide() } })

$cpuSlider.Value = $script:Cfg.Cpu; $gpuSlider.Value = $script:Cfg.Gpu
$startMode = $script:Cfg.Mode   # checking Auto below saves over it
$modes['Auto'].Checked = $true
if ((Update-Allowed) -and $startMode -in 'Max', 'Custom', 'Curve') { $modes[$startMode].Checked = $true }
elseif (-not $Support.Fans) { $lblMsg.Text = 'Fan control not supported here. See Settings.' }
elseif (-not $Support.Tested) { $lblMsg.Text = 'Untested model: fan modes are off. See Settings.' }
$timer.Start()
[Windows.Forms.Application]::Run($form)
