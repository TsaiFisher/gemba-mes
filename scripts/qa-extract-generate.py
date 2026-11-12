#!/usr/bin/env python3
"""擷取產生器：把一份規格 .md 改寫成面向使用者的規則說明（規格 §10.11）。

產物有兩份，職責完全分開：

  1. `.claude/qa-extracts/<stem>.json`  —— manifest，**進版控**。
     每一則都寫死 `approved: false`。核可＝人改成 true 之後被 merge（定案 #45）。
     這支腳本永遠不會產出 `approved: true`，也不該有任何參數能讓它產出。

  2. 審核頁 `<review-dir>/<stem>.html` —— **不進版控**，每次重跑就重產。
     它是給懂規則的人看的：每一則擷取物並排它擷取自哪一段原文。

**為什麼不是全自動**：LLM 讀規格寫規則會幻覺，寫錯了沒有任何機制會發現，
而它會被當成權威答案引用（§10.11.4）。`Approved` 是唯一防線，不是流程裝飾。

用法：
    scripts/qa-extract-generate.py .claude/specifications/currency.md
    scripts/qa-extract-generate.py Modules/BAS/Services/UomService.cs   # 程式碼來源（§10.11.13）
    scripts/qa-extract-generate.py <來源> --limit 3        # 先確認管線會動
    scripts/qa-extract-generate.py <來源> --model phi4:latest

**推論端點**（桌機兩份 Ollama 都綁 11434 但在不同 namespace，§17.11.7）：
本腳本預設打 `127.0.0.1:11434`，那是 **WSL 內的備援那份**（只有 gemma3:4b）。
要用主力（Win11，含 gemma4:latest）得走 SSH 反向通道的 11435：

    ~/.cache/ollama-pull/switch-llm.sh status            # 先看現況
    ~/.cache/ollama-pull/switch-llm.sh win --no-restart  # 拉通道＋寫組態，不碰 app
    export QA_OLLAMA_URL=http://127.0.0.1:11435          # 這個 shell 之後都不必再加 --url

端點不通或模型不在時，`preflight` 會在跑第一段之前就擋下來並印出上面這幾行
——沒有它的話，症狀是每一段各自失敗一次，跑完才發現一則都沒產出。
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

# ── 來源閘門（§10.11.8 的判定表，編碼成程式碼） ──────────────────────────
#
# **這份清單不是文件，是閘門。** 收錄範圍在 §10.11.5 被定義為「唯一不可退讓的紅線」，
# 而紅線的失敗模式是無聲的：對 security-assessment-and-plan.md 手滑跑一次，
# 產出的草稿就躺在 .claude/qa-extracts/ 等著被誰順手核可。
# 靠人記得「哪些不能跑」是把唯一防線押在記性上，所以寫成程式碼並且預設拒絕。

REDLINE = {
    ".claude/specifications/llm-qa-and-report-tools.md",
    ".claude/specifications/report-permission-inventory.md",
    ".claude/specifications/security-assessment-and-plan.md",
    ".claude/specifications/archive/security-m3-2fa-design.md",  # 2026-10-08 移入 archive/（目錄本身也在 REDLINE_DIRS）
    "CONSULTANT-ONBOARDING.md",
}
# `.claude/specifications/archive/`（2026-09-30 起）：已完成章節的封存，內容是當時紀錄而非現況，
# 且其中的 llm-qa-and-report-tools-archive.md 就是上面紅線那份的本體——只擋原檔名，
# 搬到新檔名就會悄悄通過閘門，所以整個目錄列紅線。
REDLINE_DIRS = {".claude/reviews", ".claude/specifications/archive"}

# ❌ 不收：無終端使用者價值，或描述的不是現況（roadmap ＝未來式）。
# 與紅線分開列，因為兩者「被擋下來時該怎麼辦」不同：
# 紅線是絕不可收；這一類只是這次不收，日後真有理由可以改判。
EXCLUDED = {
    ".claude/specifications/roadmap-fin-cst-sub.md",
    ".claude/specifications/consistency-framework.md",
    ".claude/specifications/test-hardening-plan.md",
    ".claude/specifications/archive/mobile-pwa-appendix-unused-spa.md",  # 2026-10-08 移入 archive/
    ".claude/specifications/STATUS.md",
    "DEVELOPMENT.md",
    "MOBILE-ARCHITECTURE.md",
    "PRINT-ARCHITECTURE.md",
    "CLAUDE.md",
    ".claude/LOCAL_RULES.md",
    # 2026-08-17 補：全量統計時才發現這兩份通過了閘門。與上面幾份同類
    # （開發者導向、非終端使用者規則），只是原判定表按 .claude/specifications/ 逐份審，
    # 沒把 repo 根目錄的 .md 掃完。閘門宣稱預設拒絕，那漏掉的每一份都是它沒做到的部分。
    "README.md",
    "CHANGELOG.md",
    # 2026-08-17 裁決：手冊教學系統的**內部設計**（三支產生器、截圖工具住哪、規格細節），
    # 讀者是開發者不是終端使用者，與上面幾份同類。
    # 注意這不等於「教學功能本身不該被問到」——那屬於手冊，走手冊那條語料，不走擷取物。
    ".claude/specifications/user-manual-tutor.md",
    # 2026-08-24 改判（原判定「⚠️ 部分——決策紀錄，3 處待決須排除」）：整份不收。
    # 原判定要靠段落級強制排除擋掉三處待決，而那個規則**從來沒被寫進 SECTION_FORCE_EXCLUDE**，
    # 於是整份照收——14 則擷取物、核可 0 則。
    #
    # 但真正的問題比「漏設規則」更根本：這份文件的**開場白自己就寫著**
    # 「僅用於分析和提供建議，並不會實際修改任何會計上的意義……需要決策的部分
    # 必須由業務或財務人員決定後才能進行實作」。整份是待決事項，不是現況。
    # 定案 #39 說得很清楚：未來式與過期同樣有害，**都不是現況**。
    #
    # 而擷取改寫會把這件事抹掉：實際產出的兩則寫成「系統只會將後續沖銷的材料成本計入，
    # 導致單位成本偏低」——讀起來就是現行行為的權威描述，使用者無從得知這是一個
    # 還在討論、尚未裁決的議題。這正是 §10.11.1「原文比程式碼危險」的那一類。
    ".claude/specifications/reconciliation-decisions.md",
}

# ⚠️ 段落級強制排除：即使檔案可擷取，命中的段落一律不送進模型。
# 目前只有一筆，但這個機制必須先在，否則 mfg-domain.md 沒辦法跑
# ——它同時是全庫最有價值和最危險的一份（§10.11.8）。
#
# **比對標題與內文兩者，不能只比對標題。** 初版只比對 heading，實跑統計時發現
# 對 mfg-domain.md **命中 0 段**：該檔沒有任何標題含「缺口」或「繞過」，
# 「缺口 A」是第 3330 行的行內粗體。於是 §10.11.8 指名要擋的那三句
# （「不做備料直接開工…FPI 完全被繞過」「即使 RequiresFpi = true，只要不點備料就能開工」）
# 會照常送進模型——而這個失敗是無聲的：印出來的排除數是 0，看起來就像「這份沒東西要排」。
# 之所以一直沒被發現，是因為 mfg-domain.md 從沒跑過，只跑過 currency.md。
#
# pattern 收窄到「可照做的繞過指引」本身，不擴大到 `RequiresFpi`：
# 後者會連 §3.1 途程設計、§4.3 Dispatch 狀態機、§15.3 IPQC 一起砍掉（9 段），
# 而 §4.3 的「FPI 閘口可選：僅當 RequiresFpi = true 時…」正是該擷取的正面規則。
# 同理不用「繞過」單詞：§25.4 免排程直接開工是簡化模式的**正當設計**（定案 §25），不是缺口。
# 現行 pattern 命中 5 段（§25.14 待討論、§26.1–26.4 修復說明），全是開發用敘述。
SECTION_FORCE_EXCLUDE = {
    ".claude/specifications/mfg-domain.md": [
        re.compile(r"缺口\s*A|不做備料直接開工|FPI 完全被繞過|只要不點備料就能開工"),
    ],
}

# ── 開發導向章節：標題命中就不送模型（2026-08-17 加，全檔通用） ─────────────
#
# 全量審完 471 則之後回頭量的：**205 則退回裡有 129 則（63%）出自
# 「測試／規劃／建議／下一步」這類章節**——`business_data_flow_reconciliation`
# 光是「§5 資料流等價劃分與邊界值測試設計」就佔 48 則，全部是 QA 測試案例。
# 這些在送模型之前就擋得掉，省的不只是審核時間，還有生成時間
# （`mfg-domain` 跑了 38 分鐘，那 48 則測試案例是純白跑）。
#
# **詞是逐個量出來的，不是想出來的。** 把「測試｜規劃｜建議」整組拿來當閘門
# 會擋掉 129 則該退的，同時**誤砍 44 則該留的**，包含 FIFO 成本計算、
# 平均單價計算這種核心規則。逐詞的精確率：
#
#     Test Case／等價劃分  48 擋 / 0 誤   100%   ← 收
#     下一步               6 擋 / 0 誤   100%   ← 收
#     測試作業／測試覆蓋      8 擋 / 0 誤   100%   ← 收
#     API 規劃／實作補強      4 擋 / 0 誤   100%   ← 收
#     自動測試             16 擋 / 9 誤    64%   ← 不收
#     規劃                 55 擋 / 41 誤   57%   ← **絕不能收**
#
# 「規劃」為什麼是毒的：`15. 品質管理規劃`、`16. 生產成本規劃` 這種標題底下，
# 規劃的是**當初的設計**，內容早就實作了。**標題的時態不等於內容的時態。**
#
# 命中的段落一律列進審核頁（見 render_review 的分組），沿用 §10.11.11 ②
# 「被過濾掉的要印出來」——否則這就是一個無聲刪除機制。
SECTION_SKIP = re.compile(
    r"Test Case|等價劃分|下一步|測試作業|測試覆蓋|API 規劃|實作補強"
)

# ── 程式碼來源（§10.11.13／待決 #50 裁決為做） ──────────────────────────
#
# `.md` 那一側的閘門是**逐檔列舉、預設拒絕**（上面的 REDLINE／EXCLUDED）。
# 程式碼有 679 支產品碼 `.cs`，逐檔寫進這支腳本不現實，但**不能因此換成 regex 白名單**
# ——regex 是預設放行，679 支裡漏掉哪一支不會有任何跡象，而漏掉的可能就是授權或 2FA。
#
# 所以拆成兩道，方向相反、缺一不可：
#   ① CODE_SOURCES_FILE —— 進版控的逐檔清單，**沒列的一律拒絕**。它在 PR diff 裡看得到，
#      新增一支 `.cs` 進語料是一個要被審的動作，不是跑腳本的副作用。
#   ② CODE_REDLINE —— 檔名層的硬紅線，**列了也照樣拒絕**。清單是人維護的，
#      人會手滑把 `LicenseValidator.cs` 加進去；這一道守的是那一次手滑。
#   ③ CODE_REDLINE_BODY —— 段落層的硬紅線。①②比對的都是**檔名**，而授權／2FA
#      那種內容可以住在任何一支檔名乾淨的檔裡：`Program.cs:665` 就是「啟動時授權驗證」
#      整段（含 Internal License 的 clientCode），而 `Program.cs` 這個檔名不命中任何字。
#      **過濾的維度與內容所在的維度不同，就一定漏**——這個形狀的錯誤本專案已第四次
#      （簽核 0 則是假零、手機被歸進 Core 其他、退回名單表頭、以及這次）。
CODE_SOURCES_FILE = ".claude/qa-extracts/code-sources.txt"

CODE_REDLINE = re.compile(
    r"License|Fingerprint|TwoFactor|2fa|Totp|Recovery|Crypt|Password|Secret"
    r"|Token|Antiforgery|DataProtection|Auth(entication|orization)?(Handler|Service|Filter)?",
    re.IGNORECASE,
)

# 段落本文用的紅線。**刻意不直接沿用 `CODE_REDLINE`**：那一條是為檔名寫的，
# 拿去比對本文會把 `Auth`（`DataAuth` 資料權限）、`Token` 這類在正常規則裡也會出現的
# 字整批連坐掉，而資料權限「誰看得到哪些單」正是使用者最需要的那種規則。
# 這裡收窄成「講的就是機密機制本身」的字，中英各列一份（註解是中文寫的，
# 只比對英文識別字會漏掉整段用中文描述授權流程的段落）。
#
# 誤擋的代價＝少一則，而且會印出來；漏放的代價＝把授權內部細節寫進任何登入者
# 都查得到的語料。兩邊不對稱，所以寧可寬。
CODE_REDLINE_BODY = re.compile(
    r"License|Fingerprint|TwoFactor|Totp|Antiforgery|DataProtection"
    r"|RecoveryCode|PasswordHash|clientCode"
    r"|授權檔|授權驗證|授權失效|機器指紋|環境指紋|雙因素|備援碼|私鑰|公鑰|金鑰|密碼雜湊",
    re.IGNORECASE,
)
CS_REDLINED = "（命中內容紅線）"

# 測試與 migration 不是規則的來源：測試講的是「怎麼驗」，migration 講的是 schema 變更史。
# 兩者都會通過「有中文註解」這個條件，所以要明確擋掉，不能只靠清單沒列。
CODE_PATH_DENY = (
    "SmartFactory.MES.Tests/", "SmartFactory.MES.IntegrationTests/",
    "SmartFactory.MES.UITests/", "/Migrations/", "/obj/", "/bin/",
)

# 章節位置要給使用者看（引文展開時 `HeadingPath` 就印在標題上，§10.11.12），
# 所以**不能用類別或方法名稱**——那正是提示詞第 3 條禁止出現在擷取物裡的東西。
# 模組代號換成使用者說得出口的名字，細節由該則註解的第一句話承擔。
MODULE_LABELS = {
    "BAS": "基本資料", "INV": "庫存管理", "FIN": "財務會計", "PUR": "採購",
    "ORD": "訂單出貨", "MFG": "生產製造", "QCR": "品質管理", "ACT": "帳務",
    "RPS": "報表", "SUB": "委外", "CST": "成本", "QA": "系統助理",
}

# 一則程式碼註解要進語料的最低中文量。低於此的多半是「取得或設定…」這種樣板，
# 或半句話的提醒——兩者都構不成一條使用者需要知道的規則。
CODE_MIN_CJK = 30

# 擷取物說明的字數下限。見 `parse` 的說明——程式碼註解一句話就是完整規則的比例，
# 比規格高得多，沿用 .md 的 40 字會把成立的規則當成格式失敗丟掉。
# 20 這個數字沒有理論根據，它只是「一句完整的中文規則」的下緣（實測最短的成立例子
# 是 23 字的『系統會阻止任何嘗試讓可用庫存量變成負數的動作』）。**真正的防線是人審**，
# 這個門檻只負責擋掉模型在解釋自己的那種半句話。
CODE_MIN_TEXT = 20

# 切得出來、但不該送模型的單位，用這個前綴標記。**不是安靜丟掉**——
# main() 會把它們濾出來列進審核頁的「未送模型」欄（§10.11.11 ②）。
CS_UNUSABLE = "（章節位置"

CJK = re.compile(r"[\u4e00-\u9fff]")

# ── 產生器提示詞 ────────────────────────────────────────────────────────
#
# 六條規則各自對應一種已知的失敗，不是通則性的叮嚀：
#   1 → §10.11.5「擷取時不寫操作步驟，只寫規則」（兩種語料不重疊）
#   2 → 加料。這是改寫任務的**主要**失敗模式，而 §10.8.2 的六項指標一項都量不到它
#   3 → §10.11.1 理由二（語彙不對，照原文引用等於沒回答）
#   4 → §10.11.8「未來式」與「已被推翻的舊方案」——兩者都不是現況
#   5 → §10.11.5 紅線的段落級落實（檔案過得了閘門，段落仍可能過不了）
#   6 → 棄權。不是每一段都有東西可擷取，沒有棄權選項就會逼出加料

PROMPT = """你要把一份 ERP 系統開發規格的其中一段，改寫成給「使用者」看的規則說明。

