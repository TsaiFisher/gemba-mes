#!/bin/bash
# ProjectZ 手冊 Q&A 推論後端切換
#
# 這台有兩份 Ollama，且**都綁 11434 但在不同 network namespace**：
#   win  = Win11 那份（主力，12 顆模型）→ 只能經 SSH 反向通道從 WSL 打 11435
#   wsl  = WSL 內 ~/.local/ollama 那份（備援，只有 gemma3:4b）→ 127.0.0.1:11434
# 從 WSL 打 127.0.0.1:11434 永遠打到 wsl 那份，打不到 Win11。「連得上」不等於「連對了」。
#
# 用法：
#   switch-llm.sh status              # 印出兩端點、通道、app、目前 profile
#   switch-llm.sh ensure              # 只確保目前 profile 的端點通（不改組態、不碰 app）
#   switch-llm.sh win [model]         # 切到 Win11（預設 gemma4:latest）
#   switch-llm.sh wsl [model]         # 切到 WSL 內（預設 gemma3:4b）
#   switch-llm.sh verify              # 端到端問一題，驗引文契約（citations 非空）
#   選項：--start（app 沒跑就啟動）  --no-restart（只改組態不碰 app）
#
# ⚠ **WSL 一重開機，win 通道就斷，而且不會自己回來。**
# 通道是 Windows 上一支 `ssh -N` 連進 WSL 的 sshd；WSL 關機時 TCP 斷、該進程退出，
# 沒有任何東西會重拉。Win11 的 ollama.exe 反而好好的（Startup 有 Ollama.lnk），
# 所以症狀是「模型明明在、卻連不上推論服務」。
# 復原就一句 `switch-llm.sh ensure`；run-app-llm.sh 已在啟動前自動呼叫它。
# 為什麼不設 Windows 開機自啟：那是「Windows 登入時」觸發，而 `wsl --shutdown`
# 不需要重新登入 Windows——觸發時機與故障時機對不上。詳見規格 §17.11.2。
set -uo pipefail

# 腳本住版控（tools/），但**狀態與 log 一律留 ~/.cache**，不污染專案目錄——
# 對齊 tools/ 其餘腳本的慣例（見 start-lan.sh / dev-android.sh）。
PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$HOME/.cache/ollama-pull"
mkdir -p "$CACHE"
PROFILE_ENV=$CACHE/llm-profile.env
STATE=$CACHE/llm-state.json
APPSETTINGS=$PROJ/appsettings.Development.json
# 走 PATH，不寫死 System32 全路徑（各機 Windows 目錄大小寫與磁碟機代號未必相同）
PS=powershell.exe
EMBED_MODEL=snowflake-arctic-embed2:latest
WIN_URL=http://127.0.0.1:11435
WSL_URL=http://127.0.0.1:11434
WIN_DEFAULT=gemma4:latest
WSL_DEFAULT=gemma3:4b
export XDG_RUNTIME_DIR=/run/user/$(id -u)

red()  { printf '\033[31m%s\033[0m\n' "$*"; }
grn()  { printf '\033[32m%s\033[0m\n' "$*"; }
ylw()  { printf '\033[33m%s\033[0m\n' "$*"; }
die()  { red "✗ $*"; exit 1; }

http() { curl -s -m 4 -o /dev/null -w '%{http_code}' "$1/api/tags"; }
tags() { curl -s -m 8 "$1/api/tags"; }

# --- 守門 0：win profile 的兩個環境前提 -------------------------------------
# win profile 整條路徑都建立在「這台是 WSL，而且叫得動 powershell.exe」之上。
# 日後會有實體 Windows / 實體 Linux / WSL 三種環境，非 WSL 的機器上 win profile
# 沒有意義——沒有這道守門的話，症狀會是 tunnel_up 靜默失敗後卡滿 20 秒才說「11435 拉不起來」，
# 把環境不適用誤導成通道故障。
is_wsl() { [ -f /proc/sys/kernel/osrelease ] && grep -qi microsoft /proc/sys/kernel/osrelease; }

# WSLInterop 是 binfmt_misc 的一筆註冊，由 /init 在 WSL 啟動時寫入。
# 它會被 systemd-binfmt 洗掉——apt 升級任何帶 /usr/lib/binfmt.d/*.conf 的套件
# （實例：python3.12-minimal）就會觸發 systemd-binfmt，而它在套用前先 flush 整個
# binfmt_misc，把 /init 註冊的這筆一併清除。此時 powershell.exe 會變成
# 「Exec format error」，而不是「找不到指令」。
interop_ok() { [ -e /proc/sys/fs/binfmt_misc/WSLInterop ] || [ -e /proc/sys/fs/binfmt_misc/WSLInterop-late ]; }

