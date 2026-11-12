#!/bin/bash
# 合併前的確定性閘門：查不到「Build & Test 真的跑過而且成功」就不准合併。
#
# 存在的理由（2026-09-08 實際踩過）：
# 這個 repo 的草稿 PR 會被 CI 跳過 `Build & Test`（2026-09-03～11-06 新開 PR 還會被自動轉草稿，
# 已撤除；手動開的草稿仍然 skip），草稿 PR 的 `Build & Test` 會被
# **skip**，而那一輪 workflow run 仍然回報 `conclusion=success`。
# 於是「CI 綠了」有三種截然不同的含意，在 run 層完全分不出來：
#   ① 測試真的跑過而且過了
#   ② 草稿閘門把測試整個跳過（run 仍是 success）
#   ③ 只推分支：只跑 Validate Module Boundaries，本來就不含測試
# PR #131 就是在 `gh pr ready` 之後 21 秒合併的——CI 還在跑，結果是 failure，
# main 因此變紅。**所以這支腳本一律看 job 層的 conclusion，不看 run 層。**
#
# 為什麼不用 `gh pr merge --auto`：本 repo 是 Free 方案私有庫，
# `branches/main/protection` 回 403（沒有 branch protection ＝ 沒有 required checks），
# 且 `allow_auto_merge=false`。沒有必要檢查可等，auto-merge 等於立刻合併。
# 等哪天升級 Pro 或轉公開拿到 required status checks，這支就可以退役。
#
# 用法：
#   scripts/safe-merge.sh 133                 # 檢查通過才 squash 合併
#   scripts/safe-merge.sh 133 --dry-run       # 只檢查、不合併
#   scripts/safe-merge.sh 133 --wait          # 等到有結論為止（每 30 秒查一次）
#                                             # 連「run 還沒建立」也等；預設上限 1800 秒，--timeout 可調
#   scripts/safe-merge.sh --sha <sha>         # 只驗某個 commit 的 CI 狀態（不碰 PR）
#   scripts/safe-merge.sh 133 --method merge  # 預設 squash
set -uo pipefail

# 這個 fork 有 origin(ProjectZ) 與 upstream(Core) 兩個 remote，gh 未設預設 repo 時
# 會誤解析目標——所以一律明講。要用在別的 repo 就設 SAFE_MERGE_REPO。
REPO="${SAFE_MERGE_REPO:-TsaiFisher/ProjectZ}"
# job 名稱是「Build & Test (.NET 10)」，用前綴比對以免哪天版本號變了就失效。
JOB_PREFIX="${SAFE_MERGE_JOB_PREFIX:-Build & Test}"

red() { printf '\033[31m%s\033[0m\n' "$*"; }
grn() { printf '\033[32m%s\033[0m\n' "$*"; }
ylw() { printf '\033[33m%s\033[0m\n' "$*"; }

PR=""; SHA=""; DRY=0; WAIT=0; METHOD="squash"; TIMEOUT="${SAFE_MERGE_TIMEOUT:-1800}"
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY=1 ;;
    --wait)    WAIT=1 ;;
    --sha)     shift; SHA="${1:-}" ;;
    --method)  shift; METHOD="${1:-squash}" ;;
    --timeout) shift; TIMEOUT="${1:-1800}" ;;
    -h|--help) sed -n '1,30p' "$0"; exit 0 ;;
    [0-9]*)    PR="$1" ;;
    *) red "不認得的參數：$1"; exit 2 ;;
  esac
  shift
done

if [ -z "$PR" ] && [ -z "$SHA" ]; then
  red "要給 PR 編號，或用 --sha 只驗某個 commit"; exit 2
fi

# 回傳（印出）該 commit 上 Build & Test 的彙總狀態：
#   success / failure / running / skipped / none
# 同一個 head SHA 可能有多輪 run（草稿那輪 + 按 Ready 之後那輪），
# 所以是「**有沒有任何一輪真的跑過而且成功**」，不是看最新那一輪。
ci_state() {
  local sha="$1" runs rid line status conc
  local seen_success=0 seen_failure=0 seen_running=0 seen_skipped=0 seen_any=0
  runs="$(gh api "repos/$REPO/actions/runs?head_sha=$sha&per_page=50" \
            --jq '.workflow_runs[].id' 2>/dev/null)" || { echo "none"; return; }
  for rid in $runs; do
    while IFS='|' read -r status conc; do
      [ -z "$status" ] && continue
      seen_any=1
      case "$status/$conc" in
        completed/success) seen_success=1 ;;
        completed/failure|completed/timed_out|completed/cancelled) seen_failure=1 ;;
        completed/skipped) seen_skipped=1 ;;
        *) seen_running=1 ;;   # queued / in_progress / waiting
      esac
    done <<< "$(gh api "repos/$REPO/actions/runs/$rid/jobs" \
                  --jq ".jobs[] | select(.name | startswith(\"$JOB_PREFIX\")) | \"\(.status)|\(.conclusion // \"\")\"" 2>/dev/null)"
  done
  if   [ "$seen_success" = 1 ]; then echo success
  elif [ "$seen_running" = 1 ]; then echo running
  elif [ "$seen_failure" = 1 ]; then echo failure
  elif [ "$seen_skipped" = 1 ]; then echo skipped
  elif [ "$seen_any"     = 1 ]; then echo none
  else echo none; fi
}

