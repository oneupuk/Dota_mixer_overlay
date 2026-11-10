@echo off
rem Builds DotaGlassOverlay.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No SDK / Visual Studio needed.
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "WINMD=%WINDIR%\System32\WinMetadata"
if not exist "%FW%\csc.exe" (
  echo .NET Framework 4.8 compiler not found at %FW%
  exit /b 1
)

if not exist bin mkdir bin

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /debug- /platform:anycpu ^
  /out:bin\DotaGlassOverlay.exe ^
  /win32manifest:src\app.manifest ^
  /resource:src\Overlay.xaml,Overlay.xaml ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%FW%\System.Threading.Tasks.dll" /r:"%FW%\System.IO.dll" /r:"%FW%\System.Numerics.dll" /r:"%FW%\System.Numerics.Vectors.dll" ^
  /r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.Media.winmd" /r:"%WINMD%\Windows.Storage.winmd" ^
  /r:"%WINMD%\Windows.UI.winmd" /r:"%WINMD%\Windows.Graphics.winmd" ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:Accessibility.dll ^
  src\*.cs
if errorlevel 1 exit /b 1

copy /y src\app.config bin\DotaGlassOverlay.exe.config >nul
echo Built bin\DotaGlassOverlay.exe
