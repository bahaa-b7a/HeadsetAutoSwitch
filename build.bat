@echo off
rem Builds HeadsetAutoSwitch with the C# compiler that ships with Windows (.NET Framework 4).
rem No SDK or install needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist bin mkdir bin

rem background app (no window)
"%CSC%" /nologo /o+ /target:winexe /win32icon:assets\icon.ico /out:bin\HeadsetAutoSwitch.exe src\HeadsetAutoSwitch.cs || exit /b 1
rem console build for troubleshooting:  bin\HeadsetAutoSwitch-debug.exe --test
"%CSC%" /nologo /o+ /target:exe /win32icon:assets\icon.ico /out:bin\HeadsetAutoSwitch-debug.exe src\HeadsetAutoSwitch.cs || exit /b 1

echo Built bin\HeadsetAutoSwitch.exe and bin\HeadsetAutoSwitch-debug.exe
