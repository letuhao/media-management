@echo off
REM Cleanup Duplicate Records - Batch Script
REM This script provides multiple ways to run the cleanup

echo 🧹 Cleanup Duplicate Thumbnail and Cache Records
echo ================================================
echo.

echo Choose cleanup method:
echo 1. PowerShell script (requires MongoDB .NET Driver)
echo 2. MongoDB shell script (requires MongoDB shell)
echo 3. Dry run with PowerShell (test only)
echo 4. Dry run with MongoDB shell (test only)
echo.

set /p choice="Enter your choice (1-4): "

if "%choice%"=="1" goto powershell
if "%choice%"=="2" goto mongoshell
if "%choice%"=="3" goto powershell_dry
if "%choice%"=="4" goto mongoshell_dry

echo Invalid choice. Please run the script again.
pause
exit /b 1

:powershell
echo.
echo Running PowerShell cleanup script...
powershell -ExecutionPolicy Bypass -File "cleanup-duplicate-records.ps1"
goto end

:powershell_dry
echo.
echo Running PowerShell cleanup script (DRY RUN)...
powershell -ExecutionPolicy Bypass -File "cleanup-duplicate-records.ps1" -DryRun
goto end

:mongoshell
echo.
echo Running MongoDB shell cleanup script...
echo Make sure MongoDB is running and accessible.
echo WARNING: This will modify your database!
echo.
set /p confirm="Are you sure you want to proceed? (yes/no): "
if /i "%confirm%"=="yes" (
    mongosh image_viewer cleanup-duplicate-records-mongo-shell.js
) else (
    echo Operation cancelled.
)
goto end

:mongoshell_dry
echo.
echo Running MongoDB shell cleanup script (DRY RUN)...
echo Make sure MongoDB is running and accessible.
echo This will show what would be deleted without making changes.
mongosh image_viewer --eval "var isDryRun = true;" cleanup-duplicate-records-mongo-shell.js
goto end

:end
echo.
echo Cleanup completed!
pause
