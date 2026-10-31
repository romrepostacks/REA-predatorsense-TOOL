# Read-only probe: what does this laptop expose for fan control? Changes nothing.
# Run from an elevated PowerShell:  .\probe.ps1 | Tee-Object probe.txt
. $PSScriptRoot\acer.ps1

"== System"
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, SystemSKUNumber | Format-List
Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion, ReleaseDate | Format-List
"OS: $((Get-CimInstance Win32_OperatingSystem).Caption) $([Environment]::OSVersion.Version)"

"`n== Acer WMI classes in root\WMI"
Get-CimClass -Namespace root/WMI | Where-Object CimClassName -match 'Acer|Gaming|WMID' | ForEach-Object {
    $_.CimClassName
    $_.CimClassMethods | ForEach-Object {
        $ps = $_.Parameters | ForEach-Object { "$($_.Name):$($_.CimType)[$($_.Qualifiers.Name -join '/')]" }
        "    $($_.Name)($($ps -join ', '))"
    }
}

"`n== Read-only gaming calls (output in hex; low byte 00 = success)"
$reads = [ordered]@{
    'GetGamingSysInfo supported sensors' = @('GetGamingSysInfo', 0x0000)
    'GetGamingSysInfo CPU temp'          = @('GetGamingSysInfo', 0x0101)
    'GetGamingSysInfo GPU temp'          = @('GetGamingSysInfo', 0x0A01)
    'GetGamingSysInfo CPU fan RPM'       = @('GetGamingSysInfo', 0x0201)
    'GetGamingSysInfo GPU fan RPM'       = @('GetGamingSysInfo', 0x0601)
    'GetGamingFanBehavior CPU+GPU'       = @('GetGamingFanBehavior', 0x0009)
    'GetGamingFanSpeed CPU'              = @('GetGamingFanSpeed', 0x01)
    'GetGamingFanSpeed GPU'              = @('GetGamingFanSpeed', 0x04)
    'GetGamingMiscSetting profiles'      = @('GetGamingMiscSetting', 0x0A)
    'GetGamingMiscSetting profile'       = @('GetGamingMiscSetting', 0x0B)
}
foreach ($k in $reads.Keys) {
    try   { '{0,-36} 0x{1:X16}' -f $k, (Invoke-AcerGaming $reads[$k][0] $reads[$k][1]) }
    catch { '{0,-36} ERROR {1}' -f $k, $_.Exception.Message }
}

"`n== Acer/Predator services, apps and devices"
Get-Service | Where-Object { $_.DisplayName -match 'Predator|Acer' -or $_.Name -match '^PS' } |
    Format-Table Name, DisplayName, Status, StartType -AutoSize
Get-AppxPackage | Where-Object Name -match 'Predator|Acer' | Format-Table Name, Version -AutoSize
Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
    Where-Object DisplayName -match 'Predator|Acer' | Format-Table DisplayName, DisplayVersion, InstallLocation -AutoSize
Get-PnpDevice | Where-Object InstanceId -match '^ACPI\\(PNP0C14|ACR)' | Format-Table Status, FriendlyName, InstanceId -AutoSize
