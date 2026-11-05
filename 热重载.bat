@echo off
rem 进程内换核——只改业务核心（Web / Store / Sites / Browser / Collect / Config）时用。
rem 改宿主 exe 或契约（IShellCore）要走 部署.ps1（进程重启）。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0热重载.ps1"
pause
