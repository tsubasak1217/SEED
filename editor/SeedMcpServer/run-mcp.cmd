@echo off
rem ============================================================================
rem  run-mcp.cmd - SEED editor MCP server launcher (shadow-copy mode)
rem
rem  WHY:
rem  Launching bin\Debug\net9.0\SeedMcpServer.exe directly from .mcp.json keeps
rem  that exe locked for as long as Claude Code holds the MCP connection, so
rem  building the editor (which also builds SeedMcpServer) fails with
rem  "the file is being used by another process".
rem  This wrapper copies the build output to a temp folder on every start and
rem  runs the copy; the build output itself is never locked.
rem
rem  WHERE:
rem  %LOCALAPPDATA%\SEED\mcp\<random>\  (removed on exit)
rem  stdout is reserved for JSON-RPC: this script never writes to stdout.
rem  (ASCII only: cmd.exe mis-parses UTF-8 comments.)
rem ============================================================================
setlocal
set "SRC=%~dp0bin\Debug\net9.0"
if not exist "%SRC%\SeedMcpServer.exe" (
  echo [run-mcp] build output not found: %SRC% 1>&2
  echo [run-mcp] run: dotnet build editor\SeedMcpServer\SeedMcpServer.csproj 1>&2
  exit /b 78
)

set "DST=%LOCALAPPDATA%\SEED\mcp\%RANDOM%%RANDOM%"
mkdir "%DST%" >nul 2>&1
xcopy "%SRC%\*" "%DST%\" /E /Y /Q >nul 2>&1
if errorlevel 1 (
  echo [run-mcp] copy failed: %SRC% to %DST% 1>&2
  exit /b 78
)

rem Pass the original build-output folder so Launcher can find SEEDEditor.exe
rem relative to it (the temp copy has no such neighbours).
set "SEED_MCP_SOURCE_DIR=%SRC%"
"%DST%\SeedMcpServer.exe" %*
set "CODE=%ERRORLEVEL%"

rmdir /S /Q "%DST%" >nul 2>&1
exit /b %CODE%