必須遵守：
1. 只寫「規則是什麼、為什麼」。不寫操作步驟——哪個畫面、按哪個按鈕、欄位填什麼，一律不寫。
2. 只能用原文已經寫到的事實。原文沒寫的一律不准補，包含你認為理所當然的部分。
   數字、比率、天數、順序，全部照原文，不准推算。
3. 不得出現程式碼、類別名稱、方法名稱、欄位名稱、資料表名稱、API 路徑、設定鍵。
   要換成使用者的說法。例如原文寫「以 PlantId 套用 Global Query Filter」，
   你要寫「每個人只看得到自己廠別的單據」。
4. 只寫「現在就是這樣」的規則。原文若在講以後才要做的、曾經考慮但沒有採用的、
   還沒決定的，一律當作這一段沒有內容。
   ⚠️ 原文最常見的寫法是「**原本**是 A（會出什麼事），**現在改成** B」——
   這種段落講的是**兩個**行為：A 是已經修掉的舊缺陷，B 才是現在的規則。
   看到「原本／本來／先前／這裡原是／已修／改成／不再／從此」這類字眼，
   要寫的是 B。A 那一段描述通常更生動、更具體（有症狀、有情境），
   但它**不是現況**，寫成現況就是把已經修好的缺陷當成系統行為告訴使用者。
   若整段只在講 A 而沒有交代 B，當作這一段沒有內容。
5. 原文若在講怎麼繞過檢核、哪裡有漏洞還沒修、密碼或金鑰怎麼取得，
   一律當作這一段沒有內容。
6. 這一段若沒有任何使用者需要知道的規則（例如整段都是程式範例或開發流程），
   照下面的格式回覆沒有內容。

輸出格式，只有兩行，不要有其他任何文字：
標題：（十二個字以內，說明這條規則在講什麼）
說明：（八十到兩百字，完整的句子，用繁體中文）

這一段沒有內容時，只輸出一行：
無

原文的章節位置：{heading}

原文：
{body}
"""

# 程式碼註解的提示詞：在 PROMPT 六條之外**多一條**，而那一條是這個來源存在的最大風險。
#
# §10.11.13 的抽樣量到：相似度最低那一帶 8 則抽樣 8 則全是實作取捨
# （SVG 少一個屬性、零向量的餘弦是 NaN、資料型別怎麼選）。這些**在程式碼裡佔多數**，
# 而模型幾乎不棄權（§10.11.11 實跑 19 段只棄權 1 段）——沒有第 7 條，
# 產出的會是一整批把重構理由改寫成「系統會…」的空話，而且讀起來完全通順。
CODE_PROMPT = PROMPT.replace(
    "這一段沒有內容時，只輸出一行：",
    """7. 原文是程式碼裡的註解。註解大多在講**實作取捨**——資料型別怎麼選、
   為什麼改成這樣寫、效能、測試怎麼驗、哪一段程式負責什麼。
   這一類使用者在系統裡看不到，一律當作這一段沒有內容。
   只有當它講的是「使用者會遇到的行為、限制、預設值、擋下來的原因」時才擷取。

