@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM =============================================================================
REM Ergonomy apply_update.bat
REM
REM Handoff script launched by UpdateManager AFTER every file handle in the
REM agent process has been disposed. The parent process must exit so Windows
REM releases locks on the target binaries.
REM
REM Arguments:
REM   %1 sourceDir     staged payload directory (already SHA256-verified)
REM   %2 targetDir     install directory to replace
REM   %3 pid           parent agent PID to wait on (file-lock release)
REM   %4 serviceName   optional Windows service name (Ergonomy.Service)
REM   %5 version       semantic version being applied (idempotency key)
REM   %6 markerPath    file that records the last successfully applied version
REM   %7 restartExe    optional full path of the interactive executable to relaunch
REM
REM Exit codes:
REM   0 success (or already applied)
REM   1 usage error
REM   2 staged source missing
REM   3 copy failed after retries
REM =============================================================================

if "%~1"=="" goto :usage
if "%~2"=="" goto :usage

set "SOURCE=%~1"
set "TARGET=%~2"
set "PID=%~3"
set "SERVICE=%~4"
set "VERSION=%~5"
set "MARKER=%~6"
set "RESTART_EXE=%~7"

if not exist "%SOURCE%\" (
  echo [%DATE% %TIME%] Staged source directory is missing: "%SOURCE%"
  exit /b 2
)

REM Idempotency: if this exact version is already marked applied, skip the copy
REM and only ensure the process/service is running.
if exist "%MARKER%" (
  set /p APPLIED=<"%MARKER%"
  if /I "!APPLIED!"=="%VERSION%" (
    echo [%DATE% %TIME%] Version %VERSION% already applied. Skipping copy.
    goto :restart
  )
)

:waitpid
if "%PID%"=="" goto :copy
if "%PID%"=="0" goto :copy
tasklist /FI "PID eq %PID%" 2>nul | findstr /R /C:" %PID% " >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto :waitpid
)

REM Settle so antivirus / SearchIndexer / previous FileStream handles drop.
timeout /t 2 /nobreak >nul

:copy
if not exist "%TARGET%\" mkdir "%TARGET%" >nul 2>&1

set /a ATTEMPT=0
:copylock
set /a ATTEMPT+=1
REM /E copy subdirs, /IS include same files, /IT include tweaked files.
REM robocopy: 0-7 are success, 8+ are failure. Retry while files stay locked.
robocopy "%SOURCE%" "%TARGET%" /E /IS /IT /R:3 /W:2 /NFL /NDL /NJH /NJS /NP
set "RC=%ERRORLEVEL%"
if %RC% GEQ 8 (
  if %ATTEMPT% GEQ 15 (
    echo [%DATE% %TIME%] Copy failed after %ATTEMPT% attempts. robocopy=%RC%
    exit /b 3
  )
  echo [%DATE% %TIME%] Target files still locked; retry %ATTEMPT%/15
  timeout /t 2 /nobreak >nul
  goto :copylock
)

if not "%MARKER%"=="" (
  for %%I in ("%MARKER%") do (
    if not exist "%%~dpI" mkdir "%%~dpI" >nul 2>&1
  )
  >"%MARKER%" echo %VERSION%
  echo [%DATE% %TIME%] Applied version %VERSION%
)

:restart
if not "%SERVICE%"=="" (
  sc query "%SERVICE%" >nul 2>&1
  if not errorlevel 1 (
    net stop "%SERVICE%" /y >nul 2>&1
    net start "%SERVICE%"
    if not errorlevel 1 (
      echo [%DATE% %TIME%] Service "%SERVICE%" restarted.
      goto :eof
    )
  )
)

if not "%RESTART_EXE%"=="" (
  if exist "%RESTART_EXE%" (
    start "" "%RESTART_EXE%"
    echo [%DATE% %TIME%] Relaunched "%RESTART_EXE%"
  )
)
goto :eof

:usage
echo Usage: apply_update.bat sourceDir targetDir pid serviceName version markerPath restartExe
exit /b 1
