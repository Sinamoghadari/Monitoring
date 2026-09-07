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
set "REPORT_URL=http://172.17.214.38:8082/api/updates/report"
set "STATUS=SUCCESS"
set "EXITCODE=0"
set "DETAILS=update started"
set "ROBOCOPY_RC=0"
set "UPDATES_ROOT=%ProgramData%\Ergonomy\updates"
set "LOGDIR=%ProgramData%\Ergonomy\update-logs"

if not exist "%LOGDIR%\" mkdir "%LOGDIR%" >nul 2>&1

if "%VERSION%"=="" (
  set "LOGFILE=%LOGDIR%\update_exec_unknown.log"
) else (
  set "LOGFILE=%LOGDIR%\update_exec_%VERSION%.log"
)

echo [%DATE% %TIME%] apply_update start VERSION=%VERSION% SOURCE="%SOURCE%" TARGET="%TARGET%" PID=%PID% > "%LOGFILE%"
call :log Arguments: service="%SERVICE%" marker="%MARKER%" restart="%RESTART_EXE%"

if not exist "%SOURCE%\" (
  call :log Staged source directory is missing: "%SOURCE%"
  set "STATUS=SOURCE_MISSING"
  set "EXITCODE=2"
  set "DETAILS=Staged source directory is missing: %SOURCE%"
  call :report
  exit /b 2
)

REM Idempotency: if this exact version is already marked applied, skip the copy
REM and only ensure the process/service is running.
if exist "%MARKER%" (
  set /p APPLIED=<"%MARKER%"
  if /I "!APPLIED!"=="%VERSION%" (
    call :log Version %VERSION% already applied. Skipping copy.
    set "STATUS=ALREADY_APPLIED"
    set "EXITCODE=0"
    set "DETAILS=Version %VERSION% already applied; skipping copy and ensuring process is running."
    call :cleanup
    goto :restart
  )
)

:waitpid
if "%PID%"=="" goto :copy
if "%PID%"=="0" goto :copy
tasklist /FI "PID eq %PID%" 2>nul | findstr /R /C:" %PID% " >nul
if not errorlevel 1 (
  call :log Waiting for parent PID %PID% to exit so file locks are released.
  timeout /t 1 /nobreak >nul
  goto :waitpid
)

REM Settle so antivirus / SearchIndexer / previous FileStream handles drop.
call :log Parent PID exited. Settling 2s before copy.
timeout /t 2 /nobreak >nul

:copy
if not exist "%TARGET%\" mkdir "%TARGET%" >nul 2>&1

set /a ATTEMPT=0
:copylock
set /a ATTEMPT+=1
REM /E copy subdirs, /IS include same files, /IT include tweaked files.
REM robocopy: 0-7 are success, 8+ are failure. Retry while files stay locked.
call :log robocopy attempt !ATTEMPT!/15
robocopy "%SOURCE%" "%TARGET%" /E /IS /IT /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >> "%LOGFILE%" 2>&1
set "ROBOCOPY_RC=!ERRORLEVEL!"
call :log robocopy finished RC=!ROBOCOPY_RC! attempt=!ATTEMPT!
if !ROBOCOPY_RC! GEQ 8 (
  if !ATTEMPT! GEQ 15 (
    call :log Copy failed after !ATTEMPT! attempts. robocopy=!ROBOCOPY_RC!
    set "STATUS=FAILED_COPY"
    set "EXITCODE=3"
    set "DETAILS=robocopy failed after 15 attempts. robocopy_errorlevel=!ROBOCOPY_RC! source=%SOURCE% target=%TARGET%"
    call :report
    exit /b 3
  )
  call :log Target files still locked; retry !ATTEMPT!/15
  timeout /t 2 /nobreak >nul
  goto :copylock
)

if not "%MARKER%"=="" (
  for %%I in ("%MARKER%") do (
    if not exist "%%~dpI" mkdir "%%~dpI" >nul 2>&1
  )
  >"%MARKER%" echo %VERSION%
  call :log Applied version %VERSION%
)

set "STATUS=SUCCESS"
set "EXITCODE=0"
set "DETAILS=robocopy succeeded RC=!ROBOCOPY_RC! after !ATTEMPT! attempt(s). Version %VERSION% applied."
call :cleanup

