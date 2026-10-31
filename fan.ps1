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

$FAN_BITS = 0x1 -bor 0x8           # CPU bit 0, GPU bit 3
$CPU_FAN = 0x1; $GPU_FAN = 0x4
$AUTO = 1; $TURBO = 2; $CUSTOM = 3

function Set-Behavior([int]$m) {
    $in = [uint64]$FAN_BITS -bor ([uint64]$m -shl 16) -bor ([uint64]$m -shl 22)
    $r = Invoke-AcerGaming SetGamingFanBehavior $in
    if ($r -band 0xFF) { throw ('SetGamingFanBehavior failed, status 0x{0:X2}' -f ($r -band 0xFF)) }
}
function Set-Speed([int]$fan, [int]$pct) {
    $r = Invoke-AcerGaming SetGamingFanSpeed ([uint64]$fan -bor ([uint64]$pct -shl 8))
    if ($r -band 0xFF) { throw ('SetGamingFanSpeed failed, status 0x{0:X2}' -f ($r -band 0xFF)) }
}
function Get-Sensor([int]$id) {
    $r = Invoke-AcerGaming GetGamingSysInfo (0x1 -bor ([uint64]$id -shl 8))
    if ($r -band 0xFF) { return $null }
    ($r -shr 8) -band 0xFFFF
}
function Get-Status {
    [pscustomobject]@{
        CpuTemp = Get-Sensor 0x01; GpuTemp = Get-Sensor 0x0A
        CpuRpm  = Get-Sensor 0x02; GpuRpm  = Get-Sensor 0x06
    }
}

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
