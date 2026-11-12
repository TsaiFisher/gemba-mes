#!/bin/bash
# PostgreSQL 資料庫備份腳本（生產環境 migration 前必跑）。
#
# 用法：
#   scripts/backup-db.sh                          # 用環境變數/預設值
#   PGHOST=db PGDATABASE=SmartFactoryMES scripts/backup-db.sh
#
# 連線參數（標準 PG 環境變數，未設定時用開發預設值）：
#   PGHOST（預設 localhost）、PGPORT（5432）、PGDATABASE（SmartFactoryMES）、
#   PGUSER（dev）、PGPASSWORD（建議用 ~/.pgpass，勿寫死在排程內）
#
# L3 安全強化（可選，未設定時維持原明文本機行為）：
#   BACKUP_GPG_RECIPIENT   設定後以 gpg 公鑰加密備份（產物 .dump.gpg，明文即刪）。
#                          例：BACKUP_GPG_RECIPIENT=backup@corp.com（該金鑰須先匯入）。
#   BACKUP_RCLONE_REMOTE   設定後將（加密後的）備份 rclone copy 至異地。
#                          例：BACKUP_RCLONE_REMOTE=offsite:mes-backups
#
# 檔案備份（待決 #20 / deploy/file-backup.md，2026-08-31 實作）：
#   REPORTS_PATH           排程報表的落地目錄，**即組態 `Reports:StoragePath`**。P2 上線後才存在。
#   UPLOADS_PATH           上傳檔目錄（公司 logo 等），預設為本 repo 的 wwwroot/uploads。
#                          設 UPLOADS_PATH=- 可明確跳過。
#   兩者收進**同一個 files 包**，與 DB dump 共用時間戳，加密／異地／保留期全部共用。
#   都沒有內容時只備 DB（開發區的預設行為，不變）。
#   ⚠️ 報表檔含機密等級資料（§8.1 裁定 28 支為機密），
#      所以它跟 DB 一樣要走上面的加密與異地，不可只丟在本機。
#   ⚠️ **刻意不把 pg_dump 產物也塞進那個 tar**（file-backup.md 原文寫「同一個壓縮包」）：
#      包起來就不能直接 pg_restore --list，而那道完整性驗證是這支腳本現有的守門。
#      改用「同一次執行、同一個時間戳的兩個產物」達成同樣的對齊性質。
#   ⚠️ 生產務必至少啟用加密＋異地其一，否則備份明文落單機＝單點資料外洩/遺失風險。
#
# 還原程序（rollback runbook）：
#   1. 停止應用程式（systemctl stop smartfactory 或對應服務）
#   2a.（未加密）pg_restore --clean --if-exists -d "$PGDATABASE" <備份檔.dump>
#   2b.（已加密）gpg --decrypt <備份檔.dump.gpg> | pg_restore --clean --if-exists -d "$PGDATABASE"
#   2c.（有檔案包）tar -xzf <備份檔-files.tar.gz> -C <目標父目錄>
#      包內是相對路徑（reports/、uploads/），解到對應的父目錄即可
#      ——順序無所謂，但兩者必須取自**同一次執行**（檔名的時間戳相同），否則
#      ReportRun 列與檔案對不上，下載會 404。
#      （--clean 會先 DROP 再重建；確認對的是目標資料庫！）
#   3. 若是要回滾某次 migration：還原備份後，部署「對應舊版」的應用程式，
#      勿讓新版啟動時 AutoMigrate 又把 schema 推回去
#      （生產環境應設 "Database:AutoMigrate": false）
#   4. 啟動應用程式並驗證 /health
#   ※ 還原演練建議排入季度，確認加密備份可實際解密還原：scripts/dr-drill.sh（2026-10-21 起），
#     說明與結果見 deploy/disaster-recovery.md。
#
# 建議排程（crontab，每日 02:30 + migration 前手動）：
#   30 2 * * * /path/to/repo/scripts/backup-db.sh >> /var/log/smartfactory-backup.log 2>&1
#   WSL 穩定版用 systemd timer（deploy/wsl/mes-backup.timer，Persistent=true：關機錯過的那次開機補跑）。
#   ⚠️ 2026-10-21 演練時發現穩定版機器從沒排過任何備份——寫好腳本不等於有在備份。
set -euo pipefail

PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGDATABASE="${PGDATABASE:-SmartFactoryMES}"
PGUSER="${PGUSER:-dev}"
export PGHOST PGPORT PGDATABASE PGUSER

BACKUP_DIR="${BACKUP_DIR:-$HOME/db-backups}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$BACKUP_DIR/${PGDATABASE}-${STAMP}.dump"
FILES_TAR="$BACKUP_DIR/${PGDATABASE}-${STAMP}-files.tar.gz"

mkdir -p "$BACKUP_DIR"

echo "[$(date '+%F %T')] 備份 $PGDATABASE@$PGHOST:$PGPORT → $OUT"
# -Fc 自訂格式：壓縮、可用 pg_restore 選擇性還原
pg_dump -Fc --no-owner -f "$OUT"

SIZE=$(du -h "$OUT" | cut -f1)
echo "[$(date '+%F %T')] ✅ 備份完成（$SIZE）"

# 驗證備份可讀（列出內容目錄即可，不實際還原）— 須在加密前對明文驗證
pg_restore --list "$OUT" > /dev/null
echo "[$(date '+%F %T')] ✅ 備份檔完整性驗證通過（pg_restore --list）"

