@echo off
echo Starting FileServer...
start "FileServer" "%~dp0publish\FileServer.exe" --urls http://0.0.0.0:5000

echo Waiting for server to start...
timeout /t 6 /nobreak >nul

echo Starting Cloudflare Tunnel...
echo Public URL will appear below (https://xxxx.trycloudflare.com)
echo Press Ctrl+C to stop
echo.
"C:\Program Files (x86)\cloudflared\cloudflared.exe" tunnel --url http://localhost:5000
pause
