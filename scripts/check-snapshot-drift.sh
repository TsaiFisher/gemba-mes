#!/bin/bash
# EF snapshot 漂移檢查（merge=ours 地雷守門）。
#
# 背景：.gitattributes 對 ApplicationDbContextModelSnapshot.cs 設 merge=ours，
# merge upstream（Core）後若 Core 帶入新 entity/欄位，snapshot 不含該變更；
# 下一次 `dotnet ef migrations add` 會把 Core 已套用過的 DDL「重新」生成一次
# （重複/衝突 DDL，或誤生虛假欄位增刪）。
#
# 本腳本以 EF8 `has-pending-model-changes` 比對「目前模型 vs snapshot」：
# 不需資料庫連線。模型與 snapshot 不一致 → exit 1。
#
# ── 修復程序（每次 merge upstream/main 後若本檢查失敗）────────────────
#   1. dotnet ef migrations add CoreSnapshotSync --project SmartFactory.MES.csproj
#   2. 檢視生成的 Up()/Down()：
#      內容應「只」含 Core 該次帶入、且已由 Core 自己的 migration 套用過的 DDL
#      → 將 Up()/Down() 清空（保留空方法），snapshot 的更新保留。
#      ⚠️ 若出現非預期的欄位增刪（虛假 diff），逐項比對 Core 的 migration 修剪。
#   3. 重新執行本腳本確認通過後，將空 migration + snapshot 一併 commit。
#   （空 migration 無害：DB 端只在 __EFMigrationsHistory 多一筆紀錄。）
#
# ── 與 Core 的關係：**決定維持分歧，不上送**（2026-08-22 定案）─────────────
#    本檔登錄於 scripts/core-drift-baseline.txt。這不是「還沒上送」，是評估後的決定。
#
#    分歧的內容其實大部分是通用的：
#      · --context 必須明指（Core 只要日後多一個 DbContext 就會踩到同一個假訊號）
#      · 「建置失敗」與「真的有漂移」要分開（不分開會叫人產出一支假 migration）
#      · set -e 之下 out="$(…)" 後接 rc=$? 拿不到 exit code 的陷阱
#    **只有 `check_context QaDbContext` 那一行是 ProjectZ 專屬**（QA 是本專案的選用模組）。
#
#    不上送的理由：Core 目前只有一個 DbContext，這些修正對 Core 是空轉；
#    而 Core 這輩子只改過本檔一次（2974c60，建立它那次），分歧的維護成本趨近於零。
#    ⚠️ 代價是**明知留了一顆地雷在 Core**：Core 版沒有 --context，等 Core 自己長出
#    第二個 DbContext 的那天，它的 CI 會誤報漂移，而照它印出的指引做會產出一支假 migration。
#    這筆記在 STATUS〈Core 同步〉，不要因為「我們這邊修好了」就當作解決了。
#    **必須上送的唯一觸發條件：Core 出現第二個 DbContext。** 屆時把上下文清單改成
#    資料驅動（或由呼叫端傳入），兩邊即可完全一致、分歧歸零。
#
#    ⚠️ 本檔**刻意不設 merge=ours**（見 .gitattributes 該段註解）：它已被 baseline
#    以路徑白名單凍結，再加 merge=ours 會讓 Core 對它的修正在 merge 與守門兩邊都無聲消失。
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v dotnet-ef >/dev/null 2>&1 && ! dotnet ef --version >/dev/null 2>&1; then
    echo "⚠️  dotnet-ef 未安裝（dotnet tool install -g dotnet-ef），略過 snapshot 檢查"
    exit 0
fi