這一段沒有內容時，只輸出一行：""",
).replace(
    "你要把一份 ERP 系統開發規格的其中一段",
    "你要把一段 ERP 系統程式碼裡的中文註解",
).replace("原文的章節位置：", "這段註解出現在系統的哪個部分："
).replace("（八十到兩百字", "（四十到兩百字")

# ── 自動旗標 ────────────────────────────────────────────────────────────
#
# 審核頁不只是把兩邊擺在一起。人工逐段核可要看的是 17,331 行規格的產物，
# 疲勞是必然的，所以把**機器查得掉的兩種失敗**先標出來，讓人力花在剩下的部分。
# 兩項都刻意做成「疑似」而不是「錯誤」：它們會誤報，而誤報的旗標若被當成判定，
# 審核者很快就會學會忽略整欄。

DEV_VOCAB = re.compile(
    r"\b(PlantId|MenuKey|DbContext|IPlantEntity|xmin|migration|API|SQL|JSON|"
    r"Controller|Service|Repository|Entity|Interface|async|await|nullable)\b"
    r"|[A-Za-z_][A-Za-z0-9_]*\(\)"
    r"|_[a-z][A-Za-z0-9]*\b",
    re.IGNORECASE,
)
NUMBER = re.compile(r"\d+(?:\.\d+)?")

# 圍籬內的「像程式」訊號。**用密度，不用圍籬佔比**——這不是微調，是實測換來的：
# `currency.md` §三 換算公式整段都在圍籬裡（佔比 0.74），但內容是
# 「本幣金額 ＝ 外幣金額 × Rate」，全庫最該擷取的一條規則，訊號密度 0.000。
# 同一份裡 §五、六、七的 C#／cshtml 範例密度是 0.028–0.093，中間隔了一個數量級。
# 用佔比當閘門會把那條規則跟程式範例一起丟掉，而且丟得毫無聲音。
CODE_SIGNAL = re.compile(r"[;{}()\[\]]|=>|\bvar\b|\bpublic\b|\bawait\b|@[A-Za-z]|</")


@dataclass
class Section:
    heading: str
    body: str
    index: int
    # slug 的雜湊輸入。`.md` 用標題路徑（＝heading，維持既有 slug 不變）；
    # `.cs` 用「檔案 + 宣告」——程式碼的顯示用標題取自註解的第一句話，
    # 那句話一改 slug 就跟著變，而**改一句註解不該讓已核可的那則失效**。
    key: str = ""
    # staleness 專用，**永遠不送模型**。`.cs` 的 body 只有註解＋宣告行，於是
    # 「改了程式碼、沒動註解」在逐段比對裡完全看不見：對帳會判未變、沿用已核可的說明、
    # 再把整份 sourceHash 更新成現檔——等於用一次對帳替一段可能已經不成立的行為重新蓋章。
    # 整支檔案的雜湊本來擋得住這件事，逐段對帳把它放掉了，所以要自己補回來。
    # fingerprint 收「這段註解管到的那段程式碼」；`.md` 留空（那裡 body 就是原文全部）。
    fingerprint: str = ""


@dataclass
class Extract:
    slug: str
    lang: str
    heading_path: str
    title: str
    text: str
    section: Section
    seconds: float
    flags: list[str] = field(default_factory=list)


def repo_root(start: Path) -> Path:
    for d in [start, *start.parents]:
        if (d / ".git").exists():
            return d
    sys.exit("找不到 repo 根目錄（往上找不到 .git）。")


def sha256_of_source_bytes(raw: bytes) -> str:
    """**必須與 ExtractManifestImporter.Sha256 逐位元組相同**，否則匯入時全部判 stale
    ——而 stale 的段落一律不給檢索，症狀是「產了一整批但一句都答不出來」。

    對面是 `File.ReadAllTextAsync` 之後對 UTF-8 位元組取雜湊：
    .NET 會吃掉 BOM，不會動行尾。所以這裡讀 bytes、去掉 BOM，其餘原樣。

    🚨 **判過期的每一個呼叫端都必須走這一支，不可以自己寫 `sha256(read_bytes())`。**
    2026-08-25 實測：`qa-extract-worklist.py` 與 `qa-extract-reconcile.py` 各自寫了
    一份不去 BOM 的版本，於是**來源檔一有 BOM，那份 manifest 從產出的那一刻就永遠過期**
    ——審核清單把它當「已過期」靜默跳過（`--approve` 也拒收），對帳則因為在 git 歷史裡
    找不到相符的雜湊而把每一則都判成「已變」。兩邊的症狀都是安靜地少收，
    而少收在畫面上長得跟「這支沒東西」一模一樣。第二批的 ORD／PUR 有 3 支帶 BOM，
    第一批一支都沒有——所以這個洞在第一批完全看不出來。
    """
    if raw.startswith(b"\xef\xbb\xbf"):
        raw = raw[3:]
    return hashlib.sha256(raw).hexdigest()


def sha256_of_source(path: Path) -> str:
    """見 `sha256_of_source_bytes`。"""
    return sha256_of_source_bytes(path.read_bytes())


def split_sections(text: str, path: Path | str | None = None) -> list[Section]:
    """切段。**依副檔名分派**：`.md` 走標題，`.cs` 走註解區塊（§10.11.13）。

    `path` 可省略，省略時一律當 markdown——舊呼叫端（reconcile、worklist）
    在改到之前仍然可用，而它們處理的本來就是 `.md`。

    ---
    以下是 markdown 那條路：依 markdown 標題切段，維護標題路徑。

    圍籬內的 `#` 不是標題——規格裡滿是 `# 註解` 的 shell 與 C# 範例，
    不追蹤圍籬會把一段程式碼中間切開，送給模型的就是半截語法。

    **這裡不做任何長度過濾。** 第一版把門檻設在 200 字，結果 `currency.md` 的
    §三 換算公式（96 字）被丟掉——而那一段就是「本幣金額 ＝ 外幣金額 × Rate」，
    全庫最該擷取的一條規則。短不等於不重要，規格裡最短的段落往往是最濃的那條。
    過濾留在呼叫端做，而且被過濾掉的要印出來、要上審核頁。
    """
    if path is not None and str(path).endswith(".cs"):
        return split_cs_sections(text, str(path))

    sections: list[Section] = []
    trail: dict[int, str] = {}
    cur_head, cur_body, fenced = None, [], False

    def flush():
        if cur_head is None:
            return
        body = "\n".join(cur_body).strip()
        if body:
            sections.append(Section(cur_head, body, len(sections) + 1))

    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
        m = None if fenced else re.match(r"^(#{1,6})\s+(.*\S)\s*$", line)
        if m:
            flush()
            level, title = len(m.group(1)), m.group(2)
            trail[level] = title
            for deeper in [k for k in trail if k > level]:
                del trail[deeper]
            cur_head = " › ".join(trail[k] for k in sorted(trail))
            cur_body = []
        elif cur_head is not None:
            cur_body.append(line)
    flush()
    return sections


# 不照 `Modules/<代號>/` 長的來源。**這不是特例清單，是分桶方法的補丁**：
# 依目錄分桶，桶名就會漏掉不照目錄長的東西。已經踩過兩次——
# 第一次是「簽核 0 則」（簽核在 Core 主體不在 Modules/），
# 第二次是手機（`Api/Mobile/`）被歸進「Core 其他」，模組表上完全看不出有手機，
# 直到使用者問「有沒有規劃手機」才查出來。
# 少了這一條，手機的規則會被標成「系統共用」——詞彙通道拿 HeadingPath 當索引的一部分，
# 於是問「手機報工」永遠不會在標題上命中。
PATH_MODULES = (
    ("Api/Mobile/", "手機", "mobile"),
)


def module_label(rel: str) -> str:
    """`Modules/<代號>/…` → 使用者說得出口的模組名。不在 Modules 底下的走 Core 主體。"""
    for prefix, label, _ in PATH_MODULES:
        if rel.startswith(prefix):
            return label
    m = re.match(r"Modules/([A-Za-z]+)/", rel)
    return MODULE_LABELS.get(m.group(1).upper(), "系統共用") if m else "系統共用"


def module_label_code(rel: str) -> str:
    """manifest 檔名用的模組代號（小寫）。與 `module_label` 分開：一個給人看、一個當索引鍵。"""
    for prefix, _, code in PATH_MODULES:
        if rel.startswith(prefix):
            return code
    m = re.match(r"Modules/([A-Za-z]+)/", rel)
    return (m.group(1).lower() if m else "core")


def cs_heading_text(comment: str) -> str:
    """把註解變成**使用者看得懂**的章節位置。

    這一步不是排版，是紅線的延伸：`HeadingPath` 會直接印在引文展開的標題上
    （§10.11.12），而提示詞第 3 條禁止擷取物出現類別、方法、欄位名稱——
    章節位置若寫成 `UomService.ConvertAsync`，等於從側門把同一件事漏出去。

    **優先找「本來就沒有識別字」的那一句**，而不是把識別字從第一句挖掉。
    挖掉的結果實測是「刻意不濾 ：小數位數…」「是給既有非法資料的防呆：[] 上線前…」
    ——句子破了，而破掉的句子會原封不動印在使用者的引文標題上。
    整段都找不到乾淨句子時才退回「挖掉再清理」，清不出 4 個中文字就放棄這一則。
    """
    def clean(s: str) -> str:
        s = re.sub(r"<[^>]*>", "", s)                 # <summary> 這類 XML doc 標籤
        s = re.sub(r"`[^`]*`", "", s)                 # 反引號裡的程式碼
        s = re.sub(r"[（(][^）)]*[）)]", "", s)         # 括號補充
        s = re.sub(r"^\s*[0-9①-⑩]+\s*[.、)]\s*", "", s)  # 開頭的條列編號
        s = re.sub(r"^[⚠🚨※\-–—*·]+\s*", "", s)
        return s.strip()

    ident = re.compile(r"[A-Za-z_][A-Za-z0-9_.<>]{2,}")
    sentences = [clean(x) for x in re.split(r"[。；;\n]", comment) if x.strip()]

    pick = next((s for s in sentences
                 if not ident.search(s) and len(CJK.findall(s)) >= 6), "")
    if not pick:
        for s in sentences:
            s = ident.sub("", s)
            s = re.sub(r"[\[\]{}()<>]", "", s)
            s = re.sub(r"\s+", " ", s).strip(" ，、。—-·:：/|")
            # 識別字被挖掉之後在兩個中文字中間留下空隙＝句子破了。
            # 「是給既有非法資料的防呆： 上線前存進去的負數會讓 直接丟」就是這樣來的，
            # 而它會原封不動印在使用者的引文標題上。破掉就換下一句，都破就放棄這一則。
            if re.search(r"[\u4e00-\u9fff]\s+[\u4e00-\u9fff]", s):
                continue
            if len(CJK.findall(s)) >= 4:
                pick = s
                break
    if not pick:
        return ""

    # 截斷要斷在標點上。硬切會留下半個引號（實測「…不是」），而那會直接印給使用者看。
    if len(pick) > 32:
        cut = max((pick.rfind(c, 0, 32) for c in "，、：:"), default=-1)
        pick = pick[: cut if cut >= 12 else 32].rstrip("，、：:")
    for a, b in (("「", "」"), ("『", "』"), ("（", "）")):
        if pick.count(a) != pick.count(b):
            pick = pick.replace(a, "").replace(b, "")
    return pick.strip()


def split_cs_sections(text: str, rel: str) -> list[Section]:
    """`.cs` 的切段單位＝**一個註解區塊 ＋ 它註解的那一行宣告**。

    **為什麼不能沿用 markdown 那條路**：`split_sections` 依 `#` 標題切段，
    `.cs` 沒有標題，於是回 0 段——而它**不會報錯**，只會產出一份 entries 為空的
    manifest，然後被匯入器以「entries 為空」整批中止。這是 §10.11.13 記的硬前置①。

    兩種單位，對應兩種註解習慣：
      · `///` XML doc —— 寫在宣告上方，講「這個東西是什麼」
      · `//`  一般註解 —— 寫在邏輯旁邊，講「為什麼這樣做」，本專案的規則多半在這裡

    宣告行一起帶進 body 是刻意的：註解常常用「它」「這裡」指涉下一行，
    只送註解會讓模型不知道在講什麼，然後開始猜——而猜出來的東西讀起來完全正常。

    中文量不足 CODE_MIN_CJK 的一律不收（英文樣板註解、半句話的提醒），
    章節位置產不出來的也不收，兩者都由呼叫端印出來。
    """
    lines = text.splitlines()
    sections: list[Section] = []
    seen_heading: set[str] = set()
    label = module_label(rel)
    i, n = 0, len(lines)

    while i < n:
        stripped = lines[i].strip()
        if not stripped.startswith("//"):
            i += 1
            continue

        doc = stripped.startswith("///")
        block, start = [], i
        while i < n:
            cur = lines[i].strip()
            if not cur.startswith("//") or cur.startswith("///") != doc:
                break
            block.append(re.sub(r"^///?/?\s?", "", cur))
            i += 1

        # 註解後面第一行實質程式碼。屬性（[Fact]、[HttpPost]）與空行跳過——
        # 它們夾在註解與宣告之間是常態，把屬性當成宣告會讓 body 少掉真正的那一行。
        decl, j = "", i
        while j < n and j < i + 4:
            cand = lines[j].strip()
            if cand and not cand.startswith(("[", "#")):
                decl = cand
                break
            j += 1

        comment = "\n".join(x for x in block if x.strip())
        if len(CJK.findall(comment)) < CODE_MIN_CJK:
            continue

        heading_text = cs_heading_text(comment)
        if not heading_text:
            sections.append(Section(f"{label} › （章節位置產不出來）", comment,
                                    start + 1, key=f"{rel}::{start}"))
            continue

        heading = f"{label} › {heading_text}"
        if heading in seen_heading:
            # 同一份裡標題重複＝審核頁與 worklist 的「對回原文」會指到其中一則。
            # 兩則共用一個章節位置本來就分不出來，寧可少收一則。
            sections.append(Section(f"{label} › （章節位置重複）", comment,
                                    start + 1, key=f"{rel}::{start}"))
            continue
        seen_heading.add(heading)

        body = comment + (f"\n\n宣告：{decl}" if decl else "")
        # key ＝ 檔案 ＋ 宣告：註解字句改了 slug 不變，宣告改了才變。
        sections.append(Section(heading, body, start + 1, key=f"{rel}::{decl or start}"))

    # 每一段「管到」的範圍＝自己的註解開頭，到下一段的註解開頭之前。
    # 中間被跳過的註解（中文太少）連同它的程式碼一併算進前一段：那些行沒有人在看，
    # 歸給上一段至少會被比到，總比落在任何一段之外好。
    for k, s in enumerate(sections):
        end = sections[k + 1].index - 1 if k + 1 < len(sections) else n
        s.fingerprint = "\n".join(lines[s.index - 1:end])

    return sections


def stem_for(rel: str) -> str:
    """manifest 檔名的 stem。程式碼的要帶模組，見呼叫端註解。"""
    src = Path(rel)
    return f"code-{module_label_code(rel)}-{src.stem}".lower() if rel.endswith(".cs") else src.stem


def slug_stem_for(rel: str) -> str:
    """slug 前綴。**對帳腳本靠這支反推 slug**，所以規則只能有一份實作。"""
    return re.sub(r"[^a-z0-9]+", "-", stem_for(rel).lower()).strip("-")


def slug_of(slug_stem: str, heading_path: str, taken: set[str], quiet: bool = False) -> str:
    """slug ＝ 檔名 ＋ **標題路徑的雜湊**。刻意不是段落序號。

    序號版（`currency-04`）綁的是「第幾段」，不是內容。在來源規格前面插一個標題，
    之後每一段的 slug 全部往後位移一格，而**沒有任何一步會報錯**：
    實測 `currency.md` 插一個標題後，第 4 段的 slug 從「三、換算公式」變成
    「二、匯率類型」、第 11 段從「七、View」變成「六、Controller」。
    後果是 `DECISIONS.md` 記的退回理由指到別的內容，靠 slug 對回去的核可
    也會落在沒審過的段落上——而**唯一的症狀是答案內容不對**。

    改成雜湊之後，slug 是「這段內容在文件裡的位置」的識別，插段落不影響既有的段。
    代價是換來的：標題本身被改寫時 slug 會變（那時它本來就該重審），
    而且 slug 不再看得出前後順序——順序看 `headingPath`，manifest 裡本來就有。

    雜湊取 8 碼（32 bit）：一份最多 160 則，碰撞機率約 3e-6。低，但**碰撞的代價是
    匯入器整批中止**（`Validate` 要求同一份內 slug 唯一，一拋例外全部規則類問題一起消失），
    所以還是要接住。同一份裡標題路徑重複也走這條——把出現次序摻進雜湊輸入重算，
    並且印出來：這種情況下該則的 slug 會退回「跟位置有關」，必須看得見。
    """
    for n in range(64):
        key = heading_path if n == 0 else f"{heading_path}\x00{n}"
        s = f"{slug_stem}-{hashlib.sha256(key.encode('utf-8')).hexdigest()[:8]}"
        if s not in taken:
            taken.add(s)
            if n:
                # quiet ＝ 對帳在重放既有 slug，不是在決定新的。
                # 每次盤點都重印一次碰撞警告，只會讓人學會忽略這一欄。
                if not quiet:
                    print(f"　　⚠ 標題路徑重複或雜湊碰撞，改用第 {n} 次雜湊 → {s}　（{heading_path}）")
            return s
    sys.exit(f"slug 連續 64 次碰撞，不正常，請查：{heading_path}")


def code_density(body: str) -> float:
    """圍籬內每個字元平均帶多少「像程式」的訊號。沒有圍籬就是 0。

    為什麼需要這道確定性的閘門：提示詞第 6 條給了模型棄權的選項，
    實跑 19 段只棄權 1 段（2026-08-14）。模型幾乎不棄權，於是把
    `ICurrencyService` 的介面定義硬改寫成「系統會自動取得當前使用的本幣代號」，
    把 API 回傳結構寫成「回傳一個表示已找到但狀態為未成功的情況」。

    **這種空話不會被任何旗標抓到**——它沒有加料、沒有語彙外洩、讀起來也通順，
    但它會佔一列、會被嵌入，然後跟真正的規則搶檢索的名額。
    與其再調提示詞（§10.8.2 之十一：為對齊字面改提示詞，三輪全部持平或退步），
    不如在送出去之前就用一個量得到的性質擋掉。
    """
    fenced, inside = [], False
    for line in body.splitlines():
        if line.lstrip().startswith("```"):
            inside = not inside
            continue
        if inside:
            fenced.append(line)
    text = "\n".join(fenced)
    return len(CODE_SIGNAL.findall(text)) / len(text) if text else 0.0


def load_code_sources(root: Path) -> dict[str, str]:
    """進版控的程式碼來源清單。格式：`<repo 相對路徑>`，可用 tab 補一個章節位置覆寫。

    **檔案不存在＝一支都不准跑**，不是「沒設限」。預設拒絕要在缺檔時也成立，
    否則刪掉清單就等於解除閘門。
    """
    f = root / CODE_SOURCES_FILE
    if not f.is_file():
        return {}
    out: dict[str, str] = {}
    for line in f.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        path, _, label = line.partition("\t")
        out[path.strip()] = label.strip()
    return out


def check_source_allowed(rel: str, root: Path | None = None) -> None:
    """來源閘門。`.md` 走 REDLINE／EXCLUDED，`.cs` 走逐檔清單（§10.11.13）。"""
    if rel.endswith(".cs"):
        # ① 紅線先判，**且不看清單**：清單是人維護的，這一道守的是有人把
        #    LicenseValidator.cs 加進去的那一次手滑。
        if CODE_REDLINE.search(Path(rel).name):
            sys.exit(
                f"拒絕：{rel} 的檔名命中程式碼紅線（授權／指紋／2FA／密碼／權杖／加密／驗證授權）。\n"
                f"若它被列在 {CODE_SOURCES_FILE} 裡，請把那一行刪掉——"
                "這不是可以用參數覆寫的檢查。"
            )
        if any(d in "/" + rel for d in CODE_PATH_DENY):
            sys.exit(f"拒絕：{rel} 在測試／migration／建置產物路徑下，那裡沒有使用者規則。")
        allowed = load_code_sources(root or Path.cwd())
        if rel not in allowed:
            sys.exit(
                f"拒絕：{rel} 不在 {CODE_SOURCES_FILE} 上。\n"
                "程式碼來源是**預設拒絕**：要收一支就先把它加進那份清單並送審（§10.11.13 硬前置②）。"
            )
        return

    if rel in REDLINE or any(rel.startswith(d + "/") for d in REDLINE_DIRS):
        sys.exit(
            f"拒絕：{rel} 在 §10.11.8 的紅線清單上，不得收錄亦不得改寫後收錄。\n"
            "這不是可以用參數覆寫的檢查。"
        )
    if rel in EXCLUDED:
        sys.exit(
            f"拒絕：{rel} 判定為不收（無終端使用者價值，或描述的不是現況）。\n"
            "若確有理由改判，請先改 §10.11.8 的判定表與本腳本的清單，不要繞過。"
        )


# 端點不通時要印的東西。**桌機有兩份 Ollama，而且都綁 11434 但在不同 network namespace**
# （§17.11.7）：從 WSL 打 127.0.0.1:11434 永遠打到 WSL 那份，打不到 Win11 那份。
# 「連得上」不等於「連對了」，所以這裡不自動改端點，只把現況攤開讓人自己決定。
SWITCH_HINT = """
桌機有兩份 Ollama，都綁 11434 但在不同 network namespace（規格 §17.11.7）：
  · Win11 那份（主力，12 顆模型，含 gemma4:latest）→ 從 WSL 走 SSH 反向通道 http://127.0.0.1:11435
  · WSL 那份（備援，只有 gemma3:4b，已取消開機自啟）      → http://127.0.0.1:11434

  ~/.cache/ollama-pull/switch-llm.sh status      # 看兩個端點、通道、app、目前 profile
  ~/.cache/ollama-pull/switch-llm.sh win         # 切到 Win11（不通會自動拉起 SSH 反向通道）
  ~/.cache/ollama-pull/switch-llm.sh win --no-restart   # 只拉通道與組態，不碰 app