# ── --sha：只驗一個 commit，不碰 PR ────────────────────────────────────────
if [ -n "$SHA" ]; then
  # GitHub 的 head_sha 查詢**只認完整 40 碼**，給短 SHA 會靜靜回 0 筆
  # （看起來就像「這個 commit 沒跑過 CI」——是假的拒絕，比假的通過難察覺）。
  # 所以先用本地 git 展開；展不開（例如不在工作樹裡）就照原樣送出去。
  full="$(git rev-parse "$SHA^{commit}" 2>/dev/null)" && [ -n "$full" ] && SHA="$full"
  if [ ${#SHA} -ne 40 ]; then
    red "需要完整 40 碼 commit SHA（收到 ${#SHA} 碼：$SHA）"; exit 2
  fi
  st="$(ci_state "$SHA")"
  echo "commit ${SHA:0:7} 的 Build & Test：$st"
  [ "$st" = "success" ] && exit 0 || exit 1
fi

# ── PR 模式 ───────────────────────────────────────────────────────────────
meta="$(gh pr view "$PR" --repo "$REPO" \
         --json state,isDraft,mergeable,mergeStateStatus,headRefOid,title \
         --jq '"\(.state)|\(.isDraft)|\(.mergeable)|\(.mergeStateStatus)|\(.headRefOid)|\(.title)"' 2>/dev/null)"
[ -z "$meta" ] && { red "查不到 PR #$PR（repo=$REPO）"; exit 2; }
IFS='|' read -r STATE DRAFT MERGEABLE MSTATE HEAD TITLE <<< "$meta"

echo "PR #$PR  $TITLE"
echo "  狀態 $STATE / draft=$DRAFT / $MERGEABLE / $MSTATE"
echo "  head ${HEAD:0:7}"

fail=0
if [ "$STATE" != "OPEN" ]; then red "  ✗ PR 不是 OPEN（$STATE）"; fail=1; fi
# 草稿本來就不能合併，但先講清楚，否則使用者只會看到下面那句「測試被 skip」而不知道為什麼
if [ "$DRAFT" = "true" ]; then
  red "  ✗ 這是草稿 PR——草稿的 Build & Test 會被 skip，先 gh pr ready $PR"; fail=1
fi
if [ "$MERGEABLE" = "CONFLICTING" ]; then red "  ✗ 有衝突，先 rebase"; fail=1; fi

st="$(ci_state "$HEAD")"

# --wait 必須連「還沒出現」一起等，不能只等 running。
#
# 2026-09-08 自己踩到：`gh pr ready` 之後**立刻**跑這支，那個瞬間
# ready_for_review 觸發的那一輪還沒被建立，查得到的只有草稿那輪的 skipped，
# 於是它把 skipped 當成終局、直接拒絕——**假的拒絕，而且正好發生在
# 它最該支援的那個流程上（ready 完接著合併）**。
# 所以 running / skipped / none 三者在 --wait 之下都算「還沒有結論」，
# 只有 success 與 failure 是終局；另加總逾時上限，免得永遠掛著。
if [ "$WAIT" = 1 ]; then
  waited=0
  while [ "$st" != "success" ] && [ "$st" != "failure" ] && [ "$waited" -lt "$TIMEOUT" ]; do
    case "$st" in
      running) ylw "  … Build & Test 還在跑（已等 ${waited}s）" ;;
      *)       ylw "  … 還查不到本輪的 Build & Test（目前：$st，已等 ${waited}s）——新的一輪可能還沒建立" ;;
    esac
    sleep 30; waited=$((waited+30))
    st="$(ci_state "$HEAD")"
  done
  [ "$st" != "success" ] && [ "$st" != "failure" ] \
    && red "  ✗ 等了 ${waited}s 仍無結論（目前：$st）——逾時，未合併"
fi

case "$st" in
  success) grn "  ✓ Build & Test 在 ${HEAD:0:7} 上真的跑過且成功" ;;
  running) red "  ✗ Build & Test 還在跑——加 --wait 等它，不要現在合併"; fail=1 ;;
  failure) red "  ✗ Build & Test 失敗"; fail=1 ;;
  skipped) red "  ✗ Build & Test 被 skip（草稿閘門）——run 會顯示 success，但一個測試都沒跑"
           # 已經在等了還叫人「改用 --wait」是誤導，只在沒等的時候才提示
           [ "$WAIT" = 0 ] && ylw "    剛按過 Ready 的話，新的一輪可能還沒建立：改用 --wait"
           fail=1 ;;
  none)    red "  ✗ 這個 commit 上查不到任何 Build & Test（只推分支只會跑 Boundaries）"
           [ "$WAIT" = 0 ] && ylw "    剛推上去的話，run 可能還沒建立：改用 --wait"
           fail=1 ;;
esac

if [ "$fail" != 0 ]; then
  echo; red "拒絕合併。"; exit 1
fi

if [ "$DRY" = 1 ]; then echo; grn "檢查通過（--dry-run，未合併）"; exit 0; fi

# 合併前重讀一次 head：從檢查到現在若有人推了新 commit，剛才驗的就不是要合的東西
now="$(gh pr view "$PR" --repo "$REPO" --json headRefOid --jq '.headRefOid')"
if [ "$now" != "$HEAD" ]; then
  red "head 在檢查期間變了（${HEAD:0:7} → ${now:0:7}），重跑一次再合併"; exit 1
fi

echo
gh pr merge "$PR" --repo "$REPO" "--$METHOD"
