#!/usr/bin/env bash
# 帶推論後端組態啟動 app（tools/ 內另外三支 *啟動 app* 的腳本走 dotnet run，這支跑已建置的 dll）。
#
# 與 switch-llm.sh 成對使用：那支負責「切到哪個後端」，這支負責「照著切好的後端把 app 帶起來」。
set -uo pipefail

PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CACHE="$HOME/.cache/ollama-pull"

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:5110

# 推論後端組態由 switch-llm.sh 產生，這裡只 source 不寫死。
# 用環境變數而非只靠 appsettings.Development.json：後者是 reloadOnChange，
# 別的 session 一還原該檔，執行中的 app 立刻吃到舊值、不必重啟就中招。
# 環境變數優先且不熱重載，才守得住。
# shellcheck disable=SC1091
[ -f "$CACHE/llm-profile.env" ] && . "$CACHE/llm-profile.env"

# --- 啟動前自我修復推論端點 -------------------------------------------------
# win 通道是一次性的：WSL 一重開機，Windows 端那支 ssh -N 就斷了且不會自己重拉，
# 症狀是「模型明明在、卻連不上推論服務」（2026-08-31 實際踩到，空窗 39 分鐘）。
#
# 放在這裡而不是設 Windows 開機自啟，是因為**觸發時機**：
# 開機自啟是「Windows 登入時」跑，而 `wsl --shutdown` 不需要重新登入 Windows。
# 「app 啟動」才是唯一與「WSL 重開機」對得上的時機——重開機後你本來就要重啟 app。
#
# 刻意不致命：端點拉不起來時 app 照常啟動。檢索的詞彙通道不需要任何模型，
# 推論服務沒開時它必須照常運作（§10 的「嵌入失敗只降級、不中止」）。
"$PROJ/tools/switch-llm.sh" ensure \
  || echo "⚠ 推論端點拉不起來，app 仍照常啟動（Q&A 會降級，不會整個掛掉）"

exec "$PROJ/bin/Debug/net10.0/SmartFactory.MES"
