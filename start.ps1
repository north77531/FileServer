# 啟動 FileServer
# 如需修改共享資料夾，請編輯 appsettings.json 中的 SharedFolder

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$project = "$PSScriptRoot\FileServer.csproj"

Write-Host "啟動 FileServer..." -ForegroundColor Green
Write-Host "按 Ctrl+C 停止" -ForegroundColor Yellow
Write-Host ""

& $dotnet run --project $project --urls "http://0.0.0.0:5000"