一次性也可以直接指定：--url http://127.0.0.1:11435
要讓它在這個 shell 一直有效：export QA_OLLAMA_URL=http://127.0.0.1:11435
或者直接沿用 switch-llm.sh 已經寫好的那份組態（app 與本腳本同一個事實來源）：
  source ~/.cache/ollama-pull/llm-profile.env"""


def preflight(url: str, model: str) -> None:
    """跑之前先確認端點通、模型在。

    **補的是一個真實的浪費（2026-08-25）**：沒有這一道時，端點不通的症狀是
    「每一段各自失敗一次」——193 段就是 193 行一模一樣的錯誤，
    而真正的原因（通道沒開）只在第一行看得出來，且**跑完才會知道一則都沒產出**。
    通道半開的情況更糟：每一段都會等滿 timeout（預設 900 秒）。

    不通時**不自動改端點**，只把另一個常見端點探一下並照實說。
    自動切過去正是 §17.11.7 那條「連得上不等於連對了」要防的事——
    兩份 Ollama 的模型庫不一樣，靜默切換會讓人以為跑的是主力那顆。
    """
    def tags(u: str) -> set[str] | None:
        try:
            with urllib.request.urlopen(f"{u}/api/tags", timeout=8) as resp:
                return {m["name"] for m in json.load(resp).get("models", [])}
        except Exception:
            return None

    names = tags(url)
    if names is None:
        other = "http://127.0.0.1:11435" if url.endswith("11434") else "http://127.0.0.1:11434"
        alt = tags(other)
        extra = (f"\n\n⚠ 但 {other} 有回應（{len(alt)} 顆模型）——你要的大概是它，"
                 "不過切之前先確認那是不是你要的那份。" if alt is not None else "")
        sys.exit(f"端點不通：{url}{extra}\n{SWITCH_HINT}")

    if model not in names:
        sys.exit(
            f"端點 {url} 上沒有模型 {model}。\n可用：{', '.join(sorted(names)) or '（一顆都沒有）'}\n"
            f"{SWITCH_HINT}"
        )
    print(f"✓ 端點 {url} 就緒，模型 {model} 存在")


def ask(url: str, model: str, prompt: str, timeout: int) -> tuple[str, float]:
    """走原生 /api/chat，`think:false` ＋ 寬鬆的 num_predict（§10.8.1 硬性契約）。

    `done_reason == "length"` 或內容為空一律當失敗——那是 §10.8.1 第 2 點的靜默失敗：
    HTTP 200、done:true、token 也真的生成了，只有內容是空的。
    """
    payload = json.dumps(
        {
            "model": model,
            "messages": [{"role": "user", "content": prompt}],
            "stream": False,
            "think": False,
            "options": {"num_predict": 1024, "temperature": 0.0},
        }
    ).encode()
    req = urllib.request.Request(
        f"{url}/api/chat", data=payload, headers={"Content-Type": "application/json"}
    )
    started = time.monotonic()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.load(resp)
    elapsed = time.monotonic() - started

    content = (data.get("message") or {}).get("content", "").strip()
    if data.get("done_reason") == "length":
        raise RuntimeError("生成被 num_predict 截斷（done_reason=length），內容不可信。")
    if not content:
        raise RuntimeError("模型回了空內容（§10.8.1 第 2 點的靜默失敗）。")
    return content, elapsed


def parse(answer: str, min_text: int = 40, fallback_title: str = "") -> tuple[str, str] | None:
    """回 None 代表模型棄權。棄權是正常結果，不是失敗。

    **有兩條路徑，而且第二條不是防禦性冗贅。** 首次實跑（2026-08-14、`gemma4:latest`、
    `currency.md` 前三段）三段全數「不符格式」——但內容是對的：模型給了標題行、
    給了說明段，只是沒印 `標題：`／`說明：` 這兩個字面。

    §10.8.2 之二與之十一已經量過同一件事兩次：為了讓模型對齊一個字面而改提示詞，
    三輪全部持平或退步。**所以這裡不再動提示詞，改讓解析器接受無標籤的形狀**，
    但要設防——寬容的解析器若無條件接受，任何一段閒聊都會變成一則擷取物：

      * 標題不得超過 24 字、不得以句號結尾（那是說明，不是標題）
      * 說明至少 `min_text` 字（低於此的多半是模型在解釋自己而不是在改寫）

    **程式碼來源把下限降到 24 字**（2026-08-25 實跑換來的）：規格的一段講一件事的來龍去脈，
    程式碼註解常常一句話就是完整規則。首批頭三支 20 則裡有 3 則（15%）卡在 40 字，
    而被丟掉的是「品號不存在就不讓存」「停用的品號不出現在選單」這種**確實成立的規則**。
    往上調提示詞逼模型寫長只會換來加料——§10.11.11 已經量過，為對齊字面改提示詞三輪全退步。
    """
    body = answer.strip()
    if re.fullmatch(r"[「『【]?無[。．.」』】]?", body):
        return None

    # 模型有時不照約定回「無」，而是寫一整句解釋為什麼沒東西可擷取。
    # 實跑 mobile-pwa.md §13（程式碼層慣例・未採用）回的是
    # 「無法判斷規則，因為原文內容皆為未採用的技術方案或開發實作細節」——
    # 判斷完全正確，那一節正是該排除的未採用方案，卻被記成失敗。
    # 於是「模型答錯」與「模型正確棄權」在統計上變成同一個數字，
    # 而失敗數正是用來決定哪些檔案值得重跑的訊號。
    #
    # pattern 只認開頭：擷取物的第一行是標題（≤24 字、不以句號結尾），
    # 不可能以這些詞起手，所以不會把正常擷取物誤判成棄權。
    if re.match(
        r"^\s*(無法判斷|無可擷取|沒有(可|能)(以)?擷取|沒有需要|不適用"
        r"|無(相關|可用)?規則|本(段|節)(無|沒有))",
        body,
    ):
        return None

    # 棄權也可能寫在**最後一行**：模型先解釋為什麼沒東西可擷取，最後補一句「輸出：無」。
    # 上面那道 pattern 只認開頭，於是整段解釋被當成說明、章節位置被當成標題，
    # 產出一則「曾因此而失效｜規則判斷：這段註解描述的是…屬於開發實作取捨。輸出：無」
    # ——2026-08-26 第五批 `PlantFormEnhancementFilter.cs` 實跑撞到，而**模型的判斷是對的**，
    # 那一段確實是實作取捨（第 7 條該擋的東西）。
    #
    # **棄權被當成內容，比擷取失敗嚴重得多**：失敗會計數、會列在審核頁上被看到；
    # 這個會一路通過標題長度、字數下限、`scrub()` 每一道檢查，長得跟正常擷取物一樣進語料。
    # 只認明確的收尾標記（「輸出：無」或單獨一行的「無」），不猜語意——
    # 猜語意會開始丟掉正常的擷取物，那是往反方向的錯。
    if re.search(
        r"(?:^|\n)\s*(?:輸出|結果|答案|判定)\s*[：:]\s*[「『【]?無[。．.」』】]?\s*$", body
    ) or (
        "\n" in body and re.search(r"\n\s*[「『【]?無[。．.」』】]?\s*$", body)
    ):
        return None

    title = text = ""
    for line in body.splitlines():
        m = re.match(r"^\s*標題\s*[：:]\s*(.*)$", line)
        if m:
            title = m.group(1).strip()
            continue
        m = re.match(r"^\s*說明\s*[：:]\s*(.*)$", line)
        if m:
            text = m.group(1).strip()
            continue
        if text and line.strip():
            text += line.strip()

    if not title or not text:
        lines = [ln.strip() for ln in body.splitlines() if ln.strip()]
        if len(lines) >= 2:
            title = re.sub(r"^\s*[#*\s]*", "", lines[0]).strip(" *#：:")
            text = "".join(lines[1:])

    # 第三種形狀：整段擠成一行、用「：」分隔（實跑 APPROVAL-WORKFLOW.md 撞到）。
    # 那一則的內容完全正確（自動指定直屬主管、多位時取使用者名稱字典序最小），
    # 純粹因為模型沒照約定換行就被丟掉——換來的是一個失敗計數，
    # 事後還看不出是格式問題還是模型答錯，兩者在統計上長得一樣。
    #
    # 條件是 title 與 text **都**沒解析出來才啟用：若已經走過「說明：」分支，
    # 這裡再切一次會把「說明」兩個字當成標題，產出垃圾。
    # 只切第一個冒號，且下面「≤24 字、不以句號結尾」兩道檢查照常守著——
    # 切錯位置會被擋下，不會放行。
    if not title and not text:
        m = re.match(r"^\s*([^：:\n]{2,24})[：:]\s*(.+)$", body.replace("\n", ""), re.S)
        # ⚠️ 開場白也帶冒號：「好的，以下是改寫：…」會被這個形狀切成
        # 標題「好的，以下是改寫」＋內文，然後一路通過所有檢查變成一則擷取物。
        # 這一行是 2026-08-25 順手補的——程式碼來源把說明字數下限降到 20 之後更容易撞到。
        if m and not re.match(r"^(好的|以下|根據|這一?段|原文|回答|當然)", m.group(1)):
            title, text = m.group(1).strip(" *#"), m.group(2).strip()

    # 第四種形狀：**整則就是一句話，沒有標題**（2026-08-25 程式碼來源實跑撞到三次）。
    # 「無法修改已完成支付或作廢的發票，因為這樣做會導致記錄的總額與已經結算的金額產生差異。」
    # ——內容完全正確，只是模型沒給標題行，也沒有冒號可以切。
    #
    # 標題改用「這一段出現在哪裡」補上，而不是叫模型再想一個：標題是**標籤不是內容**，
    # 從章節位置取一段來當標籤不會憑空多出任何事實，而讓模型多生一行反而是加料的機會。
    # `fallback_title` 由呼叫端給（程式碼來源給的是章節位置的最後一節），沒給就維持原本的失敗。
    if not title and fallback_title and text_looks_like_rule(re.sub(r"\s+", "", body)):
        title, text = fallback_title[:24], re.sub(r"\s+", "", body)

    if not title or not text:
        raise RuntimeError(f"輸出不符格式，前 120 字：{body[:120]!r}")

    # 先清殘留再做長度檢查：靠標籤字面撐到下限的說明不該放行。
    title, text = scrub(title, text)
    if not title or not text:
        raise RuntimeError(f"清掉殘留後就空了，前 120 字：{body[:120]!r}")

    # 第一行太長／以句號結尾＝那不是標題，是規則的第一句。與上面同一個處理：
    # 標籤從章節位置補，內容一個字都不丟。實測「輸入單位的小數點位數決定了數字輸入框的
    # 增減步距與允許精度」28 字就是這樣被整則丟掉的。
    if len(title) > 24 or title.endswith(("。", "．", ".")):
        joined = re.sub(r"\s+", "", title + text)
        if fallback_title and text_looks_like_rule(joined):
            title, text = fallback_title[:24], joined
        else:
            raise RuntimeError(f"第一行不像標題（{len(title)} 字）：{title[:40]!r}")
    if len(text) < min_text:
        raise RuntimeError(f"說明只有 {len(text)} 字（下限 {min_text}），太短：{text[:60]!r}")
    if META_OUTPUT.search(text):
        raise RuntimeError(f"輸出是對讀者說話的後設回覆，不是規則：{text[:60]!r}")
    return title, text


# 模型的**後設回覆**：不是規則，是它在跟你說話。
#
# 這一條是 2026-09-29 逐則分級時撞出來的：`code-inv-inventorypostingservice-e2d0889b`
# 的內容是「無法在沒有程式碼和註解的情況下進行轉換。請提供需要處理的原文。」——
# 它有標題行、有說明行、字數也夠，於是通過了 parse() 的每一道檢查，被寫進語料、
# 匯入索引，一路撐到人工逐則讀才被發現。
#
# `text_looks_like_rule` 已經擋過「好的／以下／根據」開頭的閒聊，但那只在
# **模型沒照格式**的 fallback 路徑上跑。照著格式回一句後設訊息就完全沒有人擋。
# 這裡改成在正常路徑上也檢查——判準刻意抓「對讀者說話」的句型而不是關鍵字，
# 因為規則說明本身不會要求讀者提供任何東西，也不會自稱做不到。
#
# 2026-10-01 收窄：單一關鍵字會誤擋正常規則——「則**無法完成**分錄」「組合在一起
# **作為一個**單一項目」被丟掉（使用者裁決放寬）；「為什麼這個月**沒有收到**報表」
# 「由排程或大型**語言模型**呼叫」這兩則已核可的規則也會命中。改成都要帶
# 「對著原文／對著讀者」的上下文才算。案例見 qa-extract-selftest.py〈後設回覆閘門〉。
META_OUTPUT = re.compile(
    r"請提供|請給我|請告訴我|需要(?:你|您)提供"
    r"|無法在沒有|無法(?:處理|轉換|改寫|回答)(?:此類|這類|此段|這段|您|你)"
    r"|原文(?:中)?(?:並)?(?:沒有|未)(?:明確)?(?:描述|提到|說明|包含)"
    r"|我(?:不能|無法|需要)|抱歉"
    r"|作為一(?:個|名)(?:AI|人工智慧|語言模型|助理|模型)|我是(?:一個)?(?:AI|語言模型)"
    r"|沒有收到(?:任何)?(?:原文|內容|輸入|程式碼|註解)"
)


def scrub(title: str, text: str) -> tuple[str, str]:
    """把三種**確定性殘留**清掉。純字面，不呼叫模型、不改變任何事實。

    2026-08-25 使用者逐則審第二批時盤出來的。三種都不是模型答錯，是輸出的邊角
    沒被收乾淨，而它們一路留在語料裡：擷取物會直接印給使用者看（§10.11.12 引文），
    一則結尾掛著孤零零的「無」讀起來像系統壞了。

      ① **尾端棄權字元**：模型先給了完整答案，最後又補一個「無」
         （多半是在回應提示詞第 6 條的棄權選項）。只在句末終止符之後才砍，
         避免砍到「……權限為無」這種正常結尾。
      ② **殘留的標籤字面**：`說明：`／`規則說明：` 這些是提示詞的欄位名，
         不是內容。出現在說明開頭、或句末終止符之後。
         ⚠️ **只清說明、不清標題**：`版本標籤說明：系統目前…` 的「說明」是標題的一部分。
      ③ **標題抄了整條章節位置**：`系統共用 › 既有測試/呼叫端不需調整`。
         `parse()` 的 `fallback_title` 給的本來就只有最後一節，
         會出現「›」是模型自己把提示詞裡的 `{heading}` 整條抄回來。取最後一節即可。

    **不清的**：標題等於章節位置的最後一節（全語料 5 則）。那是 `parse()` 第四種形狀
    刻意的行為——模型沒給標題時用章節位置補一個標籤，見該函式的註解。
    把它當殘留清掉會讓那幾則變成沒有標題，是**把設計當成缺陷修**。
    """
    if " › " in title:
        title = title.split(" › ")[-1].strip()
    # 清到不再變為止：模型偶爾疊兩層標籤（`說明：規則說明：…`），只清一輪會剩下一層。
    # 實際踩到：code-fin-apinvoiceservice-1ad590cc。自檢以「再 scrub 一次不變」判殘留，
    # 所以這裡必須冪等。
    while True:
        before = text
        text = re.sub(r"^(規則說明|說明|規則|注意)\s*[：:]\s*", "", text)
        text = re.sub(r"(?<=[。．！？])(規則說明|說明|規則|注意)\s*[：:]\s*", "", text)
        text = re.sub(r"(?<=[。．！？）」])\s*(無|N/A)\s*$", "", text)
        if text == before:
            break
    return title.strip(), text.strip()


def text_looks_like_rule(one: str) -> bool:
    """把「沒照格式但內容是對的」收下來的最低條件。參數是**已經壓成一行**的字串。

    刻意收得很緊——這條路只在模型沒照格式時才走，寬一格就會把模型的閒聊
    （「好的，以下是改寫：」）收成一則擷取物。
    """
    return (
        20 <= len(one) <= 260
        and len(CJK.findall(one)) >= 15
        and not re.match(r"^(好的|以下|根據|這一?段|原文)", one)
    )


def flag(text: str, source_body: str) -> list[str]:
    """兩項機器查得掉的失敗。**都會誤報，所以叫「疑似」。**"""
    flags = []

    leaked = sorted(set(DEV_VOCAB.findall(text)))
    if leaked:
        flags.append("疑似語彙未轉換：" + "、".join(x for x in leaked if x)[:80])

    src_numbers = set(NUMBER.findall(source_body))
    invented = sorted(set(NUMBER.findall(text)) - src_numbers)
    if invented:
        flags.append("疑似加料（原文沒有的數字）：" + "、".join(invented)[:80])

    return flags


def render_review(
    rel: str, model: str, source_hash: str, extracts: list[Extract],
    errors: list[tuple[Section, str]],
    groups: list[tuple[str, str, list[Section]]],
) -> str:
    """審核頁。**並排是這一頁存在的全部理由**——只看擷取物判斷不了有沒有加料。"""
    def esc(s: str) -> str:
        return html.escape(s, quote=False)

    rows = []
    for e in extracts:
        flags_html = "".join(
            f'<li class="flag">{esc(f)}</li>' for f in e.flags
        )
        rows.append(f"""
