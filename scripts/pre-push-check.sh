#!/bin/bash
# ProjectZ push 前檢查（本機跑，不打 CI）
#
# 存在的理由：這個 fork 有三道守門是**比對 HEAD 而不是工作樹**的
# （Core 漂移、snapshot 漂移、boundary hook），所以「commit 完才有意義跑」，
# 而那個時間點很容易直接就 push 了。
#
# ⚠️ 它**證明不了 CI 會綠**：UITests 要外部 app（:5110）、E2E 要 Ollama、
#    Actions 額度被停時 job 根本不會啟動——那不是測試紅。最後一段列出的
#    人工項目是腳本驗不了的部分，不要因為這支綠了就跳過。
#
# 用法：
#   scripts/pre-push-check.sh              # 完整檢查（會 fetch upstream）
#   scripts/pre-push-check.sh --no-fetch   # 離線時用；會明講「沒驗到」而不是當成通過
#   scripts/pre-push-check.sh --quick      # 跳過建置與單元測試（只跑守門）
set -uo pipefail
cd "$(dirname "$0")/.."

DO_FETCH=1; DO_TESTS=1
for a in "$@"; do
  case "$a" in
    --no-fetch) DO_FETCH=0 ;;
    --quick)    DO_TESTS=0 ;;
    *) echo "不認得的參數：$a"; exit 2 ;;
  esac
done

red() { printf '\033[31m%s\033[0m\n' "$*"; }
grn() { printf '\033[32m%s\033[0m\n' "$*"; }
ylw() { printf '\033[33m%s\033[0m\n' "$*"; }
FAIL=0
step() { printf '\n\033[1m── %s\033[0m\n' "$*"; }
ok()   { grn "✓ $*"; }
bad()  { red "✗ $*"; FAIL=1; }
skip() { ylw "⚠ $*"; }

step "1. 分支與待推內容"
BRANCH=$(git rev-parse --abbrev-ref HEAD)
echo "  分支：$BRANCH"
[ "$BRANCH" = "main" ] && bad "在 main 上。Free 私有 repo 沒有 branch protection，push-to-main 是唯一閘門——請開分支。" \
                       || ok "不在 main 上"

if [ -n "$(git status --porcelain)" ]; then
  bad "工作樹不乾淨。守門比對的是 HEAD，未 commit 的修正驗不到。"
  git status --short | head -10
else
  ok "工作樹乾淨"
fi

AHEAD=$(git rev-list --count "@{u}..HEAD" 2>/dev/null || echo "?")
if [ "$AHEAD" = "?" ]; then
  skip "這個分支還沒有 upstream tracking，待推數量未知（第一次 push 要用 -u）"
else
  echo "  待推 commit：$AHEAD 個"
  git log --oneline "@{u}..HEAD" | head -10 | sed 's/^/    /'
fi

step "2. 機密檔沒有進版控"
# gitleaks 擋的是「內容像金鑰」，這一段擋的是「這幾個檔本身不該被追蹤」——
# 兩者不重疊：一份沒有金鑰但有正式區連線字串的 appsettings.Development.json
# 過得了 gitleaks。
SECRETS=(appsettings.Development.json license.json .env)
LEAK=0    # 用區域旗標，不要看全域 FAIL——前一段失敗會讓這一段連「通過」都不印，
          # 而那正好是最需要看到結論的一段。
for f in "${SECRETS[@]}"; do
  if git ls-files --error-unmatch "$f" >/dev/null 2>&1; then
    bad "$f 已被版控追蹤——必須先 git rm --cached 並確認歷史"; LEAK=1
  fi
done
[ "$LEAK" = 0 ] && ok "appsettings.Development.json／license.json／.env 都不在版控內"

step "3. 與 Core 對齊"
if [ "$DO_FETCH" = 1 ]; then
  if git fetch upstream main 2>/dev/null; then
    ok "已 fetch upstream/main"
  else
    skip "fetch upstream 失敗（沒網路或沒設 upstream）。下面兩道守門比對的是**上一次 fetch 的**
    upstream/main——Core 這段期間若動過，這裡的綠燈是過期的。"
  fi
else
  skip "--no-fetch：跳過 fetch。守門比對的是上一次 fetch 的 upstream/main，不是 Core 現況。"
fi

step "4. Core 漂移守門"
if bash scripts/check-core-drift.sh >/tmp/ppc-drift.log 2>&1; then
  ok "$(tail -1 /tmp/ppc-drift.log)"
else
  bad "有新的 Core 漂移（完整輸出 /tmp/ppc-drift.log）"; tail -15 /tmp/ppc-drift.log | sed 's/^/    /'