# ── 報表檔（待決 #20）：與 DB 同一次打包 ─────────────────────────────────────
#
# **順序是刻意的：先 dump DB，再打包報表目錄。** 反過來會壞。
#   DB 先、報表後 → 中間新增的報表：檔案在包裡、`ReportRun` 列不在 DB → 孤兒檔，無害。
#   報表先、DB 後 → 中間新增的報表：列在 DB、檔案不在包裡 → **還原後下載 404**。
# 兩者都不是「同一時點」，但一邊的破法是多一個檔案，另一邊是壞掉的連結。
ARTIFACTS=("$OUT")
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UPLOADS_PATH="${UPLOADS_PATH:-$REPO_ROOT/wwwroot/uploads}"

TAR_ARGS=()
for D in "${REPORTS_PATH:-}" "$UPLOADS_PATH"; do
    [ -z "$D" ] || [ "$D" = "-" ] && continue
    if [ -d "$D" ]; then
        # -C 進到父目錄再打包，避免把絕對路徑寫進 tar（還原時才不會強制落回原路徑）
        TAR_ARGS+=(-C "$(dirname "$D")" "$(basename "$D")")
    else
        # 不視為錯誤：排程還沒產出任何報表、或還沒有人上傳過 logo 時，目錄本來就不存在。
        # 但要出聲——安靜跳過會讓「以為有備到檔案」一路撐到還原當天才發現。
        echo "[$(date '+%F %T')] ⚠️  $D 不存在，未納入本次備份"
    fi
done

if [ ${#TAR_ARGS[@]} -gt 0 ]; then
    tar -czf "$FILES_TAR" "${TAR_ARGS[@]}"
    FSIZE=$(du -h "$FILES_TAR" | cut -f1)
    echo "[$(date '+%F %T')] ✅ 檔案已打包（$FSIZE）→ $FILES_TAR"
    ARTIFACTS+=("$FILES_TAR")
else
    echo "[$(date '+%F %T')] · 沒有可備份的檔案目錄，本次只備 DB"
fi

# ── L3：可選加密（設定 BACKUP_GPG_RECIPIENT 才啟用）──────────────────────────
FINALS=()
for A in "${ARTIFACTS[@]}"; do
FINAL="$A"
if [ -n "${BACKUP_GPG_RECIPIENT:-}" ]; then
    command -v gpg >/dev/null || { echo "❌ 已設 BACKUP_GPG_RECIPIENT 但找不到 gpg" >&2; exit 1; }
    # 加密產物一律跟著**當前這一份** $A 命名，不可寫死 $OUT：
    # 迴圈跑的是 (DB dump, 檔案包) 兩份，寫死 $OUT 會讓第二圈用 --yes 蓋掉第一圈的
    # 密文，並在下一行把明文刪掉——結果是只剩一個叫 .dump.gpg、內容卻是檔案包的檔，
    # DB 備份**靜默消失**（腳本照樣 exit 0 並印兩行 🔒）。保留期的 find 樣式本來就
    # 收 *-files.tar.gz.gpg，可見原意就是逐份命名。
    ENC="$A.gpg"
    # --trust-model always：排程場景免互動信任提示；金鑰管理由匯入時把關
    gpg --yes --batch --trust-model always --encrypt \
        --recipient "$BACKUP_GPG_RECIPIENT" --output "$ENC" "$A"
    rm -f "$A"            # 明文即刪，僅保留加密副本
    FINAL="$ENC"
    echo "[$(date '+%F %T')] 🔒 已以 GPG 加密（recipient=$BACKUP_GPG_RECIPIENT）→ $FINAL"
fi
FINALS+=("$FINAL")
done

# ── L3：可選異地同步（設定 BACKUP_RCLONE_REMOTE 才啟用）─────────────────────
if [ -n "${BACKUP_RCLONE_REMOTE:-}" ]; then
    command -v rclone >/dev/null || { echo "❌ 已設 BACKUP_RCLONE_REMOTE 但找不到 rclone" >&2; exit 1; }
    rclone copy "${FINALS[@]}" "$BACKUP_RCLONE_REMOTE" 2>/dev/null \
        || for F in "${FINALS[@]}"; do rclone copy "$F" "$BACKUP_RCLONE_REMOTE"; done
    echo "[$(date '+%F %T')] ☁️  已異地同步 ${#FINALS[@]} 份 → $BACKUP_RCLONE_REMOTE"
fi

# 清除過期備份（同時涵蓋 .dump 與 .dump.gpg）
# 報表包一併納入，且用**同一個 RETENTION_DAYS**——待決 #20 要求保留期與
# `Reports:RetentionDays` 一致，兩份分開過期會讓還原點對不齊（DB 還得到、報表已刪）。
DELETED=$(find "$BACKUP_DIR" \( -name "${PGDATABASE}-*.dump" -o -name "${PGDATABASE}-*.dump.gpg" \
       -o -name "${PGDATABASE}-*-files.tar.gz" -o -name "${PGDATABASE}-*-files.tar.gz.gpg" \) \
    -mtime +"$RETENTION_DAYS" -print -delete | wc -l)
[ "$DELETED" -gt 0 ] && echo "[$(date '+%F %T')] 已清除 $DELETED 份超過 ${RETENTION_DAYS} 天的舊備份"

echo "[$(date '+%F %T')] 完成"
