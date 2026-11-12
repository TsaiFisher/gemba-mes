#!/usr/bin/env bash
#
# get-fingerprint.sh
# 取得本機環境指紋（v2 錨定碼），演算法與 SmartFactory.MES 應用程式完全一致。
#
# 建議優先使用 SmartFactory.MES.MachineId 執行檔 —— 它與 app 共用同一份原始碼，
# 不可能漂移。本腳本是「機器上沒有 .NET runtime」時的備援，以及 CI 的輕量檢查。
#
# 用法：
#   bash tools/get-fingerprint.sh            顯示指紋
#   bash tools/get-fingerprint.sh --raw      同時顯示 raw 字串（診斷用）
#
# ⚠️ 本腳本不會產生錨定碼。錨定碼請用：
#       SmartFactory.MES.MachineId --init
#    自動產生會讓「授權失效」偽裝成「悄悄換了一個身分」，因此刻意不支援。

set -euo pipefail

ALGO="v2"
SHOW_RAW=false
[ "${1:-}" = "--raw" ] && SHOW_RAW=true

# ── 錨定碼取得順序（必須與 C# MachineAnchor.ResolveAnchor() 完全一致）─────────
#   1. 環境變數 SMARTFACTORY_INSTANCE_ID（僅 CI／開發；正式授權不接受，見 LicenseValidator.EnsureAnchorSourceAllowed）
#   2. 環境變數 SMARTFACTORY_ANCHOR_PATH 指定的檔案（設定後不再回退）
#   3. 系統位置 /etc/smartfactory/instance-id
#   4. 使用者位置 ~/.config/smartfactory/instance-id
SYSTEM_PATH="/etc/smartfactory/instance-id"
USER_PATH="${HOME}/.config/smartfactory/instance-id"

ANCHOR_ID=""
ANCHOR_SRC=""

read_anchor_file() {
    # $1=路徑；成功時設定 ANCHOR_ID / ANCHOR_SRC
    [ -f "$1" ] || return 1
    local content
    content=$(tr -d '[:space:]' < "$1" 2>/dev/null) || return 1
    [ -n "$content" ] || return 1
    # Windows DPAPI 封裝格式（DPAPI:v1:...）不能在這裡當成錨定碼直接雜湊——
    # 那會算出一個「看起來正常」但與 app 不一致的指紋，是靜默失效。
    # 本腳本是 Linux 備援，遇到封裝檔一律明確報錯，請改用 MachineId 執行檔。
    case "$content" in
        DPAPI:v1:*)
            echo "❌ 錨定檔為 Windows DPAPI 封裝格式，本腳本無法讀取：$1" >&2
            echo "   請改用 SmartFactory.MES.MachineId 執行檔取得指紋。" >&2
            exit 1
            ;;
    esac
    ANCHOR_ID="$content"
    ANCHOR_SRC="$1"
    return 0
}

if [ -n "${SMARTFACTORY_INSTANCE_ID:-}" ]; then
    ANCHOR_ID=$(printf '%s' "$SMARTFACTORY_INSTANCE_ID" | tr -d '[:space:]')
    ANCHOR_SRC="環境變數 SMARTFACTORY_INSTANCE_ID"
elif [ -n "${SMARTFACTORY_ANCHOR_PATH:-}" ]; then
    # 明確指定路徑時不回退到預設位置（與 C# 行為一致）
    read_anchor_file "$SMARTFACTORY_ANCHOR_PATH" || true
else
    read_anchor_file "$SYSTEM_PATH" || read_anchor_file "$USER_PATH" || true
fi

if [ -z "$ANCHOR_ID" ]; then
    echo "❌ 找不到機器錨定碼，無法計算環境指紋。" >&2
    echo "" >&2
    echo "已嘗試的位置：" >&2
    if [ -n "${SMARTFACTORY_ANCHOR_PATH:-}" ]; then
        echo "    ${SMARTFACTORY_ANCHOR_PATH}（由 SMARTFACTORY_ANCHOR_PATH 指定）" >&2
    else
        echo "    ${SYSTEM_PATH}" >&2
        echo "    ${USER_PATH}" >&2
    fi
    echo "（環境變數 SMARTFACTORY_INSTANCE_ID 僅供 CI／開發，正式授權不接受）" >&2
    echo "" >&2
    echo "請執行下列指令產生後重試：" >&2
    echo "    SmartFactory.MES.MachineId --init" >&2
    exit 1
fi

# ── 指紋 = SHA256(UTF8("v2:" + anchorId))，大寫 hex ──────────────────────────
RAW="${ALGO}:${ANCHOR_ID}"
FINGERPRINT=$(printf '%s' "$RAW" | sha256sum | awk '{print toupper($1)}')

echo "環境指紋："
echo "$FINGERPRINT"
echo ""
echo "錨定碼來源：${ANCHOR_SRC}"
echo "演算法：${ALGO}"
if [ "$SHOW_RAW" = "true" ]; then
    echo "（raw：${RAW}）"
fi
echo ""
# ⚠️ 這段文字與 Services/LicenseSupport.cs 的 HowToObtain 是同一份文案的兩份複本。
#    shell 引用不到 C# 常數，所以只能手動同步——改動時請一併 grep smartfactory-licensing。
echo "申請授權需要「申請碼」（含主機識別碼）：請改用 SmartFactory.MES.MachineId 取得申請碼與登記網址。"
echo "無法執行該工具時，可將上述指紋交給您的授權提供者（導入顧問）——只有指紋時只能核發每 90 天確認的授權。"
echo "申請方式：https://github.com/TsaiFisher/smartfactory-licensing"
