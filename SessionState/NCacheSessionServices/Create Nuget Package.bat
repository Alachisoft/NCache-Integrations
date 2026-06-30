:: - Author:Usman Shahid

::Prerequisite: Add nuget.exe to this folder, or add path to nuget.exe to the "PATH" environment variable.
::Prerequisite: Check that you have the latest framework version by navigating to windows\Microsoft.NET\Framework\ and replace version below with the highest version in the directory. Note that the project requires a minimum version of v4.0.30319
@echo off

set msBuildDir=%WINDIR%\Microsoft.NET\Framework\v4.0.30319

:: Append the following to the next line to generate a build log file "/l:FileLogger,Microsoft.Build.Engine;logfile=release_build_log.log"
call %msBuildDir%\msbuild.exe  NCacheSessionServices.csproj /p:Configuration=Release  /verbosity:n /l:FileLogger,Microsoft.Build.Engine;logfile=release_build_log.log
call nuget.exe pack NCacheSessionServices.csproj