# REA PredatorSense tool

Fan control for the Acer Predator Triton 300 (PT315) on Windows 11, without PredatorSense.

PredatorSense is only a front end. The fans are run by the embedded controller, and PredatorSense
talks to it through Acer's gaming WMI interface (`root\WMI` class `AcerGamingFunction`,
GUID `7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56`). That interface lives in the BIOS, so it still works on
Windows 11 even when the PredatorSense installer refuses to. These scripts call it directly.

Method IDs and bit layouts come from the Linux
[acer-wmi driver](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/acer-wmi.c),
which uses the same interface:

| Method | ID | Input |
|---|---|---|
| GetGamingSysInfo | 5 | `0x0001 \| sensor<<8` (CPU temp 0x01, CPU fan 0x02, GPU fan 0x06, GPU temp 0x0A) |
| SetGamingFanBehavior | 14 | bits 0-15 fans (CPU 0x1, GPU 0x8), bits 16-17 CPU mode, 22-23 GPU mode (1 auto, 2 max, 3 custom) |
| SetGamingFanSpeed | 16 | bits 0-7 fan (CPU 1, GPU 4), bits 8-15 percent |

Every output has a status in the low byte; 0 means success. The PT315 is older than the models the
Linux driver lists, so `probe.ps1` checks what this BIOS actually supports before anything is set.

## App

Double-click `FanControl.cmd` (it asks for admin). The window shows live temps and RPM and has
four modes: Auto, Max, Custom (CPU and GPU sliders) and Curve (`tempC:fan%` points, applied every
2 seconds). Custom and Curve are re-applied every 2 seconds, forced to 100% at 92C, and drop
to Auto on any error. Minimizing hides it to the tray; closing it returns the fans to auto, so leave it in
the tray while using Curve or Custom.

**Settings** has *Start with Windows* (a logon task with admin rights, so no UAC prompt; it starts
in the tray in your last mode) and shows the compatibility check. The app remembers your last mode,
curve and sliders in `%APPDATA%\PredatorFanControl\settings.json`.

**GPU overclock** sets the Normal / Turbo GPU presets (misc settings 5 and 7, as the Linux driver
does). Closing the app sets it back to Normal. If games crash, go back to Normal.

**Other models:** at start the app makes read-only calls to check for Acer gaming WMI, temperature
sensors and the fan commands. Fan modes are only enabled on models confirmed to work (currently
PT315-52); on others they stay off unless you tick *Allow fan control on this untested model*.

## Scripts (elevated PowerShell)

```powershell
.\probe.ps1 | Tee-Object probe.txt   # read-only, run first
.\test-curve.ps1                     # curve math self-check
.\fan.ps1                            # temps and RPM
.\fan.ps1 max                        # then .\fan.ps1 auto
.\fan.ps1 custom -Cpu 60 -Gpu 70
.\fan.ps1 curve -Curve '45:25,55:35,63:50,70:65,75:85,80:100'   # Ctrl+C returns fans to auto
```

The curve loop forces 100% above `-PanicTemp` (92C) and hands control back to auto if it exits or
loses the temperature reading. A reboot also resets the EC to auto.
