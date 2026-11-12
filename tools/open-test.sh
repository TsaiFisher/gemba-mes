#!/usr/bin/env bash
# 情境 2：開放測試 — Cloudflare Tunnel（quick tunnel）
#
# 原理：cloudflared 由 WSL 主動對外建立連線，取得一個 https://xxx.trycloudflare.com 網址。
#       TLS 由 Cloudflare 終結（憑證手機天生信任，不需安裝 CA），
#       並帶 X-Forwarded-Proto: https 回源 —— 已實測 app 的 UseForwardedHeaders 會正確改寫
#       scheme，Secure cookie 與 HttpsRedirection 均正常。
#       全程為出站連線，不需防火牆規則、不需管理員權限、app 留在 WSL（授權指紋不變）。
#
# ⚠️ 這個網址是「公開」的 —— 任何人取得即可存取 MES 登入頁，沒有額外保護。
#    測試結束請務必執行 ./open-test.sh stop。
#
# 用法： ./open-test.sh          啟動並印出網址
#        ./open-test.sh url      重印目前網址
#        ./open-test.sh stop     停止 tunnel（app 保留）
#        ./open-test.sh stop-all 停止 tunnel 與 app
set -uo pipefail

PORT=5110
PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$HOME/.cache/mes-mobile-test"           # log 與網址檔刻意留在 ~/.cache，不污染專案目錄
APP_LOG="$DIR/app.log"
CF_LOG="$DIR/cf.log"
URL_FILE="$DIR/current-url.txt"
# cloudflared 依序尋找：環境變數 > PATH > ~/.local/bin
CLOUDFLARED="${CLOUDFLARED:-$(command -v cloudflared 2>/dev/null || echo "$HOME/.local/bin/cloudflared")}"
mkdir -p "$DIR"

case "${1:-start}" in
  stop)
    pkill -f "cloudflared tunnel --url http://localhost:$PORT" && echo "tunnel 已停止" || echo "沒有執行中的 tunnel"
    rm -f "$URL_FILE"
    exit 0 ;;
  stop-all)
    pkill -f "cloudflared tunnel --url http://localhost:$PORT" 2>/dev/null
    pkill -f "dotnet run --urls http://localhost:$PORT" 2>/dev/null
    rm -f "$URL_FILE"
    echo "tunnel 與 app 皆已停止"
    exit 0 ;;
  url)
    [ -f "$URL_FILE" ] && cat "$URL_FILE" || echo "目前沒有執行中的 tunnel"
    exit 0 ;;
esac

if [ ! -x "$CLOUDFLARED" ]; then
    cat >&2 <<EOF
⚠ 找不到 cloudflared（$CLOUDFLARED）。安裝方式（單一執行檔，免 root）：

  mkdir -p ~/.local/bin
  curl -fsSL https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 \\
       -o ~/.local/bin/cloudflared && chmod +x ~/.local/bin/cloudflared

或以環境變數指定既有路徑：CLOUDFLARED=/path/to/cloudflared $0
EOF
    exit 1
fi

echo "── 1. 啟動應用程式 ─────────────────────────────"
if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"; then
    echo "   app 已在執行中，沿用既有進程"
else
    cd "$PROJ"
    setsid nohup dotnet run --urls "http://localhost:$PORT" > "$APP_LOG" 2>&1 < /dev/null &
    echo -n "   啟動中"
    until curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"; do echo -n "."; sleep 3; done
    echo " 就緒"
fi

echo
echo "── 2. 建立 Cloudflare Tunnel ───────────────────"
pkill -f "cloudflared tunnel --url http://localhost:$PORT" 2>/dev/null
: > "$CF_LOG"
setsid nohup "$CLOUDFLARED" tunnel --url "http://localhost:$PORT" > "$CF_LOG" 2>&1 < /dev/null &

echo -n "   等待網址"
for _ in $(seq 1 40); do
    if grep -qE "https://[a-z0-9-]+\.trycloudflare\.com" "$CF_LOG" 2>/dev/null; then break; fi
    echo -n "."; sleep 2
done
URL=$(grep -oE "https://[a-z0-9-]+\.trycloudflare\.com" "$CF_LOG" | head -1)
if [ -z "$URL" ]; then
    echo " 失敗，請查看 $CF_LOG"
    exit 1
fi
echo " 完成"
echo "$URL" > "$URL_FILE"

echo
echo "── 3. 端到端驗證 ───────────────────────────────"
for p in /mobile/index.html /mobile/sw.js /mobile/manifest.json; do
    printf "   %-24s HTTP %s\n" "$p" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$URL$p")"
done

cat <<EOF

════════════════════════════════════════════════
 手機開啟： $URL/mobile/index.html
════════════════════════════════════════════════

 ⚠️ 此網址公開可存取，測試結束請執行： $0 stop
 ⚠️ 每次重啟 tunnel 網址都會變 —— 網址即 PWA 的 origin，
    換網址等於換一個新 origin，Service Worker 與離線快取會重來。
    若要連續多天測同一個 PWA，請維持這個 tunnel 不要重啟。

 目前網址： $0 url
 停止　　： $0 stop / $0 stop-all
EOF
