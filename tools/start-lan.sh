#!/usr/bin/env bash
# 一鍵啟動 LAN 手機測試環境（換任何網路環境都跑這一支）
#
# 做三件事：
#   1. 偵測目前對外 IP（換 Wi-Fi、改連手機熱點後 IP 會變）
#   2. 若是沒見過的 IP，用同一個本機 CA 重簽伺服器憑證，並把歷來用過的 IP 全部簽進 SAN
#      → CA 不變，所以手機安裝過一次就永久有效，換網段不必重裝
#   3. 以 http + https 啟動 app，印出手機要開的網址
#
# 前提（已完成，換網段不需重做）：
#   • WSL networkingMode=mirrored
#   • Hyper-V 防火牆規則 MES-Mobile-5110（TCP 5110,7054）— 綁 port 不綁 IP
#   • 手機已安裝 rootCA.pem
#
# 用法： ./start-lan.sh          偵測 IP、更新憑證、啟動
#        ./start-lan.sh stop     停止 app
set -uo pipefail

PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERTS="$HOME/.cache/mes-certs"
IPFILE="$CERTS/known-ips.txt"
HTTP_PORT=5110
HTTPS_PORT=7054
LOG="$HOME/.cache/mes-mobile-test/app.log"
mkdir -p "$(dirname "$LOG")" "$CERTS"

# 在腳本內用 pkill 是安全的：本腳本自己的命令列不含這個字串
if [ "${1:-}" = "stop" ]; then
    pkill -f "dotnet run --urls" && echo "app 已停止" || echo "app 未在執行"
    exit 0
fi

# ── 1. 偵測目前對外 IP ──────────────────────────────────────
DEV=$(ip route show default 2>/dev/null | awk '{print $5; exit}')
if [ -n "$DEV" ]; then
    IP=$(ip -4 -o addr show dev "$DEV" scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1)
fi
# 無預設路由（純區域網段、無對外）時的 fallback
[ -z "${IP:-}" ] && IP=$(ip -4 -o addr show scope global 2>/dev/null | grep -v " lo " | awk '{print $4}' | cut -d/ -f1 | head -1)

if [ -z "${IP:-}" ]; then
    echo "⚠ 找不到任何對外 IP —— 確認已連上 Wi-Fi 或手機熱點" >&2
    exit 1
fi
echo "── 目前 IP：$IP  (介面 ${DEV:-未知})"

# ── 2. 憑證：累積所有用過的 IP，一起簽進 SAN ────────────────
touch "$IPFILE"
RESIGN=""
grep -qxF "$IP" "$IPFILE" || { echo "$IP" >> "$IPFILE"; RESIGN="新 IP"; }

# 效期只有 397 天（Chrome 上限 398），到期前 30 天自動換發，
# 否則 IP 沒變就不重簽的邏輯會讓憑證默默過期、https 全掛
if [ ! -f "$CERTS/server.crt" ]; then
    RESIGN="憑證不存在"
elif ! openssl x509 -in "$CERTS/server.crt" -noout -checkend 2592000 >/dev/null 2>&1; then
    RESIGN="${RESIGN:+$RESIGN／}30 天內到期"
fi

if [ -z "$RESIGN" ]; then
    echo "── 憑證已涵蓋此 IP 且效期充足，沿用"
else
    ALL_IPS=$(sort -u "$IPFILE" | tr '\n' ' ')
    echo "── 重簽憑證（原因：$RESIGN；涵蓋：$ALL_IPS）"
    # shellcheck disable=SC2086
    "$PROJ/tools/make-dev-cert.sh" $ALL_IPS >/dev/null 2>&1 || { echo "⚠ 憑證簽發失敗" >&2; exit 1; }
    echo "   ✓ 完成（CA 未變更 → 手機不需重裝）"
fi

# ── 3. 啟動 app ─────────────────────────────────────────────
pkill -f "dotnet run --urls" 2>/dev/null
sleep 2
# 保留上一次的 log —— 否則每次重啟就洗掉，連「手機到底有沒有打進來」都查不到
[ -f "$LOG" ] && mv -f "$LOG" "$LOG.prev"
cd "$PROJ"
ASPNETCORE_Kestrel__Certificates__Default__Path="$CERTS/mes-dev.pfx" \
ASPNETCORE_Kestrel__Certificates__Default__Password=changeit \
setsid nohup dotnet run --urls "http://0.0.0.0:$HTTP_PORT;https://0.0.0.0:$HTTPS_PORT" \
    > "$LOG" 2>&1 < /dev/null &

echo -n "── 啟動中"
for _ in $(seq 1 60); do
    ss -tln 2>/dev/null | grep -q ":$HTTPS_PORT" && break
    echo -n "."; sleep 3
done
echo

if ! ss -tln 2>/dev/null | grep -q ":$HTTPS_PORT"; then
    echo "⚠ 啟動失敗，請看 $LOG" >&2
    exit 1
fi

curl -s -o /dev/null -w "   自我驗證 https → HTTP %{http_code}\n" \
     --cacert "$CERTS/rootCA.pem" --max-time 10 \
     "https://$IP:$HTTPS_PORT/mobile/index.html"

cat <<EOF

════════════════════════════════════════════════════════════
 手機開啟： https://$IP:$HTTPS_PORT/mobile/index.html
════════════════════════════════════════════════════════════

 首次使用的手機需先安裝 CA（只需一次，之後換網段都不用再裝）：
     http://$IP:$HTTP_PORT/mes-dev-ca.crt
     → 會重導到 https 並顯示憑證警告（此時尚未信任，屬預期）
     → 進階 › 繼續前往 › 命名憑證 › 用途選「VPN 和應用程式」

 停止： $0 stop
 log ： $LOG
EOF