# ── --no-build：這一步 95% 的時間是在重建已經建好的東西 ────────────────────
#
# 2026-09-03 本機實測（閒置機器、Release 產物已是最新、單一 context）：
#     --configuration Release                57.8 秒
#     --configuration Release --no-build      3.8 秒
# CI 上這一步 200 秒（兩個 context），其中約 190 秒是純浪費——**根因不是建置組態，
# 是根本不該再建**。先前加 EF_BUILD_CONFIG（805c57b）只是把「全量重建」降成
# 「增量重建」，而增量重建本身就要 58 秒 × 2，所以效益一直量不出來。
#
# 只有在**產物真的在那裡**時才加 --no-build，而且找不到時要**大聲說**：
# 靜默退回重建的話，CI 上就只是「莫名其妙又慢了兩分鐘」，沒有人會去查。
# （本機呼叫端通常沒建過 Release，這時 EF_BUILD_CONFIG 根本沒設，走原本的路徑。）
#
# ⚠️ 找產物**不可以**寫成 `deps="$(ls … | head -1)"`：本檔開頭是 `set -euo pipefail`，
#    ls 找不到檔案時 pipeline 失敗 → 賦值失敗 → set -e 當場結束整支腳本，
#    連警告都印不出來（2026-09-03 第一版就是這樣寫的，實測直接靜默 exit）。
#    這與檔頭記過的 `out="$(…)"` 後接 `rc=$?` 是同一個陷阱的變形。用 glob 迴圈避開。
no_build=""
if [ -n "${EF_BUILD_CONFIG:-}" ]; then
    deps=""
    for d in bin/"$EF_BUILD_CONFIG"/*/SmartFactory.MES.deps.json; do
        if [ -f "$d" ]; then deps="$d"; break; fi
    done
    if [ -n "$deps" ]; then
        no_build="--no-build"
        echo "使用既有建置產物（--no-build）：$deps"
    else
        echo "⚠️  找不到 bin/${EF_BUILD_CONFIG}/*/SmartFactory.MES.deps.json——這一步會重新建置（約多花 2 分鐘）。"
        echo "    CI 上出現這行，代表前一步 dotnet build 的 --configuration 與 EF_BUILD_CONFIG 對不上。"
    fi
fi

echo "檢查 EF 模型與 snapshot 是否一致…"
# EF8 exit code：0 = 一致、非零 = 有 pending 變更。
# 不可改用 stdout 字串判定——警告（stderr）與結果行的交錯順序不穩定，
# tail -1 可能抓到警告行造成誤判（2026-06-12 實際發生過）。
#
# --context 必須明指：本 repo 自 2026-08-05 起有兩個上下文（ApplicationDbContext 與
# QA 選用模組的 QaDbContext）。省略 --context 時 EF 會以「找到多個 DbContext」報錯並回
# 非零 exit code，被本腳本誤判為 snapshot 漂移，再印出下方那段「常見於 merge upstream 後…」
# 的修復指引——完全不相干，會把人帶去查 Core 同步。
#
# 兩個上下文都要檢查：QaDbContext 的 snapshot 沒有 merge=ours（它不是 Core 檔案），
# 但「改了模型忘了產 migration」這個漂移對兩者是一樣的，而它同樣是啟動時才炸。
drift=0
toolchain_failed=0

