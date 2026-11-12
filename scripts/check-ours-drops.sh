#!/bin/bash
# 合併後檢查：有哪些檔案因為 .gitattributes 的 merge=ours 而「對方的改動被整個丟掉」。
#
# ── 為什麼需要這支（2026-09-08 實際踩到）────────────────────────────────────
# merge=ours 的 driver 在**兩邊都改過同一個檔**時被呼叫，結果是完整保留我們這側、
# 把對方的改動整個丟掉——**不列入衝突、不印任何警告**。git 甚至還是印
# "Auto-merging <file>"，看起來一切正常。
#
# 實例：把 main（剛合入 PR #138）併進功能分支時，
#   wwwroot/mobile/home.html            main 改了 16 行，一行都沒收
#   .claude/specifications/mobile-pwa.md  main 改了 50 行，一行都沒收
# 症狀是 #138 剛加好的「待我簽核」入口在手機首頁上直接消失，而全部測試照樣全綠
# （沒有任何東西斷言那個入口存在）。
#
# ⚠️ 只有「**兩邊都改過**」的檔會中招。只有對方單邊改過的不會——沒有衝突就不會
# 呼叫 driver，git 會直接採用對方的版本。所以風險面比想像中窄，但也因此更難發現。
#
# ── 為什麼要分兩種模式 ────────────────────────────────────────────────────────
# merge=ours 的**用途**是「Core 更新時保留本專案的客製」（appsettings、README、
# snapshot、Program.cs…），所以合併 Core 時被丟掉多半是**刻意的**，全部當錯誤會
# 變成每次都要略過的雜訊——而每次都被略過的清單等於沒有守門。
#
# 但合併**本專案自己的分支**（main → feature、feature → feature）時，merge=ours
# 完全沒有理由生效：兩邊都是本專案的內容，丟掉哪一邊都是資料遺失。
# 所以：
#   對方是 Core（upstream/main 的祖先）→ 列出來給人看，exit 0
#   對方是本專案的 ref               → exit 1，這一定要處理
#
# ── 用法 ──────────────────────────────────────────────────────────────────────
#   scripts/check-ours-drops.sh          # 檢查 HEAD 這個合併 commit
#   scripts/check-ours-drops.sh <merge>  # 檢查指定的合併 commit
#
# 修法：對受影響的檔案做真正的三方合併，例如
#   git show <base>:<file>   > /tmp/base
#   git show <ours>:<file>   > /tmp/ours
#   git show <theirs>:<file> > /tmp/theirs
#   git merge-file -L ours -L base -L theirs /tmp/ours /tmp/base /tmp/theirs
#   cp /tmp/ours <file>
set -uo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*"; }
grn() { printf '\033[32m%s\033[0m\n' "$*"; }
ylw() { printf '\033[33m%s\033[0m\n' "$*"; }

MERGE="${1:-HEAD}"

# ── 前置：必須是一個有兩個父節點的合併 commit ────────────────────────────────
OURS=$(git rev-parse -q --verify "${MERGE}^1" 2>/dev/null)
THEIRS=$(git rev-parse -q --verify "${MERGE}^2" 2>/dev/null)

if [ -z "$OURS" ] || [ -z "$THEIRS" ]; then
    ylw "⏭  $MERGE 不是合併 commit（沒有第二個父節點），無須檢查。"
    exit 0
fi

BASE=$(git merge-base "$OURS" "$THEIRS" 2>/dev/null)
if [ -z "$BASE" ]; then
    red "✗ 找不到 $OURS 與 $THEIRS 的共同祖先，無法判定。"
    exit 1
fi

# ── 模式判定：對方是 Core 還是本專案 ─────────────────────────────────────────
# upstream/main 沒 fetch 過就無從判斷，那時保守地當成「本專案」（會擋，不會漏）。
MODE="internal"
if git rev-parse -q --verify upstream/main >/dev/null 2>&1; then
    if git merge-base --is-ancestor "$THEIRS" upstream/main 2>/dev/null; then
        MODE="core"
    fi
fi

# ── 逐檔判定 ─────────────────────────────────────────────────────────────────
DROPPED=()
while IFS= read -r f; do
    [ -z "$f" ] && continue

    # 只看標了 merge=ours 的檔
    [ "$(git check-attr merge -- "$f" | awk '{print $NF}')" = "ours" ] || continue

    # 合併結果與「我們合併前」逐字元相同 ＝ 對方那一側一行都沒收進來。
    # （檔案在其中一側不存在時 git cat-file 會失敗，一併視為沒中招。）
    merged_hash=$(git rev-parse -q --verify "$MERGE:$f" 2>/dev/null) || continue
    ours_hash=$(git rev-parse -q --verify "$OURS:$f" 2>/dev/null)   || continue
    [ "$merged_hash" = "$ours_hash" ] || continue

    stat=$(git diff --numstat "$BASE" "$THEIRS" -- "$f" | awk '{print $1"+ / "$2"-"}')
    DROPPED+=("$f|$stat")
done < <(git diff --name-only "$BASE" "$THEIRS")

# ── 回報 ─────────────────────────────────────────────────────────────────────
if [ ${#DROPPED[@]} -eq 0 ]; then
    grn "✅ 沒有任何 merge=ours 檔丟掉對方的改動"
    exit 0
fi

echo ""
if [ "$MODE" = "core" ]; then
    ylw "ℹ️  以下 ${#DROPPED[@]} 個 merge=ours 檔保留了本專案的版本，Core 的改動未收："
else
    red "❌ 以下 ${#DROPPED[@]} 個 merge=ours 檔把**本專案自己**的改動丟掉了："
fi

for d in "${DROPPED[@]}"; do
    printf '   %s   （對方改了 %s）\n' "${d%%|*}" "${d##*|}"
done

echo ""
echo "   base   = $BASE"
echo "   ours   = $OURS"
echo "   theirs = $THEIRS"
echo ""

if [ "$MODE" = "core" ]; then
    cat <<'EOF'
   合併 Core 時這多半是**刻意的**（merge=ours 的用途就是保留本專案的客製），
   但仍請逐檔確認：Core 這次的改動裡有沒有本專案其實該跟的部分。
   ⚠️ 特別注意 Program.cs——Core 新增的服務註冊會在這裡被丟掉，
   而症狀是「建置成功、測試全綠，但某一頁一開就 500」。
EOF
    exit 0
fi

cat <<'EOF'
   對方是**本專案自己的 ref**，merge=ours 在這裡沒有任何正當理由生效——
   丟掉的是同事（或你自己另一支分支）剛做好的東西，而且沒有衝突、沒有警告。

   修法：對上列每個檔做真正的三方合併
     git show <base>:<file>   > /tmp/b
     git show <ours>:<file>   > /tmp/o
     git show <theirs>:<file> > /tmp/t
     git merge-file -L ours -L base -L theirs /tmp/o /tmp/b /tmp/t && cp /tmp/o <file>
   然後 git add + git commit --amend（或另開一個修正 commit）。
EOF
exit 1