<article class="pair" id="{esc(e.slug)}">
  <header>
    <code class="slug">{esc(e.slug)}</code>
    <span class="head">{esc(e.heading_path)}</span>
    <span class="sec">{e.seconds:.1f}s</span>
  </header>
  <div class="cols">
    <div class="out">
      <h3>{esc(e.title)}</h3>
      <p>{esc(e.text)}</p>
      {f'<ul class="flags">{flags_html}</ul>' if flags_html else ''}
    </div>
    <div class="src"><pre>{esc(e.section.body)}</pre></div>
  </div>
</article>""")

    # 沒進去的段落一律列出來。**這幾欄不是附錄**——擷取物的失敗方向是安靜地少答，
    # 而「少答」在這一頁上的樣子就是某一段沒有出現在上面。看不到就查不到。
    groups_html = "".join(
        f'<h2>{esc(title)}（{len(items)}）</h2><p class="meta">{esc(note)}</p>'
        f'<ul class="list">'
        + "".join(
            f"<li><span>{esc(s.heading)}</span> <em>{len(s.body)} 字</em></li>" for s in items
        )
        + "</ul>"
        for title, note, items in groups
        if items
    )
    errors_html = "".join(
        f"<li><span>{esc(s.heading)}</span> <em>{esc(msg)}</em></li>" for s, msg in errors
    )
    skipped_total = sum(len(items) for _, _, items in groups)

    return f"""<!doctype html>
