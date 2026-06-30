@echo off
setlocal

echo Cleaning bin and obj folders...
echo.

for /d /r "%~dp0" %%D in (bin,obj) do (
    if exist "%%D" (
        echo Deleting: %%D
        rd /s /q "%%D"
    )
)

echo.
echo Done!
pause