fi

step "5. snapshot 漂移守門"
# 先建一次主專案：dotnet ef 需要 restore／建置產物，全新 clone 直接跑會因 NETSDK1004 失敗（2026-10-29 預演實測）
dotnet build SmartFactory.MES.csproj -v q --nologo >/tmp/ppc-snap-build.log 2>&1 || true
bash scripts/check-snapshot-drift.sh >/tmp/ppc-snap.log 2>&1; snap_rc=$?
if [ $snap_rc = 0 ]; then
  ok "snapshot 與模型一致（兩個 DbContext）"
elif [ $snap_rc = 2 ]; then
  bad "建置／產物問題，snapshot **沒有檢查到**（完整輸出 /tmp/ppc-snap.log、/tmp/ppc-snap-build.log）——不要去產 migration"
  tail -8 /tmp/ppc-snap.log | sed 's/^/    /'
else
  bad "snapshot 與模型不一致（完整輸出 /tmp/ppc-snap.log）"
  ylw "  這在 runtime 不會出事，但下一次 migrations add 會把 Core 已套用過的 DDL 重新生成。"
  tail -15 /tmp/ppc-snap.log | sed 's/^/    /'
fi

step "6. 建置與單元測試"
if [ "$DO_TESTS" = 0 ]; then
  skip "--quick：跳過。push 前至少要跑過一次完整的。"
else
  # 逐個 csproj 建（與 CI 相同），不用 .sln：SmartFactory.MES.sln 的方案組態是空的，
  # `dotnet build *.sln` 0.2 秒回報成功卻一個專案都沒建，下面 --no-build 就拿舊產物在測（.NET 10 SDK 實測依然如此）。
  build_ok=1; : >/tmp/ppc-build.log
  for proj in SmartFactory.MES.csproj SmartFactory.MES.Tests/SmartFactory.MES.Tests.csproj \
              SmartFactory.MES.IntegrationTests/SmartFactory.MES.IntegrationTests.csproj \
              SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj; do
    dotnet build "$proj" -v q --nologo >>/tmp/ppc-build.log 2>&1 || { build_ok=0; break; }
  done
  if [ $build_ok = 1 ]; then
    ok "四個專案建置通過"
    if dotnet test SmartFactory.MES.Tests/SmartFactory.MES.Tests.csproj --no-build \
         >/tmp/ppc-test.log 2>&1; then
      ok "$(grep -m1 'Passed!' /tmp/ppc-test.log | sed 's/^ *//')"
    else
      bad "單元測試有紅（/tmp/ppc-test.log）"; grep -m10 '\[FAIL\]' /tmp/ppc-test.log | sed 's/^/    /'
    fi
  else
    bad "建置失敗（/tmp/ppc-build.log）"; grep -m10 'error' /tmp/ppc-build.log | sed 's/^/    /'
  fi
fi

step "7. 擷取物工具鏈自我測試"
# 三支 qa-extract-*.py 沒有進 dotnet test，而它們壞掉的樣子都不會報錯：
# 對帳把「程式碼改了、註解沒改」判成未變，就等於替一段可能已經不成立的說明重新蓋章。
if command -v python3 >/dev/null 2>&1; then
  if python3 scripts/qa-extract-selftest.py >/tmp/ppc-qa-selftest.log 2>&1; then
    ok "$(grep -c '✓' /tmp/ppc-qa-selftest.log) 項通過"
  else
    bad "擷取物工具鏈自我測試有紅（/tmp/ppc-qa-selftest.log）"
    grep '✗' /tmp/ppc-qa-selftest.log | sed 's/^/    /'
  fi
else
  skip "沒有 python3，未驗到"
fi

step "8. 這支腳本驗不了的（人工確認）"
cat <<'EOF'
  □ Actions 額度是否已恢復——額度被停時 job 不會啟動，那不是測試紅
  □ push 後必須**讀** CI 監看輸出再談合併：背景 watch 完成 ≠ 綠
  □ UITests／E2E 本機沒跑到：前者要外部 app 在 :5110（且 DefaultEncrypted 要清空），
    後者要 Ollama。CI 上它們有自己的前置，這裡的綠燈與它們無關
  □ 若這批動到 Core 既有檔：先 Core commit → push → fetch upstream，
    再讓部署專案的變更放行。不要用 --no-verify 繞過 boundary hook
EOF

echo
if [ "$FAIL" = 0 ]; then grn "══ 本機檢查全部通過。上面第 8 段仍需人工確認。"; else red "══ 有項目未通過，先處理再 push。"; fi
exit $FAIL