<meta charset="utf-8">
<title>擷取物審核 — {esc(rel)}</title>
<style>
  :root {{
    --ink: #1a1a1a; --dim: #6b6b6b; --line: #dcd8d0; --bg: #faf9f7;
    --card: #ffffff; --out: #f4f8f4; --warn: #8a4b00; --warnbg: #fff6e8;
  }}
  @media (prefers-color-scheme: dark) {{
    :root {{
      --ink: #e8e6e3; --dim: #9a9691; --line: #37342f; --bg: #16150f;
      --card: #1e1d18; --out: #1b241b; --warn: #e0a355; --warnbg: #2b2113;
    }}
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; padding: 2rem 1.5rem 5rem; background: var(--bg); color: var(--ink);
    font: 15px/1.75 "Noto Sans TC", "Microsoft JhengHei", system-ui, sans-serif; }}
  .wrap {{ max-width: 1180px; margin: 0 auto; }}
  h1 {{ font-size: 1.4rem; margin: 0 0 .3rem; }}
  .meta {{ color: var(--dim); font-size: .85rem; margin-bottom: 2rem; }}
  .meta code {{ font-size: .8rem; }}
  .note {{ background: var(--warnbg); border-left: 3px solid var(--warn); color: var(--ink);
    padding: .8rem 1rem; margin-bottom: 2rem; font-size: .9rem; }}
  .pair {{ background: var(--card); border: 1px solid var(--line); border-radius: 6px;
    margin-bottom: 1.2rem; overflow: hidden; }}
  .pair header {{ display: flex; gap: .8rem; align-items: baseline; flex-wrap: wrap;
    padding: .6rem .9rem; border-bottom: 1px solid var(--line); }}
  .slug {{ font-weight: 700; }}
  .head {{ color: var(--dim); font-size: .85rem; flex: 1; }}
  .sec {{ color: var(--dim); font-size: .78rem; font-variant-numeric: tabular-nums; }}
  .cols {{ display: grid; grid-template-columns: 1fr 1fr; }}
  @media (max-width: 860px) {{ .cols {{ grid-template-columns: 1fr; }} }}
  .out {{ padding: 1rem; background: var(--out); }}
  .out h3 {{ margin: 0 0 .5rem; font-size: 1rem; }}
  .out p {{ margin: 0; }}
  .src {{ padding: 1rem; border-left: 1px solid var(--line); overflow-x: auto; }}
  .src pre {{ margin: 0; white-space: pre-wrap; word-break: break-word;
    font-size: .8rem; line-height: 1.6; color: var(--dim); }}
  .flags {{ margin: .8rem 0 0; padding: 0; list-style: none; }}
  .flag {{ background: var(--warnbg); color: var(--warn); border-radius: 4px;
    padding: .35rem .6rem; font-size: .82rem; margin-top: .35rem; }}
  h2 {{ font-size: 1rem; margin: 2.5rem 0 .6rem; }}
  .list {{ margin: 0; padding: 0; list-style: none; font-size: .85rem; color: var(--dim); }}
  .list li {{ display: flex; gap: .8rem; padding: .3rem 0; border-bottom: 1px solid var(--line); }}
  .list span {{ flex: 1; }}
