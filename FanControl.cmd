@echo off
rem Starts the fan control window as administrator (WMI fan calls need it).
rem pwsh hides its own console; Start-Process -WindowStyle Hidden would hide the form too.
powershell -NoProfile -Command "Start-Process pwsh -Verb RunAs -ArgumentList '-WindowStyle','Hidden','-NoProfile','-ExecutionPolicy','Bypass','-STA','-File','\"%~dp0FanControl.ps1\"'"
