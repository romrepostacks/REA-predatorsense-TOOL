# Self-check for the curve math (no hardware touched):  .\test-curve.ps1
. $PSScriptRoot\acer.ps1
$pts = ConvertFrom-CurveString '70:60,50:30,80:80'
$cases = @{ 20 = 30; 50 = 30; 60 = 45; 70 = 60; 75 = 70; 80 = 80; 99 = 80 }
foreach ($t in $cases.Keys) {
    $got = Get-CurvePercent $t $pts
    if ($got -ne $cases[$t]) { throw "curve($t) = $got, expected $($cases[$t])" }
}
foreach ($bad in '70:101', '70', 'x:50', '') {
    $threw = $false
    try { ConvertFrom-CurveString $bad | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw "curve '$bad' should be rejected" }
}
'curve ok'
