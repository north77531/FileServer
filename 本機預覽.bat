@echo off
cd /d "%~dp0"
echo 啟動本機預覽伺服器...
echo 請開啟瀏覽器前往 http://localhost:5000/stocks
echo 按 Ctrl+C 停止
echo.
dotnet run --urls "http://localhost:5000"
pause
