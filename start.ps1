$exe         = "$PSScriptRoot\publish\FileServer.exe"
$cloudflared = "C:\Program Files (x86)\cloudflared\cloudflared.exe"
$port        = 5000

Write-Host "Starting FileServer..." -ForegroundColor Cyan

Start-Process -FilePath $exe -ArgumentList "--urls", "http://0.0.0.0:$port" -WorkingDirectory "$PSScriptRoot\publish"

# Wait until server actually responds (up to 30 seconds)
Write-Host "Waiting for server to respond..." -ForegroundColor Yellow
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$port" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        $ready = $true
        break
    } catch {}
}

if (-not $ready) {
    Write-Host "Server did not start. Please check if FileServer.exe is working." -ForegroundColor Red
    pause
    exit 1
}

Write-Host "Server is up!" -ForegroundColor Green
Write-Host ""
Write-Host "Starting Cloudflare Tunnel..." -ForegroundColor Yellow
Write-Host "Public URL will appear below (https://xxxx.trycloudflare.com)" -ForegroundColor Green
Write-Host "Press Ctrl+C to stop" -ForegroundColor Gray
Write-Host ""

& $cloudflared tunnel --url "http://localhost:$port"