check_context() {
    local ctx="$1"
    echo "  · ${ctx}"

    # ⚠️ 本檔開頭是 `set -euo pipefail`，所以**不可以**寫成
    #    `out="$(…)"` 後面再接 `rc=$?`——指令替換一失敗，set -e 會當場結束整支腳本，
    #    `rc=$?` 連跑都沒跑到，於是真的有漂移時反而什麼指引都不印就 exit 1。
    #    （舊版寫成 `if ! dotnet ef …; then` 之所以沒事，是因為條件式裡的指令豁免於 set -e。）
    #    下面的 `&& rc=0 || rc=$?` 是同樣豁免的寫法，保留賦值又拿得到 exit code。
    local out rc
    # ⚠️ `--configuration` 一定要跟呼叫端的建置組態一致，否則 dotnet ef 會**整包重建一次**。
    #    CI 上前一步是 `dotnet build --configuration Release`，而 dotnet ef 預設 Debug——
    #    於是同一份程式在同一個 job 裡被完整建了兩次。2026-09-03 量測：這一步 263 秒，
    #    佔整個 Build & Test job（827 秒）的 32%，是單一最耗時的步驟。
    #    本機通常 Debug→Debug 所以是增量、看不出差別，**只有在 CI 上才痛**——
    #    這也是為什麼它一直沒被發現。
    #    留成環境變數而不是寫死：本機呼叫端不見得建過 Release，寫死會反過來害本機重建。
    out="$(dotnet ef migrations has-pending-model-changes \
            --project SmartFactory.MES.csproj \
            ${EF_BUILD_CONFIG:+--configuration "$EF_BUILD_CONFIG"} \
            ${no_build:+$no_build} \
            --context "$ctx" 2>&1)" && rc=0 || rc=$?
    printf '%s\n' "$out"
    [ "$rc" -eq 0 ] && return 0

    # 非零 exit 有兩種來源，而它們的處理方式**完全相反**，不能混為一談：
    #   (a) 真的有待產的 migration → 產 migration；
    #   (b) 建置／還原根本沒跑起來（離線、NuGet 還原失敗、編譯錯）→ 先修建置。
    # 舊版一律當成 (a)，於是離線時會印出「執行 dotnet ef migrations add …」——
    # 照著做會生出一支內容為空或錯誤的假 migration。2026-08-13 在無網路環境實際踩到：
    # 兩個上下文都報漂移，加 --no-build 重跑後兩個都是
    # 「No changes have been made to the model since the last migration.」。
    # 這是同一類假訊號的第三次（前兩次見檔頭：2026-06-12 抓錯行、2026-08-05 漏 --context）。
    # ⚠️ 判斷式要涵蓋 --no-build 的失敗形狀。2026-09-03 實測：產物不存在時
    #    dotnet ef 回 **exit 129**、訊息是
    #      The specified deps.json [/…/bin/Bogus/net10.0/SmartFactory.MES.deps.json] does not exist
    #    ——**不含 "Build failed"**。只認那一句的話，這裡會判成「漂移」並印出
    #    「執行 dotnet ef migrations add …」的指引，照做就會產出一支假 migration。
    #    這正是本檔檔頭已經記過三次的同一類假訊號（2026-06-12 抓錯行、2026-08-05
    #    漏 --context、2026-08-13 離線），加 --no-build 就是第四次的入口，一併堵掉。
    #    "does not exist" 故意寫得寬：寧可把真漂移誤判成工具鏈問題（結果是 exit 2 的紅，
    #    訊息要人去看建置），也不要把工具鏈問題誤判成漂移（結果是有人產出一支假 migration）。
    # ⚠️ 2026-10-29 第五次：全新 clone 還沒 restore 時，dotnet ef 印的是
    #      error NETSDK1004: Assets file '…/obj/project.assets.json' not found
    #      Unable to retrieve project metadata. Ensure it's an SDK-style project.
    #    同樣不含 "Build failed"，被判成兩個上下文都漂移（pre-push-check 的 snapshot 步驟排在建置之前）。
    if printf '%s' "$out" | grep -qE "Build failed|does not exist|NETSDK1004|Unable to retrieve project metadata"; then
        echo "  ⛔ ${ctx}：**建置／產物問題，不是 snapshot 漂移**——先讓 dotnet build 過再重跑本腳本"
        toolchain_failed=1
    else
        echo "  ❌ ${ctx} 的 snapshot 與目前模型不一致"
        drift=1
    fi
}

check_context ApplicationDbContext
check_context QaDbContext

if [ "$toolchain_failed" -eq 1 ]; then
    echo ""
    echo "⛔ 建置失敗，本次**沒有檢查到** snapshot 狀態（既不是通過、也不是漂移）。"
    echo "   先跑 dotnet build 看實際錯誤；若是離線／NuGet 還原失敗，可用本機套件快取："
    echo "     dotnet restore --source \"\$HOME/.nuget/packages\""
    echo "   ⚠️ 不要照下面的漂移指引去產 migration——那會生出一支假的。"
    exit 2
fi

if [ "$drift" -eq 0 ]; then
    echo "✅ snapshot 與模型一致（兩個上下文）"
    exit 0
fi

echo ""
echo "❌ snapshot 與目前模型不一致"
echo "   ApplicationDbContext：常見於 merge upstream 後，merge=ours 保留了舊 snapshot，"
echo "   修復程序見本腳本檔頭註解（CoreSnapshotSync 空 migration 法）。"
echo "   QaDbContext：多半是改了 Modules/QA 的模型但沒產 migration，"
echo "   執行 dotnet ef migrations add <名稱> --context QaDbContext \\"
echo "        --output-dir Modules/QA/Data/Migrations"
exit 1