</style>
<div class="wrap">
  <h1>擷取物審核 — {esc(rel)}</h1>
  <p class="meta">
    模型 <code>{esc(model)}</code>　來源雜湊 <code>{esc(source_hash[:12])}</code>
    產生於 {datetime.now().strftime('%Y-%m-%d %H:%M')}
    擷取 {len(extracts)} 則／未進索引 {skipped_total} 段／失敗 {len(errors)} 段
  </p>
  <div class="note">
    <b>右邊是原文，左邊是改寫。要看的是「左邊有沒有講右邊沒寫的事」。</b>
    這一頁不會存下任何決定——看完把要放行的代號告訴我，我改 manifest 的
    <code>approved</code> 再送 PR。核可是 merge，不是這一頁上的動作。
  </div>
  {''.join(rows)}
  {groups_html}
  {f'<h2>失敗的段落（{len(errors)}）</h2><ul class="list">{errors_html}</ul>' if errors else ''}
</div>
"""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("source", help="來源 .md（repo 相對或絕對路徑）")
    # 模型預設**維持定案 #44 的 gemma4:latest**，即使目前的開發機載不動它。
    # 語料是產一次、人審一次、長期被引用的東西，降級模型產出的擷取物看不出差別
    # 卻會一直被當權威引用；機器不夠力時用 --model 明確降級，不要讓它變成預設。
    ap.add_argument("--model", default="gemma4:latest")
    # Ollama 原生預設埠。桌機用的 11435 是 SSH 反向通道的埠（規格 §17.11.2），
    # 那是「怎麼接過去」的機器設定，不該當成這支腳本的預設值。
    # ⚠️ 2026-08-16 實測：目前這台筆電從 WSL 連 11434 是 Connection refused
    # ——Ollama 綁在 Windows 的 loopback，而 WSL 還是 NAT（`.wslconfig` 沒有 networkingMode=mirrored）。
    # 接通方式未定（見 STATUS.md），走 SSH 通道的機器用 QA_OLLAMA_URL 或 --url 指定。
    # 端點的三層來源，由近而遠：--url ＞ QA_OLLAMA_URL ＞ **switch-llm.sh 寫的那個** ＞ 預設 11434。
    #
    # 第三層是 2026-08-25 補的，補的是一次真實的重工：`llm-profile.env` 明明已經寫著
    # `Features__QaAssistant__OllamaUrl=…:11435`（app 那側早就切到 Win11 了），
    # 但這支腳本只認 QA_OLLAMA_URL，於是每一次呼叫都得手動再加一次 --url。
    # 同一台機器上「後端切到哪」應該只有一個事實來源——那個事實就是 switch-llm.sh 寫的組態。
    #
    #     source ~/.cache/ollama-pull/llm-profile.env   # 之後 app 與這支腳本走同一個端點
    ap.add_argument("--url", default=(
        os.environ.get("QA_OLLAMA_URL")
        or os.environ.get("Features__QaAssistant__OllamaUrl")
        or "http://127.0.0.1:11434"))
    ap.add_argument("--lang", default="zh", choices=["zh", "en"])
    ap.add_argument("--limit", type=int, default=0, help="只跑前 N 段（確認管線會動）")
    # 補跑用：只跑指定的幾段。存在的理由是**整份重跑會賠掉人工審核**——
    # 產生器不是決定性的，重跑一份 34 則的 manifest 會把 34 則的措辭全部改寫，
    # 於是先前逐則核可過的結論失效，而 manifest 上看不出哪幾則其實沒變。
    # 有了這個參數，救一段失敗的成本就是一段，不是一整份。
    ap.add_argument(
        "--sections", default="",
        help="只跑這幾段（逗號分隔的段落編號，即列印時的 [NN]）。補跑失敗段落用。",
    )
    ap.add_argument(
        "--min-chars", type=int, default=40,
        help="小於此字數的段落不送模型（會列出來，不會安靜丟掉）",
    )
    ap.add_argument(
        "--code-density", type=float, default=0.02,
        help="圍籬程式訊號密度達此值的段落不送模型（實測：公式 0.000、C# 範例 0.028 起）",
    )
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--out", default=".claude/qa-extracts")
    ap.add_argument(
        "--review",
        default=str(Path.home() / ".cache" / "projectz-qa-extracts"),
        help="審核頁輸出目錄（不進版控）",
    )
    args = ap.parse_args()

    root = repo_root(Path.cwd().resolve())
    src = Path(args.source).resolve()
    if not src.is_file():
        sys.exit(f"來源檔不存在：{src}")
    rel = src.relative_to(root).as_posix()
    check_source_allowed(rel, root)
    is_code = rel.endswith(".cs")

    text = src.read_text(encoding="utf-8")
    source_hash = sha256_of_source(src)
    sections = split_sections(text, rel)

    # 段落層紅線（③）。放在最前面：命中的段落不該出現在後面任何一欄，
    # 連「未送模型」欄都不該——那一欄是給人掃過去看有沒有漏收的，而這些是**不能收**。
    # 只印檔案與行號，不印本文：把授權段落的內容印進終端機／審核頁，
    # 等於換一個地方外洩，紅線就白設了。
    redlined = []
    if is_code:
        redlined = [x for x in sections if CODE_REDLINE_BODY.search(x.body)]
        sections = [x for x in sections if x not in redlined]
        for x in redlined:
            print(f"[{x.index:04d}] {rel}:{x.index}　⛔ 命中內容紅線，不送模型（本文不列印）")

    # 切得出來但不該送模型的（章節位置產不出來／重複）。**列出來，不安靜丟掉**：
    # 程式碼來源的失敗方向就是安靜地少收，而少收在畫面上長得跟「這支沒東西」一樣。
    unusable = [s for s in sections if CS_UNUSABLE in s.heading]
    sections = [s for s in sections if CS_UNUSABLE not in s.heading]
    for s in unusable:
        print(f"[{s.index:04d}] {s.heading}　⚠ 未送模型")

    forced = SECTION_FORCE_EXCLUDE.get(rel, [])
    if forced:
        before = len(sections)
        sections = [
            s for s in sections
            if not any(p.search(s.heading) or p.search(s.body) for p in forced)
        ]
        removed = before - len(sections)
        print(f"段落級強制排除：{removed} 段（§10.11.8）")
        # 0 段代表 pattern 對不上，不是「這份沒東西要排」——這兩件事在畫面上長得一樣，
        # 而前者是紅線失效。設了規則卻一段都沒排到，一律當成要查的事。
        if removed == 0:
            print("  ⚠ 本檔設有強制排除規則卻一段都沒命中，請先確認 pattern 是否仍對得上原文。")

    dev_sections = [s for s in sections if SECTION_SKIP.search(s.heading)]
    sections = [s for s in sections if not SECTION_SKIP.search(s.heading)]
    for s in dev_sections:
        print(f"[{s.index:02d}] {s.heading[:44]}　⚠ 開發導向章節，未送模型")

    too_short = [s for s in sections if len(s.body) < args.min_chars]
    sections = [s for s in sections if len(s.body) >= args.min_chars]
    code_heavy = [s for s in sections if code_density(s.body) >= args.code_density]
    sections = [s for s in sections if code_density(s.body) < args.code_density]
    for s in too_short:
        print(f"[{s.index:02d}] {s.heading[:44]}　⚠ 只有 {len(s.body)} 字，未送模型")
    for s in code_heavy:
        print(f"[{s.index:02d}] {s.heading[:44]}　⚠ 程式碼訊號 {code_density(s.body):.3f}，未送模型")

    if args.sections:
        want = {int(x) for x in args.sections.replace("，", ",").split(",") if x.strip()}
        # 產出的 manifest 只會有這幾段。寫回版控目錄的話，那份 manifest 會從
        # 「整份規格的擷取物」變成「一則」——而檔案看起來完全正常，
        # 少掉的東西沒有任何痕跡。所以補跑一律寫到別處，再由人決定怎麼併回去。
        if (root / Path(args.out).expanduser()).resolve() == (
            root / ap.get_default("out")
        ).resolve():
            sys.exit(
                "--sections 會產出只含這幾段的 manifest，寫回版控目錄等於把整份截斷。"
                "請用 --out 指到別處（例如 ~/.cache/qa-extract-run/patch）。"
            )
        missing = want - {s.index for s in sections}
        if missing:
            # 要的段落不在待送清單裡有兩種可能：編號不存在，或它被閘門／字數／
            # 程式碼密度擋掉了。安靜地少跑幾段會讓人以為補跑成功了。
            sys.exit(f"這幾段不在待送模型的清單裡：{sorted(missing)}（不存在，或被上面的規則擋掉）")
        sections = [s for s in sections if s.index in want]

    if args.limit:
        sections = sections[: args.limit]

    preflight(args.url, args.model)
    print(f"來源 {rel}　雜湊 {source_hash[:12]}　段落 {len(sections)}　模型 {args.model}")
    print()

    extracts: list[Extract] = []
    skipped: list[Section] = []
    errors: list[tuple[Section, str]] = []
    # 程式碼的 manifest 檔名要帶模組：`extract/<stem>` 是索引鍵（匯入器用檔名當 FilePath），
    # 而 `.cs` 同名檔在不同模組下很常見——撞名的後果是後匯入的那份把前一份整個蓋掉。
    stem = stem_for(rel)
    # slug 前綴必須通得過匯入器的 `^[a-z0-9][a-z0-9-]*$`，而檔名不保證合規：
    # `APPROVAL-WORKFLOW.md` 是大寫、`business_data_flow_reconciliation.md` 有底線。
    # **這個錯的代價不是該份不生效，是整批中止**——匯入器的 Validate 一律拋例外，
    # 症狀是正式機啟動時所有規則類問題一起答不出來，而手冊照常，完全不像匯入壞了。
    # manifest 檔名與審核頁仍用原本的 stem，只有 slug 正規化。
    slug_stem = slug_stem_for(rel)
    taken: set[str] = set()   # slug 唯一性由 slug_of 維護，見該函式

    for s in sections:
        label = f"[{s.index:02d}] {s.heading[:44]}"
        try:
            answer, elapsed = ask(
                args.url, args.model,
                (CODE_PROMPT if is_code else PROMPT).format(heading=s.heading, body=s.body),
                args.timeout,
            )
            parsed = parse(
                answer,
                CODE_MIN_TEXT if is_code else 40,
                s.heading.split(" › ")[-1] if is_code else "",
            )
        except (urllib.error.URLError, TimeoutError, RuntimeError) as ex:
            print(f"{label}　✗ {ex}")
            errors.append((s, str(ex)))
            continue

        if parsed is None:
            print(f"{label}　— 棄權")
            skipped.append(s)
            continue

        title, body = parsed
        e = Extract(
            slug=slug_of(slug_stem, s.key or s.heading, taken),
            lang=args.lang,
            heading_path=s.heading,
            title=title,
            text=body,
            section=s,
            seconds=elapsed,
            flags=flag(body, s.body),
        )
        extracts.append(e)
        print(f"{label}　✓ {title}　{elapsed:.1f}s" + ("　⚑" * len(e.flags)))

    # manifest。**approved 永遠是 false**，見檔頭。
    manifest = {
        "sourcePath": rel,
        "sourceHash": source_hash,
        "entries": [
            {
                "slug": e.slug,
                "lang": e.lang,
                "headingPath": e.heading_path,
                "text": f"{e.title}｜{e.text}",
                "approved": False,
            }
            for e in extracts
        ],
    }
    # expanduser 後若已是絕對路徑，`root / p` 會直接回那個絕對路徑——
    # 也就是 --out 本來就允許指到 repo 外（冒煙跑不想污染版控時會這樣用）。
    # 但收尾的路徑列印一路假設它在 repo 內，於是「檔案寫成功了才在最後一行炸掉」，
    # 退出碼還是非 0。包裝腳本會把這種情況讀成整批失敗。
    out_dir = root / Path(args.out).expanduser()
    out_dir.mkdir(parents=True, exist_ok=True)
    out_file = out_dir / f"{stem}.json"

    # **一則都沒有時不寫 manifest。** 匯入器對 `entries: []` 是拋例外整批中止
    # （它自己的訊息就寫著「要停用整份請刪檔，不要留一份空的」），
    # 而整批中止的症狀是正式機所有規則類問題一起答不出來、手冊照常——完全不像匯入壞了。
    #
    # 這在 `.md` 那側一直沒踩到是運氣：每份規格都至少擷得出一則。
    # 程式碼來源會**經常**踩到——整支檔案的註解全是實作取捨時，模型應該全部棄權，
    # 那是正確行為，不該因此炸掉整批。
    if not extracts:
        print(f"⚠ 一則都沒擷到，不寫 manifest（避免 entries 為空讓匯入器整批中止）：{stem}.json")
        if out_file.exists():
            print(f"⚠ 但舊的 {out_file.name} 還在，且它已經對不上這次的結果——"
                  "請自行確認要刪掉還是保留（腳本不動已核可過的東西）。")
    else:
        out_file.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )

    review_dir = Path(args.review).expanduser()
    review_dir.mkdir(parents=True, exist_ok=True)
    review_file = review_dir / f"{stem}.html"
    review_file.write_text(
        render_review(
            rel, args.model, source_hash, extracts, errors,
            [
                ("模型棄權的段落", "模型判定這一段沒有使用者需要知道的規則。", skipped),
                ("程式碼為主、沒送模型的段落",
                 "圍籬內的程式訊號密度超過門檻。這一欄是為了擋掉「把介面定義改寫成空話」。",
                 code_heavy),
                ("太短、沒送模型的段落",
                 "短不等於不重要——規格裡最短的段落往往是最濃的那條規則，這一欄要看。",
                 too_short),
                ("章節位置產不出來或重複、沒送模型的段落",
                 "程式碼來源專有：註解的第一句話產不出使用者看得懂的章節位置，"
                 "或同一支檔案裡兩則的章節位置一樣。少收一則勝過產出一個指不回原文的引文。",
                 unusable),
                ("命中內容紅線、不送模型的段落",
                 "授權／指紋／2FA／金鑰這類機制本身。**本文刻意不顯示**——"
                 "印在這裡等於換一個地方外洩。只列行號，要看請直接開原始碼。",
                 [Section(f"{rel}:{x.index}", "（本文不顯示）", x.index) for x in redlined]),
                ("開發導向章節、沒送模型的段落",
                 "標題是測試案例／下一步／實作補強這類。詞是逐個量過精確率才收的，"
                 "但仍會有漏網——這一欄要掃過，看有沒有真規則被章節標題連坐。",
                 dev_sections),
            ],
        ),
        encoding="utf-8",
    )

    flagged = sum(1 for e in extracts if e.flags)
    print()
    print(
        f"擷取 {len(extracts)} 則（其中 {flagged} 則有旗標）／棄權 {len(skipped)}"
        f"／程式碼段 {len(code_heavy)}／太短 {len(too_short)}"
        f"／開發章節 {len(dev_sections)}／章節位置不可用 {len(unusable)}／失敗 {len(errors)}"
    )
    try:
        shown = out_file.relative_to(root)
    except ValueError:
        shown = out_file  # repo 外（冒煙跑），印絕對路徑
    print(f"manifest  {shown}　（全部 approved:false，等人核可）"
          if extracts else "manifest  未寫出（0 則）")
    print(f"審核頁    {review_file}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
