# Builds dist\BatteryBar.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or packages needed.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path "$root\src\icon.ico")) { & "$root\tools\make-icon.ps1" }
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$root\dist\BatteryBar.exe" "/win32manifest:$root\src\app.manifest" "/win32icon:$root\src\icon.ico" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll $src
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Get-Item "$root\dist\BatteryBar.exe" | Select-Object Name, Length
