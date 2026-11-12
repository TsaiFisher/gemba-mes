#!/bin/bash
# 災難復原演練：用正式的備份腳本備份 → 還原到全新的資料庫 → 逐表驗證 → （可選）把 app 接上去開機 → 寫報告。
#
# 用法：
#   SRC_DB=ProjectZ scripts/dr-drill.sh                       # 真實資料：只備份＋還原＋驗證，不啟動 app
#   SRC_DB=ProjectZ_E2E DR_BOOT=1 scripts/dr-drill.sh         # 合成資料：另外把 app 接到還原庫開機，量到 /health 的時間
#
# 為什麼要有：scripts/backup-db.sh 寫了還原程序，也說「還原演練建議排入季度」，但在 2026-10-21 之前從沒有人
# 真的還原過一次。備份檔能列出目錄（pg_restore --list）不代表還原得回來、還原回來的資料是完整的、
# 或 app 接得上去——這三件事只有實際做一次才知道，而真正需要它的那天沒有時間試錯。
#
# 環境變數：
#   SRC_DB          來源資料庫（必填）。只讀：pg_dump 不會改動來源。
#   PGHOST/PGPORT/PGUSER/PGPASSWORD  同 backup-db.sh（建議用 ~/.pgpass）
#   DR_DIR          演練產物目錄（預設 ~/.cache/dr-drill/<時間戳>）：備份檔、日誌、報告
#   DR_BOOT=1       還原後啟動 app（bin/Debug/net10.0/SmartFactory.MES.dll）接到還原庫，量開機到 /health 的時間。
#                   ⚠️ 只對合成資料用：app 會跑背景服務，真實資料上可能對真的手機推播、對真的收件人寄報表。
#                   這裡已關掉排程啟動補跑、報表工具與推播金鑰，但無法保證涵蓋未來新增的對外整合。
#   DR_PORT         開機驗證用的埠（預設 5119）
#   DR_KEEP=1       演練完不刪還原庫（預設刪除）
#   DR_KEEP_BACKUP=1 保留演練產生的備份檔（預設刪除：那是一份明文的完整資料，留在 ~/.cache 等於多一份沒人管的外洩面）
#   DR_SELFTEST=1   驗證本身的自我檢測：還原後故意刪掉一列，演練必須判定「未通過」（確認驗證不是永遠綠）
#   BACKUP_GPG_RECIPIENT／BACKUP_RCLONE_REMOTE  照 backup-db.sh；有設才演練到解密，報告會註明有沒有
set -euo pipefail

: "${SRC_DB:?請設定 SRC_DB（來源資料庫）}"
PGHOST="${PGHOST:-localhost}"; PGPORT="${PGPORT:-5432}"; PGUSER="${PGUSER:-dev}"
export PGHOST PGPORT PGUSER
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAMP=$(date +%Y%m%d-%H%M%S)
DR_DIR="${DR_DIR:-$HOME/.cache/dr-drill/$STAMP}"
DRILL_DB="dr_drill_${STAMP//-/_}"
DR_PORT="${DR_PORT:-5119}"
REPORT="$DR_DIR/report.md"
mkdir -p "$DR_DIR"
LOG="$DR_DIR/drill.log"
exec > >(tee -a "$LOG") 2>&1

now_ms() { date +%s%3N; }
psql_src() { psql -d "$SRC_DB" -v ON_ERROR_STOP=1 -At "$@"; }
psql_dr()  { psql -d "$DRILL_DB" -v ON_ERROR_STOP=1 -At "$@"; }
FAIL=0
note() { echo "$1" >> "$REPORT"; }
fail() { echo "❌ $1"; note "- ❌ $1"; FAIL=1; }

cleanup() {
    if [ -n "${APP_PID:-}" ]; then kill "$APP_PID" 2>/dev/null || true; fi
    if [ "${DR_KEEP:-0}" != 1 ]; then
        psql -d postgres -q -c "DROP DATABASE IF EXISTS \"$DRILL_DB\" WITH (FORCE)" >/dev/null 2>&1 || true
    fi
    [ "${DR_KEEP_BACKUP:-0}" = 1 ] || rm -rf "$DR_DIR/backup"
}
trap cleanup EXIT

# 每張表的列數（public schema 全部，不挑樣本——漏掉的那張就是還原不完整的那張）
COUNT_SQL="SELECT string_agg(format('SELECT %L, count(*) FROM %I', table_name, table_name), ' UNION ALL ' ORDER BY table_name)
           FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'"
