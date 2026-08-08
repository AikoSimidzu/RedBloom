# Builds the x64 capture hook. Wallpaper Engine is a 64-bit process, so a 32-bit build would
# simply refuse to load.
$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -property installationPath
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'

if (-not (Test-Path $vcvars)) {
    throw "vcvars64.bat not found under $vsPath"
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

# cl has to run with the toolchain environment, and that only comes from the batch file.
$command = "call `"$vcvars`" >nul && cl /nologo /LD /O2 /EHsc /std:c++17 /W3 " +
           "/Fe:`"$out\RedBloomHook.dll`" /Fo:`"$out\\`" `"$here\dllmain.cpp`" " +
           "/link /OUT:`"$out\RedBloomHook.dll`""

cmd /c $command
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

Get-Item "$out\RedBloomHook.dll" | Select-Object FullName, Length, LastWriteTime
