#Requires -RunAsAdministrator
<#
.SYNOPSIS
    把 ProjectZ 的發布產物架成一個 IIS 站台。對應 DEVELOPMENT.md 3.6 與
    deploy/iis-deployment.md。本腳本只做「需要管理員權限」的部分。

.DESCRIPTION
    冪等：可重複執行，會把同名站台與集區整個重建。
    預設從腳本所在目錄讀發布包（app\、mes-dev.pfx、rootCA.cer、
    appsettings.Production.json.template），所以把本檔複製到發布包根目錄再跑最省事。

.EXAMPLE
    Start-Process powershell -Verb RunAs -ArgumentList `
        '-ExecutionPolicy Bypass -NoExit -File C:\path\to\iis-setup.ps1'
#>
param(
    [string]$Stage     = $PSScriptRoot,
    [string]$Site      = 'SmartFactoryMES',
    [string]$Pool      = 'SmartFactoryMES',
    [string]$Root      = 'C:\inetpub\SmartFactoryMES',
    [string]$KeysDir   = 'C:\inetpub\SmartFactoryMES-keys',
    [string]$AnchorDir = 'C:\ProgramData\SmartFactory',
    [int]   $HttpPort  = 5110,
    # 7054 而非 443：443 通常已被別的站台佔用（測試筆電上是舊版 Web Forms 的 ProjectZ）
    [int]   $HttpsPort = 7054,
    [string]$PfxPass   = 'changeit'
)

$ErrorActionPreference = 'Stop'

Import-Module WebAdministration

function Step($n, $m) { Write-Host "`n[$n] $m" -ForegroundColor Cyan }

# ── 0. 前置檢查 ───────────────────────────────────────────────
Step 0 '前置檢查'

$ancm = 'C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $ancm)) {
    Write-Host '  X 找不到 ASP.NET Core Module V2（IIS 託管 ASP.NET Core 的必要模組）' -ForegroundColor Red
    Write-Host ''

    # 分辨兩種容易混淆的狀態：裝了 runtime vs 裝了 Hosting Bundle
    $hosting = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
                                -ErrorAction SilentlyContinue |
               Where-Object { $_.DisplayName -match 'Windows Server Hosting' }
    $shared  = Test-Path 'C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App'
    $express = Test-Path 'C:\Program Files\IIS Express\Asp.Net Core Module\V2\aspnetcorev2.dll'

    Write-Host '    診斷：'
    Write-Host ("      Windows Server Hosting（＝Hosting Bundle）: " + $(if ($hosting) { '已安裝' } else { '未安裝  <── 就是缺這個' }))
    Write-Host ("      Microsoft.AspNetCore.App 共用框架         : " + $(if ($shared)  { '已安裝（但它不含 ANCM）' } else { '未安裝' }))
    Write-Host ("      IIS Express 版的 ANCM                     : " + $(if ($express) { '已安裝（IIS Express 專用，IIS 用不到）' } else { '未安裝' }))
    Write-Host ''
    Write-Host '    注意：dotnet --list-runtimes 看得到 Microsoft.AspNetCore.App 10.0.x'
    Write-Host '          ＝／＝ 可以用 IIS 託管。那是共用框架，不含 ANCM。'
    Write-Host '          下載頁上要點的是「Hosting Bundle」，不是「Runtime」也不是「SDK」。'
    Write-Host '          正確檔名 dotnet-hosting-10.0.x-win.exe（約 112 MB；'
    Write-Host '          純 runtime 只有 ~11 MB，用檔案大小就分得出來）。'
    Write-Host ''
    $local = Join-Path $PSScriptRoot 'dotnet-hosting-10.0.14-win.exe'
    if (Test-Path $local) {
        Write-Host "    已幫你下載好：$local" -ForegroundColor Yellow
        Write-Host '    安裝後執行 iisreset，再重跑本腳本。'
    } else {
        Write-Host '    下載：https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    exit 1
}
Write-Host '  V ANCM V2 已安裝'

if (-not (Test-Path "$Stage\app\SmartFactory.MES.dll")) {
    Write-Host "  X 找不到 $Stage\app\SmartFactory.MES.dll" -ForegroundColor Red; exit 1
}
Write-Host '  V 發布產物就緒'

# 只看「別人的」站台——本站台自己的繫結不算衝突，否則重跑會被上一次的結果擋住
foreach ($p in @($HttpPort, $HttpsPort)) {
    $conflict = Get-Website | Where-Object { $_.Name -ne $Site } | Where-Object {
        @($_.bindings.Collection | Where-Object { $_.bindingInformation -match ":${p}:" }).Count -gt 0
    }
    if ($conflict) {
        Write-Host ("  X 連接埠 $p 已被站台 '" + ($conflict.Name -join ', ') + "' 佔用") -ForegroundColor Red
        exit 1
    }
}
Write-Host "  V 連接埠 $HttpPort / $HttpsPort 未被別的站台佔用"

if (Test-Path "IIS:\Sites\$Site") {
    Write-Host "  ! 已存在站台 $Site，將整個重建（設定會重設為本腳本的版本）" -ForegroundColor Yellow
}
Write-Host '  ! 既有的 ProjectZ 站台（E:\Data\PZ，舊版 Web Forms，佔 80/88/443）不會被本腳本更動' -ForegroundColor Yellow

# ── 1. 佈署檔案 ───────────────────────────────────────────────
Step 1 "複製檔案到 $Root"
New-Item -ItemType Directory -Force -Path $Root, $KeysDir, $AnchorDir | Out-Null
Copy-Item "$Stage\app\*" $Root -Recurse -Force
Copy-Item "$Stage\appsettings.Production.json.template" "$Root\appsettings.Production.json.template" -Force
Write-Host '  V 已複製'

