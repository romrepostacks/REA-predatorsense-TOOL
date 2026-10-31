# Fan control window for the Triton 300. Start it with FanControl.cmd.
# Closing the window returns the fans to auto. Minimizing hides it to the tray.
. $PSScriptRoot\acer.ps1
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$PanicTemp = 92   # the curve forces 100% at or above this
$script:Mode = 'Auto'
$script:Points = $null

$form = New-Object Windows.Forms.Form -Property @{
    Text = 'Triton 300 Fan Control'; ClientSize = '360,330'; FormBorderStyle = 'FixedSingle'; MaximizeBox = $false
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
    $tb.Add_MouseUp({ if ($script:Mode -eq 'Custom') { Set-Mode 'Custom' } })
    $tb
}
$cpuSlider = New-Slider 'CPU' 105
$gpuSlider = New-Slider 'GPU' 150

Add-Control (New-Object Windows.Forms.Label -Property @{ Text = 'Curve (tempC:fan%)'; Location = '15,205'; Size = '330,20' }) | Out-Null
$txtCurve = Add-Control (New-Object Windows.Forms.TextBox -Property @{ Text = '50:30,60:40,70:60,80:80,88:100'; Location = '15,228'; Size = '250,25' })
$btnCurve = Add-Control (New-Object Windows.Forms.Button -Property @{ Text = 'Apply'; Location = '272,226'; Size = '73,29' })
$btnCurve.Add_Click({ $modes['Curve'].Checked = $true; Set-Mode 'Curve' })

$lblMsg = Add-Control (New-Object Windows.Forms.Label -Property @{ Location = '15,270'; Size = '330,50'; ForeColor = 'DimGray' })

function Set-Mode([string]$m) {
    try {
        switch ($m) {
            'Auto'   { Set-Behavior $AUTO }
            'Max'    { Set-Behavior $TURBO }
            'Custom' { Set-Behavior $CUSTOM; Set-Speed $CPU_FAN $cpuSlider.Value; Set-Speed $GPU_FAN $gpuSlider.Value }
            'Curve'  { $script:Points = ConvertFrom-CurveString $txtCurve.Text; Set-Behavior $CUSTOM }
        }
        $script:Mode = $m
        $lblMsg.Text = "Mode: $m"
        Update-Fans
    } catch {
        $lblMsg.Text = "Error: $($_.Exception.Message)"
    }
}

function Update-Fans {
    $s = Get-Status
    $lblCpu.Text = "CPU  $($s.CpuTemp) C   $($s.CpuRpm) rpm"
    $lblGpu.Text = "GPU  $($s.GpuTemp) C   $($s.GpuRpm) rpm"
    $tray.Text = "CPU $($s.CpuTemp)C  GPU $($s.GpuTemp)C  ($script:Mode)"
    if ($script:Mode -ne 'Curve') { return }
    # No temperature means flying blind: hand control back to the laptop.
    if ($null -eq $s.CpuTemp -or $null -eq $s.GpuTemp) {
        $modes['Auto'].Checked = $true
        $lblMsg.Text = 'Lost temperature reading, switched to Auto.'
        return
    }
    $c = if ($s.CpuTemp -ge $PanicTemp) { 100 } else { Get-CurvePercent $s.CpuTemp $script:Points }
    $g = if ($s.GpuTemp -ge $PanicTemp) { 100 } else { Get-CurvePercent $s.GpuTemp $script:Points }
    Set-Speed $CPU_FAN $c; Set-Speed $GPU_FAN $g
    $lblMsg.Text = "Mode: Curve   CPU fan $c%   GPU fan $g%"
}

$tray = New-Object Windows.Forms.NotifyIcon -Property @{
    Icon = [Drawing.SystemIcons]::Information; Visible = $true; Text = 'Triton 300 Fan Control' }
$tray.Add_DoubleClick({ $form.Show(); $form.WindowState = 'Normal'; $form.Activate() })
$form.Add_Resize({ if ($form.WindowState -eq 'Minimized') { $form.Hide() } })

$timer = New-Object Windows.Forms.Timer -Property @{ Interval = 2000 }
$timer.Add_Tick({ try { Update-Fans } catch { $lblMsg.Text = "Error: $($_.Exception.Message)" } })

$form.Add_FormClosing({
    $timer.Stop()
    try { Set-Behavior $AUTO } catch {}
    $tray.Dispose()
})

$modes['Auto'].Checked = $true
$timer.Start()
[Windows.Forms.Application]::Run($form)
