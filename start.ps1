# 啟動 FileServer + Cloudflare Tunnel
# 如需修改共享資料夾，請編輯 appsettings.json 中的 SharedFolder

$dotnet      = "C:\Program Files\dotnet\dotnet.exe"
$cloudflared = "C:\Program Files (x86)\cloudflared\cloudflared.exe"
$project     = "$PSScriptRoot\FileServer.csproj"
$port        = 5000

Write-Host "=============================" -ForegroundColor Cyan
Write-Host " FileServer 啟動中..." -ForegroundColor Cyan
Write-Host "=============================" -ForegroundColor Cyan
Write-Host ""

# 背景啟動 ASP.NET Core
$serverJob = Start-Job -ScriptBlock {
    param($dotnet, $project, $port)
    & $dotnet run --project $project --urls "http://0.0.0.0:$port"
} -ArgumentList $dotnet, $project, $port

Write-Host "等待伺服器啟動..." -ForegroundColor Yellow
Start-Sleep -Seconds 4

# 啟動 Cloudflare Quick Tunnel（會顯示公開網址）
Write-Host ""
Write-Host "Cloudflare Tunnel 啟動中，請稍候..." -ForegroundColor Yellow
Write-Host "公開網址會顯示在下方（格式：https://xxxx.trycloudflare.com）" -ForegroundColor Green
Write-Host "按 Ctrl+C 可停止所有服務" -ForegroundColor Gray
Write-Host ""

try {
    & $cloudflared tunnel --url "http://localhost:$port"
} finally {
    Write-Host ""
    Write-Host "正在停止伺服器..." -ForegroundColor Yellow
    Stop-Job $serverJob -ErrorAction SilentlyContinue
    Remove-Job $serverJob -ErrorAction SilentlyContinue
    Write-Host "已停止。" -ForegroundColor Gray
}
