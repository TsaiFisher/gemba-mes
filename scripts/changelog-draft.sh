#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# CHANGELOG 草稿產生器（overall-assessment P2-3「CHANGELOG 自動化」）
#
# 彙整自上一個版本 tag（或指定起點）以來的 conventional commits，
# 依 Keep a Changelog 章節分組輸出 markdown 草稿到 stdout；
# 人工挑選/潤飾後貼入 CHANGELOG.md 的 [Unreleased] 區塊。
#
# 用法：
#   scripts/changelog-draft.sh              # 自最近一個 tag 以來
#   scripts/changelog-draft.sh v1.0.0       # 指定起點（tag 或 SHA）
#   scripts/changelog-draft.sh v1.0.0 HEAD  # 指定起訖
#
# 分組規則（type(scope): subject）：
#   feat     → Added
#   fix      → Fixed
#   security → Security（另含 subject 帶「資安/安全」關鍵字的 feat/fix 提示）
#   perf     → Changed
#   docs     → Docs
#   refactor/chore/test/ci/build/style → Internal（預設不進 CHANGELOG，僅供人工參考）
#
# 排除：
#   - merge commits（含 update-core.sh 的 Core 同步 merge——ProjectZ 版本線獨立於 Core，
#     平台更新不單獨列版，見 CHANGELOG.md 表頭說明）
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
cd "$(dirname "$0")/.."

FROM="${1:-$(git describe --tags --abbrev=0 2>/dev/null || true)}"
TO="${2:-HEAD}"
if [ -z "$FROM" ]; then
  echo "找不到版本 tag；請指定起點：scripts/changelog-draft.sh <tag|sha> [to]" >&2
  exit 1
fi

RANGE="${FROM}..${TO}"
echo "<!-- CHANGELOG 草稿：${RANGE}，$(git log --no-merges --oneline "$RANGE" | wc -l | tr -d ' ') 筆 commit（不含 merge）；產生於 $(date +%F) -->"
echo

emit_section () {
  local title="$1"; shift
  local pattern="$1"; shift
  local body
  body=$(git log --no-merges --format='%s|%h' "$RANGE" \
    | grep -E "^(${pattern})(\([^)]*\))?!?: " || true)
  [ -z "$body" ] && return 0
  echo "### ${title}"
  # scope 保留為粗體前綴：feat(mfg): xxx → - **mfg**：xxx (abc1234)
  echo "$body" | sed -E \
    -e 's/^[a-z]+\(([^)]*)\)!?: (.*)\|(.*)$/- **\1**：\2 (\3)/' \
    -e 's/^[a-z]+!?: (.*)\|(.*)$/- \1 (\2)/'
  echo
}

emit_section "Added"    "feat"
emit_section "Fixed"    "fix"
emit_section "Security" "security|sec"
emit_section "Changed"  "perf"
emit_section "Docs"     "docs"

# 資安關鍵字提示：feat/fix 內文含資安字樣者，提醒人工考慮移到 Security 章節
hints=$(git log --no-merges --format='%s|%h' "$RANGE" \
  | grep -E '^(feat|fix)(\([^)]*\))?!?: ' \
  | grep -E '資安|安全|權限|授權|2FA|CSP|XSS|CSRF|IDOR' || true)
if [ -n "$hints" ]; then
  echo "<!-- 提示：以下 feat/fix 含資安關鍵字，可考慮移至 Security 章節 -->"
  echo "$hints" | sed -E 's/^(.*)\|(.*)$/<!--   \1 (\2) -->/'
  echo
fi

echo "<!-- Internal（refactor/chore/test/ci/build/style），預設不進 CHANGELOG，僅供參考 -->"
git log --no-merges --format='%s|%h' "$RANGE" \
  | grep -E '^(refactor|chore|test|ci|build|style)(\([^)]*\))?!?: ' \
  | sed -E 's/^(.*)\|(.*)$/<!--   \1 (\2) -->/' || true