counts() { local q; q=$($1 -c "$COUNT_SQL"); $1 -F '|' -c "$q"; }
# 幾個加總：數量對了但金額不對（截斷、型別轉換）也要看得出來
SUMS_SQL="SELECT 'JournalLines.Amount', coalesce(sum(\"Amount\"),0)::text FROM \"JournalLines\"
          UNION ALL SELECT 'JournalLines.AmountLocal', coalesce(sum(\"AmountLocal\"),0)::text FROM \"JournalLines\"
          UNION ALL SELECT 'Inventories.QtyOnHand', coalesce(sum(\"QtyOnHand\"),0)::text FROM \"Inventories\"
          UNION ALL SELECT 'InventoryCostLayers.QtyRemaining', coalesce(sum(\"QtyRemaining\"),0)::text FROM \"InventoryCostLayers\"
          UNION ALL SELECT 'LastMigration', max(\"MigrationId\") FROM \"__EFMigrationsHistory\""

{
    echo "# 災難復原演練 $STAMP"
    echo
    echo "- 來源：\`$SRC_DB\`@$PGHOST:$PGPORT（$(psql_src -c "SELECT pg_size_pretty(pg_database_size(current_database()))")）"
    echo "- 演練目錄：\`$DR_DIR\`"
    echo "- 加密：$([ -n "${BACKUP_GPG_RECIPIENT:-}" ] && echo "有（$BACKUP_GPG_RECIPIENT），本次演練含解密" || echo "**未設定**——備份是明文，生產環境務必啟用加密或異地其一")"
    echo "- 異地：$([ -n "${BACKUP_RCLONE_REMOTE:-}" ] && echo "有（$BACKUP_RCLONE_REMOTE）" || echo "**未設定**——備份與資料庫在同一台機器，機器毀損時一起不見")"
    echo
} > "$REPORT"

# ── 1. 備份前的來源狀態 ────────────────────────────────────────────────
echo "[$(date '+%F %T')] 1/5 記錄來源狀態"
counts psql_src > "$DR_DIR/src-before.txt"
psql_src -F '|' -c "$SUMS_SQL" > "$DR_DIR/src-sums-before.txt"

# ── 2. 用正式的備份腳本備份（同時驗證那支腳本本身）────────────────────────────
echo "[$(date '+%F %T')] 2/5 備份（scripts/backup-db.sh）"
T0=$(now_ms)
BACKUP_DIR="$DR_DIR/backup" PGDATABASE="$SRC_DB" RETENTION_DAYS=36500 UPLOADS_PATH="${UPLOADS_PATH:--}" \
    "$REPO_ROOT/scripts/backup-db.sh"
T1=$(now_ms)
BACKUP_AT=$(date '+%F %T')
DUMP=$(ls -1 "$DR_DIR/backup/${SRC_DB}"-*.dump* | grep -v files | head -1)
counts psql_src > "$DR_DIR/src-after.txt"
psql_src -F '|' -c "$SUMS_SQL" > "$DR_DIR/src-sums-after.txt"

# ── 3. 還原到全新的資料庫（模擬原機毀損、在新環境重建）─────────────────────────
echo "[$(date '+%F %T')] 3/5 還原到新資料庫 $DRILL_DB"
psql -d postgres -q -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"$DRILL_DB\""
T2=$(now_ms)
if [[ "$DUMP" == *.gpg ]]; then
    gpg --batch --decrypt "$DUMP" | pg_restore --no-owner --exit-on-error -d "$DRILL_DB"
else
    pg_restore --no-owner --exit-on-error -d "$DRILL_DB" "$DUMP"
fi
T3=$(now_ms)

if [ "${DR_SELFTEST:-0}" = 1 ]; then
    VICTIM=$(psql_dr -c "SELECT format('%I', c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                         WHERE n.nspname='public' AND c.relkind='r' AND c.reltuples > 0 ORDER BY c.relname LIMIT 1")
    psql_dr -q -c "SET session_replication_role = replica; DELETE FROM $VICTIM WHERE ctid = (SELECT ctid FROM $VICTIM LIMIT 1)"
    echo "[$(date '+%F %T')] 自我檢測：已從還原庫的 $VICTIM 刪掉一列，以下驗證必須失敗"
    note "- 自我檢測：還原後故意從 \`$VICTIM\` 刪掉一列"
fi

# ── 4. 驗證 ───────────────────────────────────────────────────────────
echo "[$(date '+%F %T')] 4/5 逐表驗證"
counts psql_dr > "$DR_DIR/restored.txt"
psql_dr -F '|' -c "$SUMS_SQL" > "$DR_DIR/restored-sums.txt"

TABLES=$(wc -l < "$DR_DIR/src-before.txt")
RESTORED_TABLES=$(wc -l < "$DR_DIR/restored.txt")
ROWS=$(awk -F'|' '{s+=$2} END{print s+0}' "$DR_DIR/restored.txt")
[ "$TABLES" = "$RESTORED_TABLES" ] || fail "資料表數不符：來源 $TABLES、還原 $RESTORED_TABLES"
# 來源在備份前後沒變的表：還原必須完全相等；有變的：還原必須落在前後之間
MISMATCH=$(join -t'|' <(sort "$DR_DIR/src-before.txt") <(sort "$DR_DIR/src-after.txt") \
    | join -t'|' - <(sort "$DR_DIR/restored.txt") \
    | awk -F'|' '{ lo=($2<$3)?$2:$3; hi=($2>$3)?$2:$3; if ($4<lo || $4>hi) print $1": 來源 "$2"→"$3"、還原 "$4 }')
CHANGED=$(join -t'|' <(sort "$DR_DIR/src-before.txt") <(sort "$DR_DIR/src-after.txt") | awk -F'|' '$2!=$3{c++} END{print c+0}')
if [ -n "$MISMATCH" ]; then while IFS= read -r m; do fail "列數不符 $m"; done <<< "$MISMATCH"; fi
if ! diff -q "$DR_DIR/src-sums-after.txt" "$DR_DIR/restored-sums.txt" >/dev/null \
   && ! diff -q "$DR_DIR/src-sums-before.txt" "$DR_DIR/restored-sums.txt" >/dev/null; then
    fail "加總不符：$(diff "$DR_DIR/src-sums-after.txt" "$DR_DIR/restored-sums.txt" | tr '\n' ' ')"
fi

# ── 5. （可選）把 app 接到還原庫開機 ─────────────────────────────────────────
BOOT_MS=""
if [ "${DR_BOOT:-0}" = 1 ]; then
    echo "[$(date '+%F %T')] 5/5 app 接到還原庫開機（:$DR_PORT）"
    DLL="$REPO_ROOT/bin/Debug/net10.0/SmartFactory.MES.dll"
    [ -f "$DLL" ] || { fail "找不到 $DLL（先 dotnet build）"; DLL=""; }
    if [ -n "$DLL" ]; then
        T4=$(now_ms)
        ( cd "$REPO_ROOT" && \
          ConnectionStrings__DefaultConnection="Host=$PGHOST;Port=$PGPORT;Database=$DRILL_DB;Username=$PGUSER;Password=${PGPASSWORD:-}" \
          ConnectionStrings__DefaultEncrypted="" ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$DR_PORT" \
          Jobs__RunAtStartup=false Features__ReportTools__Enabled=false Features__QaAssistant__Enabled=false \
          WebPush__VapidPrivateKey="" Security__TwoFactorEnforcementEnabled=false \
          exec dotnet "$DLL" > "$DR_DIR/app.log" 2>&1 ) &
        APP_PID=$!
        for _ in $(seq 1 240); do
            curl -sf "http://localhost:$DR_PORT/health" >/dev/null 2>&1 && { BOOT_MS=$(( $(now_ms) - T4 )); break; }
            kill -0 "$APP_PID" 2>/dev/null || break
            sleep 0.5
        done
        if [ -z "$BOOT_MS" ]; then fail "app 接到還原庫後 /health 沒有回應（見 app.log）"
        else
            code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$DR_PORT/Identity/Account/Login")
            [ "$code" = 200 ] || fail "登入頁回 HTTP $code"
            grep -q "Applying migration" "$DR_DIR/app.log" && fail "開機時套用了 migration——還原庫的 schema 落後於這版程式"
        fi
        kill "$APP_PID" 2>/dev/null || true; wait "$APP_PID" 2>/dev/null || true; APP_PID=""
    fi
else
    echo "[$(date '+%F %T')] 5/5 略過開機驗證（DR_BOOT=1 才做，真實資料不建議）"
fi

# ── 報告 ────────────────────────────────────────────────────────────
{
    echo "## 結果：$([ $FAIL = 0 ] && echo "✅ 通過" || echo "❌ 未通過")"
    echo
    echo "| 項目 | 數值 |"
    echo "|---|---|"
    echo "| 備份耗時 | $(( (T1-T0) )) ms（含完整性驗證） |"
    echo "| 備份檔 | \`$(basename "$DUMP")\`，$(du -h "$DUMP" | cut -f1) |"
    echo "| 還原耗時 | $(( (T3-T2) )) ms |"
    [ -n "$BOOT_MS" ] && echo "| app 開機到 /health | $BOOT_MS ms |"
    echo "| 資料表 | $RESTORED_TABLES 張（來源 $TABLES 張），合計 $ROWS 列 |"
    echo "| 備份期間來源有變動的表 | $CHANGED 張 |"
    echo "| 加總（傳票金額、庫存在手、成本層剩餘、最後 migration） | $(tr '\n' ';' < "$DR_DIR/restored-sums.txt") |"
    echo
    echo "**RTO（本次實測）**：還原 $(( (T3-T2)/1000 )) 秒$([ -n "$BOOT_MS" ] && echo "＋開機 $((BOOT_MS/1000)) 秒")，不含取得備份檔與準備主機的時間。"
    echo
    echo "**RPO**：等於備份間隔。目前的排程是每日 02:30（backup-db.sh 檔頭），最壞情況遺失將近 24 小時的資料；"
    echo "要縮短得加 WAL 歸檔做時間點還原（PITR），或提高備份頻率。本次備份時間 $BACKUP_AT。"
} >> "$REPORT"

echo "[$(date '+%F %T')] 報告：$REPORT"
cat "$REPORT"
exit $FAIL
