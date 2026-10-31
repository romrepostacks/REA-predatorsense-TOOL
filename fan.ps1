# Fan control through Acer's gaming WMI, without PredatorSense. Run elevated.
#   .\fan.ps1                      status
#   .\fan.ps1 auto                 hand fans back to the EC
#   .\fan.ps1 max                  turbo / full speed
#   .\fan.ps1 custom -Cpu 60 -Gpu 70
#   .\fan.ps1 curve -Curve '50:30,65:50,75:70,85:100'   (tempC:percent; Ctrl+C returns to auto)
param(
    [ValidateSet('status', 'auto', 'max', 'custom', 'curve')] [string]$Mode = 'status',
    [ValidateRange(0, 100)] [int]$Cpu = 50,
    [ValidateRange(0, 100)] [int]$Gpu = 50,
    [string]$Curve = '50:30,60:40,70:60,80:80,88:100',
    [int]$Interval = 2,
    # Above this temperature the curve forces 100% whatever it says.
    [int]$PanicTemp = 92
)
. $PSScriptRoot\acer.ps1

switch ($Mode) {
    'status' { Get-Status | Format-List }
    'auto'   { Set-Behavior $AUTO; 'Fans on auto.' }
    'max'    { Set-Behavior $TURBO; 'Fans on max.' }
    'custom' { Set-Behavior $CUSTOM; Set-Speed $CPU_FAN $Cpu; Set-Speed $GPU_FAN $Gpu; "CPU $Cpu%, GPU $Gpu%." }
    'curve' {
        $pts = ConvertFrom-CurveString $Curve
        Set-Behavior $CUSTOM
        try {
            while ($true) {
                $s = Get-Status
                # No temperature reading means flying blind: give control back.
                if ($null -eq $s.CpuTemp -or $null -eq $s.GpuTemp) { throw 'Lost temperature reading.' }
                $c = if ($s.CpuTemp -ge $PanicTemp) { 100 } else { Get-CurvePercent $s.CpuTemp $pts }
                $g = if ($s.GpuTemp -ge $PanicTemp) { 100 } else { Get-CurvePercent $s.GpuTemp $pts }
                Set-Speed $CPU_FAN $c; Set-Speed $GPU_FAN $g
                '{0:HH:mm:ss} CPU {1}C {2}rpm -> {3}%   GPU {4}C {5}rpm -> {6}%' -f (Get-Date), $s.CpuTemp, $s.CpuRpm, $c, $s.GpuTemp, $s.GpuRpm, $g
                Start-Sleep -Seconds $Interval
            }
        } finally {
            Set-Behavior $AUTO
            'Fans back on auto.'
        }
    }
}
