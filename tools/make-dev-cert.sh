#!/usr/bin/env bash
# 本機測試憑證產生器（mkcert 的 openssl 手工版，不需在 Windows 安裝任何東西）
#
# 與 dotnet dev-certs 的差異：
#   dev-certs → SAN 只有 DNS:localhost、CA:FALSE，無法裝進手機、無法涵蓋 IP
#   本腳本    → 先建一個真正的本機 CA（CA:TRUE），再簽發含指定 IP/主機名 SAN 的伺服器憑證
#
# 用法：
#   ./make-dev-cert.sh 192.168.137.1                 # Win11 行動熱點的固定位址
#   ./make-dev-cert.sh 192.168.137.1 10.16.16.28     # 多個位址一起簽進去
set -euo pipefail

OUT="${OUT_DIR:-$HOME/.cache/mes-certs}"
PFX_PASS="${PFX_PASS:-changeit}"
mkdir -p "$OUT"; cd "$OUT"

HOSTS=("$@")
if [ ${#HOSTS[@]} -eq 0 ]; then
    echo "用法: $0 <IP或主機名> [更多...]" >&2
    exit 1
fi

# 1) 本機 CA — 只在第一次建立，之後重複使用（手機只需安裝這一張，一次）
if [ ! -f rootCA.pem ]; then
    openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
        -keyout rootCA.key -out rootCA.pem \
        -subj "/CN=SmartFactory MES Dev CA" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
    echo "[+] 已建立本機 CA：$OUT/rootCA.pem"
else
    echo "[=] 沿用既有 CA：$OUT/rootCA.pem"
fi

# 2) 組裝 SAN：純數字 IP 用 IP:，其餘用 DNS:
SAN=""
for h in "${HOSTS[@]}"; do
    if [[ "$h" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        SAN+="IP:$h,"
    else
        SAN+="DNS:$h,"
    fi
done
SAN+="DNS:localhost,IP:127.0.0.1"

# 3) 伺服器憑證。days 必須 <= 398，否則 Chrome 直接拒絕（ERR_CERT_VALIDITY_TOO_LONG）
openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr \
    -subj "/CN=${HOSTS[0]}" 2>/dev/null

openssl x509 -req -in server.csr -CA rootCA.pem -CAkey rootCA.key -CAcreateserial \
    -out server.crt -days 397 -sha256 \
    -extfile <(printf "subjectAltName=%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n" "$SAN") 2>/dev/null

# 4) 打包成 Kestrel 吃的 pfx
openssl pkcs12 -export -out mes-dev.pfx -inkey server.key -in server.crt \
    -certfile rootCA.pem -passout "pass:$PFX_PASS"

echo
echo "[+] 憑證：$OUT/mes-dev.pfx（密碼 $PFX_PASS）"
echo "[+] 手機要安裝的 CA：$OUT/rootCA.pem"
echo
openssl x509 -in server.crt -noout -subject -enddate -ext subjectAltName
