# 災難復原：備份、還原與演練

> 2026-10-21 第一次實際演練（`scripts/dr-drill.sh`）。在這之前，`scripts/backup-db.sh` 寫了還原程序，也說「還原演練建議排入季度」，
> 但沒有人真的還原過一次——而且**穩定版這台機器根本沒有排程備份**（沒有 crontab、沒有 systemd timer、`~/db-backups` 不存在）。

## 現況與風險（2026-10-21）

| 項目 | 現況 | 風險 |
|---|---|---|
| 排程備份 | 2026-10-21 起每日 12:30（mes-backup.timer） | 之前完全沒有 |
| 加密 | 未設定（明文 `.dump`） | 備份檔外流＝資料外流 |
| 異地 | 未設定 | 備份與資料庫在同一台機器，機器毀損時一起不見 |
| RPO（最多遺失多少） | 每日備份時最壞將近 24 小時 | 一整個班的報工、收發料 |
| RTO（還原要多久） | 實測：還原 2.7 秒（55 MB）＋開機約 4 秒 | 不含取得備份檔、準備主機的時間 |

## 第一步：啟用每日備份（穩定版 WSL）

```bash
# 密碼放 ~/.pgpass（不要寫進服務檔）
echo 'localhost:5432:*:dev:<密碼>' >> ~/.pgpass && chmod 600 ~/.pgpass

cp ~/ProjectZ/deploy/wsl/mes-backup.service ~/ProjectZ/deploy/wsl/mes-backup.timer ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now mes-backup.timer
systemctl --user start mes-backup.service     # 立刻備一次，確認跑得起來
journalctl --user -u mes-backup.service -n 20
ls -la ~/db-backups
```

- 每日 12:30（這台 WSL 半夜通常沒開機，2026-10-21 改）；`Persistent=true` 讓「那個時間 WSL 沒開著」時，下次開機補跑一次。
- 加密與異地：建立 `~/.config/mes-backup.env`，設 `BACKUP_GPG_RECIPIENT=...`（金鑰要先匯入）與／或 `BACKUP_RCLONE_REMOTE=...`。
  **生產環境至少要啟用其一**——只放本機的備份，在機器毀損時與資料庫一起消失。
- 保留 14 天（`RETENTION_DAYS`）。報表檔與上傳檔一併打包（見 `backup-db.sh` 檔頭）。

## 還原程序（真的出事時）

1. 停 app：`systemctl --user stop mes-stable`（或 `mes dev` 那一邊）。
2. 取得最近一份備份（本機 `~/db-backups`，或異地 `rclone copy <remote>/<檔名> .`）；有加密的先 `gpg --decrypt`。
3. 還原到**新的**資料庫名稱，確認沒問題再切換（不要直接 `--clean` 覆蓋現有庫——萬一備份本身是壞的就什麼都不剩）：
   ```bash
   createdb ProjectZ_restored
   pg_restore --no-owner --exit-on-error -d ProjectZ_restored ProjectZ-<時間>.dump
   ```
4. 驗證：照 `scripts/dr-drill.sh` 第 4 步的做法比對資料表數、列數與加總；或直接把 app 接上去看 `/health` 與登入頁。
5. 切換：把連線字串指到還原庫（或停掉後把舊庫改名、還原庫改成原名），啟動 app。
6. 補資料：備份時間點之後的作業要從紙本、手機佇列（手機上沒送出的會在恢復連線後補送）或對方系統補登。

## 演練（每季一次，或每次升級 PostgreSQL／改備份腳本後）

```bash
# 真實資料：只備份＋還原＋驗證，不啟動 app（演練產生的明文備份檔結束後自動刪除）
SRC_DB=ProjectZ scripts/dr-drill.sh

# 合成資料：另外把 app 接到還原庫開機，量到 /health 的時間
SRC_DB=ProjectZ_E2E DR_BOOT=1 scripts/dr-drill.sh

# 驗證本身的自我檢測：故意刪一列，必須判定未通過
SRC_DB=ProjectZ_E2E DR_SELFTEST=1 scripts/dr-drill.sh
```

演練用正式的 `backup-db.sh` 備份（同時驗證那支腳本）、還原到一個全新的 `dr_drill_*` 資料庫、比對**所有資料表**的列數
（備份前後來源都沒變的表必須完全相等）與幾個加總（傳票金額、庫存在手、成本層剩餘、最後一個 migration），結束後刪掉演練庫。
報告在 `~/.cache/dr-drill/<時間>/report.md`。

⚠️ **不要對真實資料用 `DR_BOOT=1`**：app 會跑背景服務，可能對真的手機推播、對真的收件人寄報表。腳本已關掉排程補跑、報表工具與推播金鑰，
但無法保證涵蓋未來新增的對外整合。

## 2026-10-21 演練結果

| 來源 | 資料表 | 列數 | 備份 | 還原 | 開機到 /health | 結果 |
|---|---:|---:|---:|---:|---:|---|
| 穩定版 ProjectZ（55 MB） | 191 | 26,745 | 1.5 s | 2.7 s | （不做） | ✅ 逐表與加總一致 |
| UI 測試庫（合成，28 MB） | 189 | 7,600 | 0.3 s | 2.2 s | 4.1 s | ✅ |
| 同上，自我檢測（刪一列） | | | | | | ❌（預期）：抓到 `APDebitNoteLines` 少一列 |

## 還沒做的

- **RPO 縮短**：要做到「最多遺失幾分鐘」得開 WAL 歸檔（`archive_mode=on`＋`archive_command`）做時間點還原（PITR），
  或把備份改成每小時。現場停線的代價決定值不值得。
- **異地備份實際演練**：本次沒有設定 rclone／gpg，所以「從異地取回並解密」這一段沒有演練到。設定後用同一支腳本再跑一次即可。
- **主機重建**：演練是在同一台機器上還原；換一台新機器時還要重新安裝授權（指紋綁主機，見授權申請程序）與 Security DLL。
