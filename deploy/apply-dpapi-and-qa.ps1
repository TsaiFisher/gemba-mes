#Requires -RunAsAdministrator
<#
  一次完成兩件事（只重啟一次應用程式集區）：
    A. 套用 DataProtection DPAPI 修正（換 SmartFactory.MES.dll）並淘汰舊的明文金鑰
    B. 開啟手冊 Q&A（Features:QaAssistant）

  兩件都可個別關閉：-SkipDpapi / -SkipQa
#>
param(
    [string]$Site    = 'SmartFactoryMES',
    [string]$Pool    = 'SmartFactoryMES',
    [string]$Root    = 'C:\inetpub\SmartFactoryMES',
    [string]$KeysDir = 'C:\inetpub\SmartFactoryMES-keys',
    [string]$Src     = (Join-Path $PSScriptRoot 'hotfix-dpapi'),
    [int]   $HttpPort = 5110,
    [switch]$SkipDpapi,
    [switch]$SkipQa
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop

$stamp   = Get-Date -Format 'yyyyMMdd_HHmmss'
$backup  = Join-Path $Root "..\SmartFactoryMES-backup-$stamp"
$cfgPath = Join-Path $Root 'appsettings.Production.json'

function Say($m) { Write-Host $m }
function Ok ($m) { Write-Host "  V $m" -ForegroundColor Green }
function Bad($m) { Write-Host "  X $m" -ForegroundColor Red }

Say ''
Say '[0] 前置檢查'

if (-not $SkipDpapi) {
    foreach ($f in 'SmartFactory.MES.dll','SmartFactory.MES.pdb') {
        if (-not (Test-Path (Join-Path $Src $f))) { Bad "找不到 $Src\$f"; exit 1 }
    }
    Ok "hotfix 產物就緒"
}
if (-not (Test-Path $cfgPath)) { Bad "找不到 $cfgPath"; exit 1 }
Ok "組態檔存在"

# 備份：連線字串是加密的，這個檔弄丟就要重新加密一次
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item $cfgPath $backup -Force
if (Test-Path (Join-Path $Root 'license.json')) { Copy-Item (Join-Path $Root 'license.json') $backup -Force }
if (-not $SkipDpapi) { Copy-Item (Join-Path $Root 'SmartFactory.MES.dll') $backup -Force }
if (Test-Path $KeysDir) { Copy-Item $KeysDir (Join-Path $backup 'keys') -Recurse -Force -ErrorAction SilentlyContinue }
Ok "已備份到 $backup"

Say ''
Say '[1] 停止應用程式集區'
if ((Get-WebAppPoolState -Name $Pool).Value -ne 'Stopped') {
    Stop-WebAppPool -Name $Pool
    $n = 0
    while ((Get-WebAppPoolState -Name $Pool).Value -ne 'Stopped' -and $n -lt 30) { Start-Sleep -Seconds 1; $n++ }
}
if ((Get-WebAppPoolState -Name $Pool).Value -ne 'Stopped') { Bad "集區停不下來"; exit 1 }
Ok "已停止（w3wp 已釋放檔案鎖）"

if (-not $SkipDpapi) {
    Say ''
    Say '[2] 換組件'
    Copy-Item (Join-Path $Src 'SmartFactory.MES.dll') $Root -Force
    Copy-Item (Join-Path $Src 'SmartFactory.MES.pdb') $Root -Force
    Ok "SmartFactory.MES.dll / .pdb 已更新"

    Say ''
    Say '[3] 淘汰舊的明文金鑰'
    # 舊 key-*.xml 的 <value> 就是主金鑰明文。程式修好後「既有」金鑰不會自動被加密，
    # 必須刪掉讓它重新產生，明文才真的從磁碟上消失。
    # 代價：所有登入中的使用者被登出、待用的重設密碼／確認信連結失效。
    # 本專案 CreateProtector 使用點為 0，故不會毀掉任何持久化資料。
    $old = @(Get-ChildItem -Path $KeysDir -Filter 'key-*.xml' -ErrorAction SilentlyContinue)
    if ($old.Count -gt 0) {
        $old | Remove-Item -Force
        Ok "已刪除 $($old.Count) 個明文金鑰檔（副本在備份目錄）"
    } else {
        Ok "金鑰目錄本來就沒有 key-*.xml"
    }
}

if (-not $SkipQa) {
    Say ''
    Say '[4] 開啟手冊 Q&A'
    $j = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $qa = [pscustomobject]@{
        Enabled        = $true
        ChatModel      = 'gemma3:4b'
        EmbeddingModel = 'snowflake-arctic-embed2:latest'
    }
    if ($j.PSObject.Properties.Name -contains 'Features') {
        $j.Features | Add-Member -NotePropertyName QaAssistant -NotePropertyValue $qa -Force
    } else {
        $j | Add-Member -NotePropertyName Features -NotePropertyValue ([pscustomobject]@{ QaAssistant = $qa }) -Force
    }
    $j | ConvertTo-Json -Depth 20 | Set-Content $cfgPath -Encoding UTF8
    Ok "Features:QaAssistant 已寫入（gemma3:4b / snowflake-arctic-embed2:latest）"
}

Say ''
Say '[5] 啟動並等待就緒'
Start-WebAppPool -Name $Pool
$url = "http://localhost:$HttpPort/health"
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 2
    try {
        if ((Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $ready = $true; break }
    } catch { }
}
if ($ready) { Ok "/health 200（等待 $($i*2) 秒）" } else { Bad "/health 逾時，請看 logs\stdout_*.log"; }

Say ''
Say '[6] 驗收'
$logDir = Join-Path $Root 'logs'
$today  = Get-Date -Format 'yyyyMMdd'
$log    = Join-Path $logDir "smartfactory-$today.log"

if (-not $SkipDpapi) {
    Start-Sleep -Seconds 3
    $newKeys = @(Get-ChildItem -Path $KeysDir -Filter 'key-*.xml' -ErrorAction SilentlyContinue)
    if ($newKeys.Count -eq 0) {
        Bad "尚未產生新金鑰（第一次有人存取受保護資源時才會產生，可先開一次首頁再重跑這段）"
    } else {
        $enc = ($newKeys | ForEach-Object { Select-String -Path $_.FullName -Pattern 'encryptedSecret' -Quiet }) -contains $true
        if ($enc) { Ok "新金鑰含 <encryptedSecret>＝已由 DPAPI 加密" } else { Bad "新金鑰仍是明文，修正未生效" }
    }
    if (Test-Path $log) {
        $warn = Select-String -Path $log -Pattern 'No XML encryptor configured' -ErrorAction SilentlyContinue
        if ($warn) { Bad "仍有 'No XML encryptor configured' 警告＝跑的是舊組件" }
        else       { Ok "已無 'No XML encryptor configured' 警告" }
    }
}

if (-not $SkipQa) {
    Say ''
    Say '  Q&A 索引會在背景建立（約 6 分鐘，1100+ 筆嵌入）。'
    Say '  期間提問會得到不完整或空的答案，那不是壞掉。'
    Say '  進度用 WSL 端查：'
    Say '    PGPASSWORD=Dev12345 psql -h 127.0.0.1 -U dev -d ProjectZ -tAc "SELECT count(*), count(embedding) FROM \"DocChunks\";"'
    Say '  兩個數字相等才算補完。'
}

Say ''
Say "完成。備份：$backup"
Say ''