require_win_prereqs() {
  is_wsl || die "本機不是 WSL，win profile 不適用（win = 經 SSH 反向通道打 Windows 上的 Ollama）。
  實體 Linux／正式機請用：switch-llm.sh wsl [model]
  實體 Windows 請直接把 Features__QaAssistant__OllamaUrl 指向本機 Ollama。"

  interop_ok || die "WSL interop（binfmt_misc/WSLInterop）不存在，叫不動 powershell.exe，通道拉不起來。
  成因通常是 apt 升級帶進 /usr/lib/binfmt.d/*.conf 觸發 systemd-binfmt，
  而它在套用前 flush 掉整個 binfmt_misc，連 /init 註冊的 WSLInterop 一起清除。

  修復（需 sudo；第二行讓它在下次開機後仍在）：
    sudo sh -c 'echo \":WSLInterop:M::MZ::/init:PF\" > /proc/sys/fs/binfmt_misc/register'
    sudo sh -c 'echo \":WSLInterop:M::MZ::/init:PF\" > /etc/binfmt.d/WSLInterop.conf'

  不想動 sudo 就先用 WSL 內的那份：switch-llm.sh wsl"
}

# --- 守門 1：端點就緒（不通就自動拉起）-------------------------------------
tunnel_up() {
  "$PS" -NoProfile -Command "Start-Process -FilePath 'C:\Windows\System32\OpenSSH\ssh.exe' \
    -ArgumentList '-N','-p','2222','-i',\"\$env:USERPROFILE\.ssh\wsl-ollama-tunnel\",\
    '-R','11435:127.0.0.1:11434','-o','ServerAliveInterval=30','-o','ExitOnForwardFailure=yes',\
    '-o','StrictHostKeyChecking=accept-new','dev@localhost' -WindowStyle Hidden" >/dev/null 2>&1
}
ensure_endpoint() {
  local p=$1 url=$2
  [ "$(http "$url")" = "200" ] && { grn "✓ 端點就緒 $url"; return 0; }
  if [ "$p" = win ]; then
    require_win_prereqs
    ylw "… 11435 不通，拉 SSH 反向通道（由 Windows 端發起）"
    tunnel_up
  else
    ylw "… 11434 不通，啟動 WSL 內的 ollama.service（自啟已停用，改按需啟動）"
    systemctl --user start ollama.service
  fi
  for _ in $(seq 20); do sleep 1; [ "$(http "$url")" = "200" ] && { grn "✓ 端點就緒 $url"; return 0; }; done
  if [ "$p" = win ]; then
    die "11435 拉不起來。檢查 Win11 的 ollama 是否在跑、WSL sshd 是否還在 127.0.0.1:2222。"
  else
    die "11434 拉不起來。看 systemctl --user status ollama.service"
  fi
}

# --- 守門 2/3：模型存在 + 嵌入模型 digest 一致 -----------------------------
check_models() {
  local url=$1 chat=$2
  URL=$url CHAT=$chat EMBED=$EMBED_MODEL STATE=$STATE python3 - <<'PY'
import json, os, sys, urllib.request, pathlib
url, chat, embed, state = os.environ['URL'], os.environ['CHAT'], os.environ['EMBED'], os.environ['STATE']
d = json.load(urllib.request.urlopen(url + '/api/tags', timeout=8))
byname = {m['name']: m for m in d['models']}

if chat not in byname:
    print('✗ 守門2 失敗：端點沒有生成模型 %s' % chat)
    print('  可用：' + ', '.join(sorted(byname)))
    print('  （切過去只會在畫面上看到「生成模型 %s 不存在」）' % chat)
    sys.exit(1)
print('✓ 守門2 生成模型存在：%s' % chat)

if embed not in byname:
    print('✗ 守門3 失敗：端點沒有嵌入模型 %s —— 檢索會整個失效' % embed)
    sys.exit(1)
dg = byname[embed]['digest']
p = pathlib.Path(state)
st = json.loads(p.read_text()) if p.exists() else {}
base = st.get('embedding_digest')
if base and base != dg:
    print('✗ 守門3 失敗：嵌入模型同名但不同顆！')
    print('  索引建立時 digest=%s' % base[:12])
    print('  本端點     digest=%s' % dg[:12])
    print('  → 向量空間不可比，切過去相似度會變成雜訊而不是錯誤（照樣回得出 Top-K，只是全錯）。')
    print('  要換就得整批重嵌，不要硬切。')
    sys.exit(1)
st['embedding_digest'] = dg
p.write_text(json.dumps(st, indent=1))
print('✓ 守門3 嵌入模型一致：%s (digest %s%s)' % (embed, dg[:12], '' if base else '，已記錄為基準'))
PY
}

# --- 組態寫入：兩處一起改 ---------------------------------------------------
write_config() {
  local url=$1 chat=$2 profile=$3
  cat > "$PROFILE_ENV" <<EOF
# 由 switch-llm.sh 產生，勿手改（$(date '+%F %T')）
# 環境變數優先於 appsettings 且不熱重載，才守得住別的 session 還原檔案。
export MES_LLM_PROFILE=$profile
export Features__QaAssistant__OllamaUrl=$url
export Features__QaAssistant__ChatModel=$chat
export Features__QaAssistant__EmbeddingModel=$EMBED_MODEL
EOF
  URL=$url CHAT=$chat EMBED=$EMBED_MODEL F=$APPSETTINGS python3 - <<'PY'
import json, os, collections
f = os.environ['F']
d = json.load(open(f, encoding='utf-8'), object_pairs_hook=collections.OrderedDict)
q = d.setdefault('Features', collections.OrderedDict()).setdefault('QaAssistant', collections.OrderedDict())
q['OllamaUrl'], q['ChatModel'], q['EmbeddingModel'] = os.environ['URL'], os.environ['CHAT'], os.environ['EMBED']
json.dump(d, open(f, 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
  grn "✓ 組態已寫入兩處：llm-profile.env（run-app.sh 用）+ appsettings.Development.json（F5／dotnet run 用）"
  ylw "  兩處一起改是刻意的：OllamaClient 在建構子把 model 讀死，只改一處會留下難查的未爆彈。"
}

# ss 會為同一個 socket 列出多個 fd，grep -o 會抓到重複的 pid=；
# 不收斂成一個，kill "$pid" 會拿到 "9013 9013" 而失敗，重啟整條路徑就壞了。
app_pid() { ss -ltnp 2>/dev/null | grep -m1 '127.0.0.1:5110' | grep -o 'pid=[0-9]*' | cut -d= -f2 | head -1; }

# --- 守門 4：不要打斷別人跑到一半的測試 -------------------------------------
wait_for_tests() {
  # 看 testhost 而不是 vstest.console.dll：VS Code C# Dev Kit 常駐一個 vstest.console.dll，
  # 拿它當守門條件會永遠等不到。
  if pgrep -x testhost >/dev/null 2>&1; then
    ylw "… 偵測到 testhost 正在跑，等它結束再重啟 app（直接重啟會把對方打成假紅）"
    while pgrep -x testhost >/dev/null 2>&1; do sleep 15; done
  fi
}

restart_app() {
  local pid; pid=$(app_pid)
  if [ -z "$pid" ]; then
    [ "${DO_START:-0}" = 1 ] || { ylw "· app 沒在跑，組態已就緒（要順手啟動加 --start）"; return 0; }
  fi
  wait_for_tests
  if [ -n "$pid" ]; then
    ylw "… 停舊 app (pid=$pid)"   # 用明確 pid，不用 pkill -f（會匹配到自己的命令列而自殺）
    kill "$pid"
    for _ in $(seq 30); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
  fi
  ylw "… 啟動 app"
  setsid nohup "$PROJ/tools/run-app-llm.sh" >> "$CACHE/app.log" 2>&1 &
  for _ in $(seq 90); do ss -ltn 2>/dev/null | grep -q '127.0.0.1:5110' && { grn "✓ app 已就緒 :5110"; return 0; }; sleep 2; done
  die "app 90 秒內沒起來，看 $CACHE/app.log"
}

cmd_status() {
  echo "=== 目前 profile ==="
  [ -f "$PROFILE_ENV" ] && grep '^export' "$PROFILE_ENV" | sed 's/^export /  /' || echo "  （尚未切換過）"
  echo
  echo "=== 端點 ==="
  local w s; w=$(http $WIN_URL); s=$(http $WSL_URL)
  echo "  win  11435 (Win11 經 SSH 通道) : $w $([ "$w" = 200 ] && echo 通 || echo 不通)"
  echo "  wsl  11434 (WSL ~/.local)      : $s $([ "$s" = 200 ] && echo 通 || echo 不通)"
  [ "$w" = 200 ] && tags $WIN_URL | python3 -c "import json,sys;print('  win 模型:',', '.join(m['name'] for m in json.load(sys.stdin)['models']))"
  [ "$s" = 200 ] && tags $WSL_URL | python3 -c "import json,sys;print('  wsl 模型:',', '.join(m['name'] for m in json.load(sys.stdin)['models']))"
  echo
  echo "=== 環境前提（決定 win profile 可不可用）==="
  if is_wsl; then
    echo "  這台是 WSL          : 是"
    if interop_ok; then
      echo "  WSL interop         : 在（powershell.exe 叫得動）"
    else
      red   "  WSL interop         : 不在 → win profile 拉不起通道，跑 switch-llm.sh win 會印出修復指令"
    fi
  else
    echo "  這台是 WSL          : 否 → win profile 不適用，請用 wsl profile"
  fi
  echo
  echo "=== 服務 ==="
  echo "  WSL ollama.service: $(systemctl --user is-active ollama.service) / 自啟 $(systemctl --user is-enabled ollama.service 2>&1)"
  local p; p=$(app_pid)
  if [ -n "$p" ]; then echo "  app :5110         : 執行中 pid=$p"; else echo "  app :5110         : 未執行"; fi
}

# 只做守門 1。**刻意不重啟 app、不寫組態**——run-app-llm.sh 會在啟動前呼叫它，
# 若這裡也去 restart_app 就會無限遞迴。
cmd_ensure() {
  [ -f "$PROFILE_ENV" ] || { ylw "· 尚未切換過 profile（$PROFILE_ENV 不存在），略過端點檢查"; return 0; }
  # shellcheck disable=SC1090
  . "$PROFILE_ENV"
  local p=${MES_LLM_PROFILE:-} url=${Features__QaAssistant__OllamaUrl:-}
  [ -n "$p" ] && [ -n "$url" ] || { ylw "· profile 內容不完整，略過端點檢查"; return 0; }
  ensure_endpoint "$p" "$url"
}

cmd_verify() {
  [ -n "$(app_pid)" ] || die "app 沒在跑，先 switch-llm.sh <profile> --start"
  ylw "… 端到端問一題（引文契約：回覆最後要有獨立的「來源：」行才解析得出 citations）"
  bash "$PROJ/tools/qa-ask.sh" "${1:-庫存異動明細要去哪裡查？}"
  CACHE=$CACHE python3 - <<'PY'
import json, os, pathlib, sys
d = json.loads(pathlib.Path(os.environ['CACHE'] + '/ask-resp.json').read_text(encoding='utf-8')).get('data') or {}
c = d.get('citations') or []
if d.get('isNoAnswer'):   print('\n⚠ 模型拒答（isNoAnswer=true）'); sys.exit(0)
if c: print('\n\033[32m✓ 引文契約通過\033[0m：citations %d 筆 → %s' % (len(c), c[0].get('pageCode')))
else: print('\n\033[31m✗ 引文契約未過\033[0m：citations 空。先懷疑模型遵循度（寫不出獨立的「來源：」行），不要去查檢索。')
PY
}

case "${1:-status}" in
  status) cmd_status ;;
  ensure) cmd_ensure ;;
  verify) shift; cmd_verify "${1:-}" ;;
  win|wsl)
    P=$1; shift
    MODEL=""; DO_START=0; RESTART=1
    while [ $# -gt 0 ]; do
      case "$1" in
        --start) DO_START=1 ;;
        --no-restart) RESTART=0 ;;
        -*) die "未知選項 $1" ;;
        *) MODEL=$1 ;;
      esac; shift
    done
    export DO_START
    if [ "$P" = win ]; then URL=$WIN_URL; MODEL=${MODEL:-$WIN_DEFAULT}; else URL=$WSL_URL; MODEL=${MODEL:-$WSL_DEFAULT}; fi
    echo "=== 切換到 $P：$URL / $MODEL ==="
    ensure_endpoint "$P" "$URL"
    check_models "$URL" "$MODEL" || exit 1
    write_config "$URL" "$MODEL" "$P"
    if [ "$MODEL" != gemma3:4b ]; then
      ylw "⚠ $MODEL 尚未實測過引文契約（目前只有 gemma3:4b 通過）。切完跑 switch-llm.sh verify 再定案。"
    fi
    # 用 if 不用 [ ] && cmd：後者在條件不成立時會把 1 當成腳本結束碼，讓上層誤判失敗。
    if [ "$RESTART" = 1 ]; then restart_app; fi
    ;;
  # 印到 `set -` 為止，不寫死行號——行號會隨標頭增修而失準（原本寫死 2,20 已經切掉半段警告）
  *) sed -n '2,/^set -/{/^set -/!p;}' "$0"; exit 1 ;;
esac
