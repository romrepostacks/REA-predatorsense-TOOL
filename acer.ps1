# Shared helpers for the Acer gaming WMI interface (root\WMI AcerGamingFunction,
# GUID 7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56). Bit layouts come from the Linux
# acer-wmi driver (drivers/platform/x86/acer-wmi.c).

function Get-AcerGaming {
    if (-not $script:AcerGaming) {
        $script:AcerGaming = Get-CimInstance -Namespace root/WMI -ClassName AcerGamingFunction -ErrorAction Stop |
            Select-Object -First 1
    }
    $script:AcerGaming
}

# Calls one gaming method with a single integer input and returns the output as UInt64.
# Parameter names and widths are read from the class so this works across BIOS versions.
function Invoke-AcerGaming([string]$Method, [uint64]$In) {
    $obj = Get-AcerGaming
    $m = $obj.CimClass.CimClassMethods[$Method]
    if (-not $m) { throw "AcerGamingFunction has no method $Method" }
    $inP  = $m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'In' }  | Select-Object -First 1
    $outP = $m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'Out' } | Select-Object -First 1
    $val = if ($inP.CimType -eq 'UInt32') { [uint32]($In -band 0xFFFFFFFF) } else { $In }
    $r = Invoke-CimMethod -InputObject $obj -MethodName $Method -Arguments @{ $inP.Name = $val }
    $o = $r.($outP.Name)
    if ($o -is [array]) {  # byte buffer: little-endian
        $b = [byte[]]$o + [byte[]]::new(8)
        return [BitConverter]::ToUInt64($b, 0)
    }
    [uint64]$o
}

# Piecewise-linear fan curve. $Points: sorted list of @(tempC, percent).
function Get-CurvePercent([double]$Temp, $Points) {
    if ($Temp -le $Points[0][0]) { return [int]$Points[0][1] }
    for ($i = 1; $i -lt $Points.Count; $i++) {
        $a = $Points[$i - 1]; $b = $Points[$i]
        if ($Temp -le $b[0]) {
            return [int][math]::Round($a[1] + ($b[1] - $a[1]) * ($Temp - $a[0]) / ($b[0] - $a[0]))
        }
    }
    [int]$Points[-1][1]
}

function ConvertFrom-CurveString([string]$Curve) {
    $pts = $Curve -split ',' | ForEach-Object { $t, $p = $_ -split ':'; , @([double]$t, [int]$p) }
    , @($pts | Sort-Object { $_[0] })
}
