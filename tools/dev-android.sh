#!/usr/bin/env bash
# 情境 1：開發測試 — Android 手機 + Chrome 埠轉發（A2 方案）
#
# 原理：Chrome DevTools 的 Port forwarding 用 Chrome 內建的 ADB，把
#       「手機 Chrome 的 localhost:5110」導到「Windows 的 127.0.0.1:5110」，
#       而 WSL 的 5110 由 localhostForwarding 自動鏡射到 Windows 127.0.0.1:5110。
#
#       手機網址因此是 http://localhost:5110 —— localhost 本身即 secure context，
#       Service Worker / 相機掃碼 / 推播 / Secure cookie 全部可用，
#       不需要憑證、不碰防火牆、不需要管理員權限。
#
# 用法： ./dev-android.sh          起 app 並印出操作步驟
#        ./dev-android.sh stop     停止 app
set -uo pipefail

PORT=5110
PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$HOME/.cache/mes-mobile-test/app.log"   # log 刻意留在 ~/.cache，不污染專案目錄
mkdir -p "$(dirname "$LOG")"

# adb 依序尋找：環境變數 ADB > PATH > Windows 使用者目錄下的 platform-tools。
# 不可寫死 /mnt/c/Users/<某人>/，Windows 使用者名稱各機不同。
if [ -z "${ADB:-}" ]; then
    if command -v adb.exe >/dev/null 2>&1; then
        ADB="$(command -v adb.exe)"
    else
        WIN_HOME=$(powershell.exe -NoProfile -Command 'Write-Output $env:USERPROFILE' 2>/dev/null | tr -d '\r')
        [ -n "$WIN_HOME" ] && WIN_HOME=$(wslpath "$WIN_HOME" 2>/dev/null)
        ADB="${WIN_HOME:-/mnt/c/Users/$USER}/platform-tools/adb.exe"
    fi
fi

if [ "${1:-}" = "stop" ]; then
    pkill -f "dotnet run --urls http://localhost:$PORT" && echo "app 已停止" || echo "app 未在執行"
    exit 0
fi

# Chrome 用自己內建的 ADB，外部 adb server 佔住 USB 裝置時 chrome://inspect 會看不到手機
if [ -x "$ADB" ] && timeout 10 "$ADB" devices >/dev/null 2>&1; then
    timeout 10 "$ADB" kill-server >/dev/null 2>&1
    echo "已停止外部 adb server（避免與 Chrome 內建 ADB 相衝）"
fi

echo "── 1. 啟動應用程式 ─────────────────────────────"
if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"; then
    echo "   app 已在執行中，沿用既有進程"
else
    cd "$PROJ"
    setsid nohup dotnet run --urls "http://localhost:$PORT" > "$LOG" 2>&1 < /dev/null &
    echo -n "   啟動中"
    until curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"; do echo -n "."; sleep 3; done
    echo " 就緒（log: $LOG）"
fi

echo
echo "── 2. 檢查 Chrome 埠轉發的目標是否可達 ─────────"
# Chrome 跑在 Windows，它解析的 localhost:5110 是 Windows 的 127.0.0.1:5110
WIN_OK=$(timeout 20 powershell.exe -NoProfile -Command "
try { \$c = New-Object Net.Sockets.TcpClient; \$c.Connect('127.0.0.1',$PORT); 'OK'; \$c.Close() } catch { 'FAIL' }
" 2>/dev/null | tr -d '\r\n')
if [ "$WIN_OK" = "OK" ]; then
    echo "   Windows 127.0.0.1:$PORT → 可達（WSL localhostForwarding 正常）"
else
    echo "   ⚠ Windows 127.0.0.1:$PORT 不可達，Chrome 埠轉發會失敗"
    echo "     檢查 %USERPROFILE%\\.wslconfig 是否關閉了 localhostForwarding"
fi

cat <<EOF

════════════════════════════════════════════════════════════
 接下來在 Windows 端操作（一次設定，之後每天沿用）
════════════════════════════════════════════════════════════

 手機端：
   1. 設定 › 關於手機 › 連點「版本號」7 次 → 開啟開發者選項
   2. 開發者選項 › 開啟「USB 偵錯」
   3. USB 接上電腦，連線模式選「檔案傳輸 / MTP」（不要選「僅充電」）
   4. 手機跳出「允許 USB 偵錯嗎」→ 允許（可勾選一律允許）

 Windows Chrome：
   5. 網址列輸入   chrome://inspect/#devices
   6. 勾選         Discover USB devices        ← 應看到你的手機型號
   7. 點           Port forwarding...
   8. 勾選         Enable port forwarding
   9. 新增一列     Device port : $PORT
                   Local address: localhost:$PORT
  10. 按 Done（Chrome 桌面端必須保持開啟，關掉轉發就斷）

 手機 Chrome 開啟：
       http://localhost:$PORT/mobile/index.html

 偵錯：chrome://inspect 的手機分頁旁按 inspect，可直接看手機的
       console / network / Application › Service Workers

────────────────────────────────────────────────────────────
 A2 的邊界（需要時再改用 adb reverse）：
   • 只在手機「Chrome 分頁」內生效
   • PWA 用「加到主畫面」安裝後的獨立視窗不走此轉發 → 裝機後行為測不到
   • 拔線或關閉桌面 Chrome 即中斷
────────────────────────────────────────────────────────────

 停止 app： $0 stop
EOF
