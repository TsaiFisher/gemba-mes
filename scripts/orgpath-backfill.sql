-- ============================================================================
-- OrganizationUnitPath 歷史資料回填（預設試跑，不會改到資料）
--
-- 前置：**必須先跑 scripts/orgpath-inventory.sql 並看過結果**。
--       這支會改變既有資料的可見範圍，不是可以隨手跑一下的維護作業。
--
-- 回填規則（只有這一條，沒有例外）：
--   OrganizationUnitPath 為空的列 ← 其 CreatedByRoleId 所屬角色的組織單位 Path
--   對齊 Data/ApplicationDbContext.cs:258 `entry.Entity.OrganizationUnitPath ??= CurrentRolePath;`
--   ——也就是「這筆資料當初若是今天建的，會被寫成什麼」。
--
-- 補不出來的列（沒有 CreatedByRoleId，或角色沒掛組織單位）**維持空值不動**，
-- 繼續吃 legacy 放行。這是刻意的：猜一個路徑填進去，比留著已知的缺口更糟。
--
-- 冪等：條件限定 OrganizationUnitPath 為空，重跑不會覆蓋已經有值的列。
--
-- 用法：
--   試跑（預設，跑完自動 ROLLBACK）：
--       psql -f scripts/orgpath-backfill.sql
--   真的套用：
--       psql -v apply=1 -f scripts/orgpath-backfill.sql
--
-- ⚠️ 套用前請先備份：scripts/backup-db.sh
-- ============================================================================

\set ON_ERROR_STOP on

\if :{?apply}
\else
  \set apply 0
\endif

BEGIN;

DROP TABLE IF EXISTS orgpath_backfill_log;
CREATE TEMP TABLE orgpath_backfill_log (table_name text, rows_updated bigint);

DO $do$
DECLARE
    tbl text;
    n   bigint;
BEGIN
    FOR tbl IN
        SELECT c.table_name
        FROM information_schema.columns c
        WHERE c.table_schema = 'public'
          AND c.column_name  = 'OrganizationUnitPath'
          AND EXISTS (
              SELECT 1 FROM information_schema.columns c2
              WHERE c2.table_schema = c.table_schema
                AND c2.table_name   = c.table_name
                AND c2.column_name  = 'CreatedByRoleId')
        ORDER BY 1
    LOOP
        EXECUTE format($f$
            UPDATE %I x
            SET "OrganizationUnitPath" = ou."Path"
            FROM "AspNetRoles" r
            JOIN "OrganizationUnits" ou ON ou."Id" = r."OrganizationUnitId"
            WHERE r."Id" = x."CreatedByRoleId"
              AND coalesce(x."OrganizationUnitPath", '') = ''
              AND coalesce(ou."Path", '') <> ''
        $f$, tbl);

        GET DIAGNOSTICS n = ROW_COUNT;
        IF n > 0 THEN
            INSERT INTO orgpath_backfill_log VALUES (tbl, n);
        END IF;
    END LOOP;
END
$do$;

\echo ''
\echo '=== 本次回填筆數（逐表）==='
SELECT table_name, rows_updated
FROM orgpath_backfill_log
ORDER BY rows_updated DESC, table_name;

\echo ''
\echo '=== 合計 ==='
SELECT count(*) AS tables_touched, coalesce(sum(rows_updated), 0) AS rows_updated
FROM orgpath_backfill_log;

\echo ''
\echo '=== 回填後仍為空值的殘留（＝仍吃 legacy 放行，需人工處理或接受）==='
DO $do$
DECLARE
    tbl text;
    n   bigint;
BEGIN
    DROP TABLE IF EXISTS orgpath_remaining;
    CREATE TEMP TABLE orgpath_remaining (table_name text, still_blank bigint);

    FOR tbl IN
        SELECT c.table_name
        FROM information_schema.columns c
        WHERE c.table_schema = 'public'
          AND c.column_name  = 'OrganizationUnitPath'
          AND EXISTS (
              SELECT 1 FROM information_schema.columns c2
              WHERE c2.table_schema = c.table_schema
                AND c2.table_name   = c.table_name
                AND c2.column_name  = 'CreatedByRoleId')
        ORDER BY 1
    LOOP
        EXECUTE format(
            'SELECT count(*) FROM %I WHERE coalesce("OrganizationUnitPath", '''') = ''''', tbl)
        INTO n;
        IF n > 0 THEN
            INSERT INTO orgpath_remaining VALUES (tbl, n);
        END IF;
    END LOOP;
END
$do$;

SELECT table_name, still_blank FROM orgpath_remaining ORDER BY still_blank DESC, table_name;

\if :apply
  COMMIT;
  \echo ''
  \echo '### 已套用（COMMIT）。請依 deploy/orgpath-backfill.md 第四節做套用後驗證。'
\else
  ROLLBACK;
  \echo ''
  \echo '### 試跑模式，已 ROLLBACK，資料未變更。'
  \echo '### 確認上面的筆數無誤後，改用：psql -v apply=1 -f scripts/orgpath-backfill.sql'
\endif
