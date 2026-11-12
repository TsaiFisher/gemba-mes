-- ============================================================================
-- OrganizationUnitPath 歷史資料盤點（唯讀，跑回填前必須先跑這支）
--
-- 背景：Data/ApplicationDbContext.cs:186-203 的資料權限過濾器帶一條 legacy 放行——
--   OrganizationUnitPath 為空的列「視為 DataAuth 啟用前的歷史資料，一律公開」。
-- 在單據畫面上這只是偶爾多看到一筆；在彙總報表上，這批列會被無聲加總進金額，
-- 而且數字自洽，沒有人會發現。詳見 llm-qa-and-report-tools.md §8.3（待決 #3／#11）。
--
-- 待決 #3 的裁決是「先盤點筆數再定」——這支就是那個盤點，回答四個問題：
--   A. 每張表有多少列吃到 legacy 放行
--   B. 其中有多少列「回填得出來」（CreatedByRoleId → 角色 → 組織單位有 Path）
--   C. 有多少列回填不出來，以及卡在哪一段（沒有建立者角色／角色沒掛組織單位）
--   D. 回填後這批列會落到哪些路徑下——這一項決定「誰會因此看不到原本看得到的資料」
--
-- 表的清單是用 information_schema 動態找的（同時有 OrganizationUnitPath 與
-- CreatedByRoleId 兩欄者），所以日後新增實體不需要回頭改這支腳本。
--
-- 用法：psql -f scripts/orgpath-inventory.sql
-- ============================================================================

\set ON_ERROR_STOP on

DROP TABLE IF EXISTS orgpath_inventory;
CREATE TEMP TABLE orgpath_inventory (
    table_name          text,
    rows_total          bigint,   -- 該表總列數
    legacy_blank        bigint,   -- OrganizationUnitPath 為 NULL 或空字串（＝吃到 legacy 放行）
    fillable            bigint,   -- 上述之中，回填得出路徑的
    blocked_no_role     bigint,   -- 卡住：CreatedByRoleId 也是空的
    blocked_role_no_ou  bigint    -- 卡住：有角色，但角色沒掛組織單位或組織單位沒有 Path
);

DO $do$
DECLARE
    tbl text;
BEGIN
    FOR tbl IN
        SELECT c."table_name"
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
            INSERT INTO orgpath_inventory
            SELECT %L,
                   count(*),
                   count(*) FILTER (WHERE s.blank),
                   count(*) FILTER (WHERE s.blank AND coalesce(s.ou_path,'') <> ''),
                   count(*) FILTER (WHERE s.blank AND s.role_id IS NULL),
                   count(*) FILTER (WHERE s.blank AND s.role_id IS NOT NULL
                                      AND coalesce(s.ou_path,'') = '')
            FROM (
                SELECT x."CreatedByRoleId"                          AS role_id,
                       coalesce(x."OrganizationUnitPath", '') = ''  AS blank,
                       ou."Path"                                    AS ou_path
                FROM %I x
                LEFT JOIN "AspNetRoles"      r  ON r."Id" = x."CreatedByRoleId"
                LEFT JOIN "OrganizationUnits" ou ON ou."Id" = r."OrganizationUnitId"
            ) s
        $f$, tbl, tbl);
    END LOOP;
END
$do$;

\echo ''
\echo '=== A／B／C. 逐表盤點（只列出真的有 legacy 列的表，多的排前面）==='
SELECT table_name,
       rows_total,
       legacy_blank,
       round(100.0 * legacy_blank / nullif(rows_total, 0), 1) AS legacy_pct,
       fillable,
       blocked_no_role,
       blocked_role_no_ou
FROM orgpath_inventory
WHERE legacy_blank > 0
ORDER BY legacy_blank DESC, table_name;

\echo ''
\echo '=== 總計（決定要不要做、以及做完還剩多少缺口）==='
SELECT count(*) FILTER (WHERE legacy_blank > 0) AS tables_affected,
       sum(rows_total)         AS rows_total,
       sum(legacy_blank)       AS legacy_blank,
       sum(fillable)           AS fillable,
       sum(blocked_no_role)    AS blocked_no_role,
       sum(blocked_role_no_ou) AS blocked_role_no_ou
FROM orgpath_inventory;

\echo ''
\echo '=== 掃描涵蓋率自檢（掃到 0 張表＝條件失效，不是「沒有問題」）==='
SELECT count(*) AS tables_scanned FROM orgpath_inventory;

\echo ''
\echo '=== D. 回填後這批列會落到哪些路徑（＝可見範圍會收斂成什麼樣）==='
\echo '    每一列代表「原本全公司都看得到、回填後只有該路徑的上層看得到」的一批資料。'
DO $do$
DECLARE
    tbl text;
BEGIN
    DROP TABLE IF EXISTS orgpath_target_preview;
    CREATE TEMP TABLE orgpath_target_preview (target_path text, affected_rows bigint);

    FOR tbl IN SELECT table_name FROM orgpath_inventory WHERE fillable > 0
    LOOP
        EXECUTE format($f$
            INSERT INTO orgpath_target_preview
            SELECT ou."Path", count(*)
            FROM %I x
            JOIN "AspNetRoles"       r  ON r."Id" = x."CreatedByRoleId"
            JOIN "OrganizationUnits" ou ON ou."Id" = r."OrganizationUnitId"
            WHERE coalesce(x."OrganizationUnitPath", '') = ''
              AND coalesce(ou."Path", '') <> ''
            GROUP BY ou."Path"
        $f$, tbl);
    END LOOP;
END
$do$;

SELECT target_path, sum(affected_rows) AS affected_rows
FROM orgpath_target_preview
GROUP BY target_path
ORDER BY 2 DESC;

\echo ''
\echo '=== 前置健檢一：有沒有角色沒掛組織單位（這些角色建的資料回填不了）==='
SELECT r."Id" AS role_id, r."Name" AS role_name,
       r."OrganizationUnitId",
       ou."Path"
FROM "AspNetRoles" r
LEFT JOIN "OrganizationUnits" ou ON ou."Id" = r."OrganizationUnitId"
WHERE coalesce(ou."Path", '') = ''
ORDER BY 1;

\echo ''
\echo '=== 前置健檢二：Path 前綴互相包含（過濾器用 StartsWith，會多看到不該看的）==='
\echo '    例：Root/Sales 這個角色會連 Root/SalesOps 的資料一起看到。'
\echo '    這是既有過濾器語意、不是回填造成的，但回填後這批資料才真正開始受它影響。'
SELECT a."Path" AS shorter_path, b."Path" AS also_matched_path
FROM "OrganizationUnits" a
JOIN "OrganizationUnits" b
  ON b."Path" <> a."Path"
 AND b."Path" LIKE a."Path" || '%'
 AND substr(b."Path", length(a."Path") + 1, 1) <> '/'
WHERE coalesce(a."Path", '') <> ''
ORDER BY 1, 2;

DROP TABLE IF EXISTS orgpath_target_preview;
DROP TABLE IF EXISTS orgpath_inventory;
