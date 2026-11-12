#!/usr/bin/env bash
# 以 admin 登入後對 /QaAssistant/Ask 送一題，量牆鐘並印出回應。
# switch-llm.sh verify 用它做端到端驗證（重點是引文契約：citations 非空）。
#
# 密碼預設值與 repo 內既有的開發種子一致（.github/workflows/ci.yml 也是這個值），
# 不是新引入的秘密；要換用 ADMIN_PW 覆寫。
set -uo pipefail

Q="${1:-庫存異動明細要去哪裡查？}"
CACHE="$HOME/.cache/ollama-pull"; mkdir -p "$CACHE"
RESP="$CACHE/ask-resp.json"
J=$(mktemp "$CACHE/cookie.XXXX")
B=http://localhost:5110

tok=$(curl -s -c "$J" "$B/Identity/Account/Login" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"//')
code=$(curl -s -b "$J" -c "$J" -o /dev/null -w '%{http_code}' -X POST "$B/Identity/Account/Login" \
  --data-urlencode "Input.Email=admin" \
  --data-urlencode "Input.Password=${ADMIN_PW:-Dev12345}" \
  --data-urlencode "__RequestVerificationToken=$tok")
echo "登入 http=$code"

tok2=$(curl -s -b "$J" -c "$J" "$B/QaAssistant" \
  | grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' | head -1 | sed 's/.*value="//;s/"//')
echo "--- 畫面就緒檢查 ---"
curl -s -b "$J" "$B/QaAssistant" | grep -c "推論服務尚未就緒" | sed 's/^/尚未就緒字樣出現次數: /'

s=$(date +%s.%N)
curl -s -b "$J" -c "$J" -X POST "$B/QaAssistant/Ask" \
  --data-urlencode "question=$Q" \
  --data-urlencode "__RequestVerificationToken=$tok2" > "$RESP"
e=$(date +%s.%N)
echo "=== 牆鐘 $(echo "$e-$s" | bc) 秒 ==="
RESP=$RESP python3 - <<'PY'
import json,os,pathlib
p=pathlib.Path(os.environ["RESP"])
try:
    r=json.loads(p.read_text(encoding="utf-8"))
except Exception:
    print("非 JSON 回應："); print(p.read_text(encoding="utf-8")[:500]); raise SystemExit
d=r.get("data") or {}
print("success:", r.get("success"), "| model:", d.get("model"), "| elapsedMs:", d.get("elapsedMs"))
print("isNoAnswer:", d.get("isNoAnswer"), "| citations:", d.get("citations"))
print("--- body ---"); print(d.get("body"))
PY
rm -f "$J"
