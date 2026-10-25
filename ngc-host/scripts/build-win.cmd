@echo off
rem Copyright (c) 2026 Legion Builds
rem Licensed under the MIT License; see LICENSE.txt in the repository root.
rem
rem Builds slua_shim.dll with the Visual Studio 2022 C++ tools, using the CMake and
rem Ninja that ship with Visual Studio. At most 2 compile jobs (set in the wrapper).
rem Usage: build-win.cmd <repo root> <build dir>
setlocal
set "REPO=%~1"
set "BUILD=%~2"
if "%REPO%"=="" exit /b 2
if "%BUILD%"=="" exit /b 2
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if "%VSROOT%"=="" (echo Visual Studio 2022 C++ tools not found & exit /b 3)
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 4
cmake -S "%REPO%\ngc-host" -B "%BUILD%" -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 5
cmake --build "%BUILD%" || exit /b 6
