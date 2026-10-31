@echo off
rem Starts the fan control window as administrator (WMI fan calls need it).
powershell -NoProfile -Command "Start-Process pwsh -Verb RunAs -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-STA','-File','\"%~dp0FanControl.ps1\"'"