:restart
if not "%SERVICE%"=="" (
  sc query "%SERVICE%" >nul 2>&1
  if not errorlevel 1 (
    net stop "%SERVICE%" /y >nul 2>&1
    net start "%SERVICE%"
    if not errorlevel 1 (
      call :log Service "%SERVICE%" restarted.
      set "DETAILS=!DETAILS! Service %SERVICE% restarted."
      call :report
      exit /b %EXITCODE%
    )
    call :log Service "%SERVICE%" failed to start. Falling back to executable relaunch.
    set "DETAILS=!DETAILS! Service %SERVICE% failed to start; falling back to exe."
  )
)

if not "%RESTART_EXE%"=="" (
  if exist "%RESTART_EXE%" (
    start "" "%RESTART_EXE%"
    call :log Relaunched "%RESTART_EXE%"
    set "DETAILS=!DETAILS! Relaunched %RESTART_EXE%."
  ) else (
    call :log Restart executable not found: "%RESTART_EXE%"
    set "DETAILS=!DETAILS! Restart executable not found: %RESTART_EXE%."
  )
)

call :report
exit /b %EXITCODE%

:usage
echo Usage: apply_update.bat sourceDir targetDir pid serviceName version markerPath restartExe
exit /b 1

REM -----------------------------------------------------------------------------
:log
echo [%DATE% %TIME%] %*
>>"%LOGFILE%" echo [%DATE% %TIME%] %*
goto :eof

REM -----------------------------------------------------------------------------
REM Delete staging archives and extracted payload folders. Keep applied_version.
:cleanup
call :log Cleaning staging artifacts under "%UPDATES_ROOT%"
if not exist "%UPDATES_ROOT%\" goto :eof
for /d %%D in ("%UPDATES_ROOT%\*") do (
  rd /s /q "%%D" >nul 2>&1
)
del /q "%UPDATES_ROOT%\*.zip" >nul 2>&1
del /q "%UPDATES_ROOT%\*.bin" >nul 2>&1
del /q "%UPDATES_ROOT%\*.partial" >nul 2>&1
goto :eof

REM -----------------------------------------------------------------------------
REM POST JSON report to FastAPI. Best-effort: curl failure does not change exit code.
:report
set "JSONFILE=%TEMP%\update_report_%VERSION%_%RANDOM%.json"
set "COLLECTED_AT="
set "WINSID="
set "WINUSER=%USERNAME%"
set "WINUSER_ADMIN=%USERDOMAIN%\%USERNAME%|Elevated=False"
net session >nul 2>&1
if not errorlevel 1 set "WINUSER_ADMIN=%USERDOMAIN%\%USERNAME%|Elevated=True"

for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command "[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')"`) do set "COLLECTED_AT=%%I"
for /f "usebackq tokens=2 delims=," %%I in (`whoami /user /fo csv /nh`) do set "WINSID=%%~I"

set "ESC_SOURCE=!SOURCE:\=\\!"
set "ESC_TARGET=!TARGET:\=\\!"
set "ESC_DETAILS=!DETAILS:\=\\!"
set "ESC_DETAILS=!ESC_DETAILS:"=\"!"
set "ESC_SOURCE=!ESC_SOURCE:"=\"!"
set "ESC_TARGET=!ESC_TARGET:"=\"!"

call :log Reporting status=!STATUS! exit_code=!EXITCODE! to !REPORT_URL!

(
  echo {
  echo   "computer_name": "%COMPUTERNAME%",
  echo   "version": "!VERSION!",
  echo   "status": "!STATUS!",
  echo   "exit_code": !EXITCODE!,
  echo   "source_dir": "!ESC_SOURCE!",
  echo   "target_dir": "!ESC_TARGET!",
  echo   "log_details": "!ESC_DETAILS!",
  echo   "CollectedAt": "!COLLECTED_AT!",
  echo   "ComputerName": "%COMPUTERNAME%",
  echo   "WindowsSid": "!WINSID!",
  echo   "WindowsUsername_RunAdmin": "!WINUSER_ADMIN!",
  echo   "WindowsUsername": "!WINUSER!"
  echo }
) > "!JSONFILE!"

curl.exe -s -X POST "!REPORT_URL!" -H "Content-Type: application/json; charset=utf-8" --data-binary "@!JSONFILE!" -o NUL 2>nul
del /q "!JSONFILE!" >nul 2>&1
goto :eof