# ── 2. 應用程式集區 ───────────────────────────────────────────
Step 2 "建立應用程式集區 $Pool"
# 順序不可顛倒：集區仍被站台引用時移除會失敗
if (Test-Path "IIS:\Sites\$Site")    { Remove-Website $Site }
if (Test-Path "IIS:\AppPools\$Pool") { Remove-WebAppPool $Pool }
New-WebAppPool $Pool | Out-Null
Set-ItemProperty "IIS:\AppPools\$Pool" -Name managedRuntimeVersion -Value ''        # 沒有受控碼
Set-ItemProperty "IIS:\AppPools\$Pool" -Name startMode              -Value 'AlwaysRunning'
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$Pool" -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Write-Host '  V CLR=沒有受控碼 / AlwaysRunning / idleTimeout=0 / 停用 29 小時定期回收'
Write-Host '    （這四項是守門：漏掉的話 13 支 AddHostedService 會被靜默停掉，排程驗收全是假綠）' -ForegroundColor Yellow

# ── 3. 站台 ───────────────────────────────────────────────────
Step 3 "建立站台 $Site"
New-Website -Name $Site -PhysicalPath $Root -ApplicationPool $Pool -Port $HttpPort | Out-Null
Set-ItemProperty "IIS:\Sites\$Site" -Name applicationDefaults.preloadEnabled -Value $true
Write-Host "  V http://*:$HttpPort（站台根目錄，非虛擬路徑子應用程式）"
Write-Host '  V preloadEnabled=True'

# ── 4. 憑證與 HTTPS ───────────────────────────────────────────
Step 4 '匯入憑證並建立 HTTPS 繫結'
Import-Certificate -FilePath "$Stage\rootCA.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host '  V 本機 CA 已匯入信任根（手機端已裝過同一張，不必重裝）'

$pw   = ConvertTo-SecureString $PfxPass -AsPlainText -Force
$cert = Import-PfxCertificate -FilePath "$Stage\mes-dev.pfx" -CertStoreLocation Cert:\LocalMachine\My -Password $pw
Write-Host "  V 伺服器憑證 thumbprint $($cert.Thumbprint)"

New-WebBinding -Name $Site -Protocol https -Port $HttpsPort -IPAddress '*' | Out-Null
if (Test-Path "IIS:\SslBindings\0.0.0.0!$HttpsPort") { Remove-Item "IIS:\SslBindings\0.0.0.0!$HttpsPort" }
New-Item "IIS:\SslBindings\0.0.0.0!$HttpsPort" -Value $cert | Out-Null
Write-Host "  V https://*:$HttpsPort"

# ── 5. 權限 ───────────────────────────────────────────────────
Step 5 '設定應用程式集區身分的寫入權限'
$id = "IIS AppPool\$Pool"
foreach ($d in @("$Root\logs", "$Root\App_Data", "$Root\wwwroot\uploads", $KeysDir)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    icacls $d /grant "${id}:(OI)(CI)M" /T /Q | Out-Null
}
icacls $AnchorDir /grant "${id}:(OI)(CI)R" /T /Q | Out-Null
Write-Host '  V logs / App_Data / wwwroot\uploads / DataProtection 金鑰目錄 = 可寫'
Write-Host '  V ProgramData\SmartFactory = 可讀（錨定檔）'

# ── 6. 防火牆 ─────────────────────────────────────────────────
Step 6 '防火牆規則（手機連得進來）'
Remove-NetFirewallRule -DisplayName 'SmartFactoryMES-IIS' -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName 'SmartFactoryMES-IIS' -Direction Inbound -Protocol TCP `
    -LocalPort $HttpPort,$HttpsPort -Action Allow -Profile Any | Out-Null
Write-Host "  V 已放行 TCP $HttpPort, $HttpsPort"

# ── 完成 ──────────────────────────────────────────────────────
Write-Host "`n─────────────────────────────────────────────" -ForegroundColor Green
Write-Host '站台已建立。接下來還有三件事，站台現在還起不來：' -ForegroundColor Green
Write-Host ''
Write-Host '  1. 連線字串'
Write-Host "     把 $Root\appsettings.Production.json.template"
Write-Host "     改名為 appsettings.Production.json 並填入 PG 密碼。"
Write-Host '     （WSL 要先起來，否則連不到 5432）'
Write-Host ''
Write-Host '  2. 錨定碼 → 指紋'
Write-Host "     $Stage\SmartFactory.MES.MachineId.exe --init"
Write-Host "     $Stage\SmartFactory.MES.MachineId.exe --show"
Write-Host '     把 64 字元指紋回報，由簽發端核發帶 --allow-plain 的測試授權。'
Write-Host ''
Write-Host '  3. 授權檔'
Write-Host "     把核發的 license.json 放到 $Root\"
Write-Host ''
Write-Host '  完成後：'
Write-Host "     http://localhost:$HttpPort/health"
$lanIp = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } |
          Select-Object -First 1).IPv4Address.IPAddress
Write-Host "     https://${lanIp}:$HttpsPort/mobile/index.html   <-- 手機掃碼測試"
Write-Host "     （該 IP 若不在憑證 SAN 內，手機會擋。重簽：tools/make-dev-cert.sh <新IP> <舊IP...>）"
Write-Host '─────────────────────────────────────────────' -ForegroundColor Green
