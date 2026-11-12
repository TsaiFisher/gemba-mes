#!/usr/bin/env python3
"""未核可擷取物的審核清單（規格 §10.11.4）。

**核可是唯一防線，而防線的成本是逐則讀。**這支腳本不做判斷，它做的是把
「要讀什麼、先讀哪一則、讀的時候要對照哪一段原文」擺在同一頁上——
manifest 是給機器讀的 JSON，逐則翻它去對照原文，是審核真正的摩擦所在。

產物兩份：
  · 終端摘要——每份還剩幾則、建議的批次順序
  · 審核頁 HTML（`--html`）——每則擷取物並排它擷取自哪一段原文，附機器旗標

**排序即建議**：有旗標的排最前面（機器已經看出可疑），其次是已投入審核量高的
manifest（`mfg-domain` 155 則已審 96，續審的邊際價值最高）。

核可仍然是人的動作、且記在 git（定案 #45）。`--approve` 只是省掉手改 JSON 的
編輯動作，它一次只認明確指名的 slug，不接受萬用字元，也拒絕在過期的 manifest 上動作。

用法：
    scripts/qa-extract-worklist.py                          # 終端摘要
    scripts/qa-extract-worklist.py --html ~/.cache/qa-review.html
    scripts/qa-extract-worklist.py --manifest mfg-domain --html ~/.cache/mfg.html
    scripts/qa-extract-worklist.py --approve <slug>[,<slug>…]   # 會先印出內容再問
"""

from __future__ import annotations

import argparse
import hashlib
import html
import importlib.util
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXTRACTS = ROOT / ".claude" / "qa-extracts"
DECISIONS = EXTRACTS / "DECISIONS.md"

# ⚠️ 「可以繞過某道管制」這種句型，是這份語料最貴的一種錯——定案 #37 的判定依據
# 正是 `mfg-domain.md` §26 那三句「不做備料直接開工即可繞過 FPI」。
# 它讀起來完全像一條正常規則，機器抓不出對錯，**但抓得出句型**，
# 而審核者看到標記就會知道這一則要用不同的力道讀。
BYPASS = re.compile(r"繞過|跳過|略過|即可(不|免)|不需(要)?經過|無需(核准|審核|檢驗)|可以不")

# ⚠️ 未來式：定案 #39 說得很直白——「過期之外還有對稱的第二種失效」，
# 講過去與講未來**都不是現況**。`mfg-domain.md` 有 104 行命中「規劃中／後續／Phase X」，
# 而擷取改寫最擅長的就是把「預計會做」寫成「系統會」。
#
# **這是旗標不是排除**：實測 `Phase A：產能模型與班別鏈（已實作）` 底下寫的是現行規則，
# 而同一份的 `Phase C：排程引擎` 寫的是「目前處於開發階段……預計會透過」。
# 兩者的標題長得一樣，只有讀內容分得出來——所以交給人，機器只負責標記要用力讀哪幾則。
# ⚠️ 「只剩災害、沒有規則」——原文是「**X 必須 Y，否則 Z**」，而擷取物只留下 Z。
# 三次實例：第三批 `afcbf207`（沒說釋放規則、只說不釋放會永久鎖庫存）、
# 同批 `2d453c20`（只留括號裡的「否則甘特圖長度不反映進度」）、
# 第五批 `8af99eb8`（只剩「這樣做是為了…」，「這樣做」沒有先行詞）。
#
# 🚫 **原本設想的偵測方式行不通，這裡記下來免得日後有人再想一次**：
#    以為徵兆是「擷取物以『這樣做／因此／所以』起手」（承接詞需要先行詞，
#    而先行詞正是被丟掉的那一半）。掃全語料 1288 則**只命中 1 則**，
#    就是已經退回的 `8af99eb8`——三次實例裡的另外兩次根本不是這個開頭。
#    改掃「如果沒有…」這種條件否定開頭：命中 36 則，**其中 35 則是正常規則**
#    （「如果沒有指定時區就用使用者的時區」——那是預設值規則，完全成立）。
#
# 真正分得開的不是開頭，是**後果是「系統的動作」還是「一個災害」**：
#    正常：如果沒有指定 X，系統**會用 Y** ← 後果是系統動作
#    有問題：如果沒有 Y，**會導致**庫存永久鎖住 ← 後果是災害
# 所以條件否定與災害詞**兩個都要命中**才標記。實測 1288 則命中 21 則。
#
# ⚠️ **精確率大約兩成**，剩下八成是「規則 ＋ 為什麼」——那是好的擷取物，不是問題。
# 所以它是旗標不是閘門：21 則值得人用力讀一遍，遠比讀 1288 則實際。
# 它確實抓到了一則漏網（`03b8f8a2`，已核可、純粹在描述一個已修掉的實作陷阱，
# 而同一句註解的另外兩份複製早就退回了——**它們文字不同，逐字掃描抓不到**）。
HARM = re.compile(
    r"導致|永久|永遠|再也|回不來|不回來|灌爆|爆掉|被扣兩次|重複(扣|過帳|計算|認列)"
    r"|孤兒|掛在帳上|對不起來|整批(消失|失效)|靜默|悄悄|白做|失真|錯亂|無法回流|鎖住")
COND_NEG = re.compile(r"(如果|若|要是|倘若|一旦|假如)[^，。]{0,20}?(沒有|未|沒|不會|無法|不能|不)")

FUTURE_HEAD = re.compile(r"時程|分工|切片實作|我的建議|下一步|路線|Roadmap|未來|規劃中")
FUTURE_TEXT = re.compile(
    r"將會分階段|預計|尚未實作|規劃中|後續(將|會)|第[一二三四五六七八九十]+週|建議(採用|優先|先)")


def load_generator():
    spec = importlib.util.spec_from_file_location(
        "qa_extract_generate", ROOT / "scripts" / "qa-extract-generate.py")
    mod = importlib.util.module_from_spec(spec)
    sys.modules["qa_extract_generate"] = mod
    spec.loader.exec_module(mod)
    return mod


GEN = load_generator()

REJECTED: set[str] = set()
INCLUDE_REJECTED = False


class Item:
    def __init__(self, manifest: Path, entry: dict, source_body: str):
        self.manifest = manifest
        self.entry = entry
        self.body = source_body
        # 機器查得掉的兩項（語彙外洩、原文沒有的數字）沿用產生器的判定，
        # 不另寫一份——旗標的定義漂開，審核頁與產生器印的就會互相矛盾。
        self.flags = GEN.flag(entry["text"], source_body) if source_body else [
            "⚠ 對不到原文段落（標題可能被改過）——這一則要連原文一起確認"
        ]
        if BYPASS.search(entry["text"]):
            self.flags.append("⚠ 含「可繞過管制」句型：定案 #37 的紅線正是這一類，請對照原文確認")
        body_only = entry["text"].split("｜", 1)[-1]
        if COND_NEG.search(body_only) and HARM.search(body_only):
            self.flags.append(
                "⚠ 疑似「只剩災害、沒有規則」：原文多半是「X 必須 Y，否則 Z」而只擷到 Z。"
                "**八成是誤報**（規則＋為什麼是好的擷取物），請確認這一則有沒有把規則本身寫出來")
        if FUTURE_HEAD.search(entry["headingPath"]) or FUTURE_TEXT.search(entry["text"]):
            self.flags.append(
                "⚠ 疑似非現況（時程／規劃／建議）：定案 #39 只取已實作的現況。"
                "同一個 Phase 標題底下兩種都有，請確認這一則描述的是**現在的行為**")

    @property
    def slug(self) -> str:
        return self.entry["slug"]

    @property
    def heading(self) -> str:
        return self.entry["headingPath"]


def duplicate_flags(rejected: set[str]) -> dict[str, str]:
    """逐字重複的擷取物 → 要標在它身上的訊息。**跨 manifest 比對，這是重點。**

    🚨 **2026-08-25 加的，補的是一次真實的漏網**：程式碼第一批 177 則逐則審完、
    使用者說「其餘均可保留」之後，掃描又找出兩對逐字相同的擷取物，**兩對都被保留了**。
    不是誰看漏——manifest 是一支檔案一份，切段器只在同一支檔案內去重，
    而審核頁把兩則隔了幾十則擺著（一則在 FIN、一則在 INV），跨檔重複在視覺上不存在。

    而且它抓到的不只是重複：**重複的來源通常是同一句複製貼上的註解**
    （那句「開單僅同步核准狀態顯示」在 6 支 INV 控制器裡各出現兩次），
    **一旦被改寫錯，就會一次錯很多份**。那兩對裡的兩則正是錯的——
    一則把「早期誤加 /100 使稅額縮成百分之一」寫成「已將百分比縮小為十分之一」
    （數字與時態雙錯），一則把 `RemoveRange` 要 `Clear()` 的理由寫成系統會重複過帳。

    比對用去標點後的說明本文（`｜` 之後），不含標題與章節位置：
    同一句規則在不同檔案下，標題與章節位置本來就會不一樣。
    """
    texts: dict[str, list[tuple[str, str, bool]]] = {}
    for f in sorted(EXTRACTS.glob("*.json")):
        d = json.loads(f.read_text(encoding="utf-8"))
        for e in d["entries"]:
            body = e["text"].split("｜", 1)[-1]
            key = re.sub(r"[\s，。、；：（）「」]", "", body)
            texts.setdefault(key, []).append((f.stem, e["slug"], e["approved"]))

    out: dict[str, str] = {}
    for group in texts.values():
        if len(group) < 2:
            continue

        # **已經處理完的重複群組不再出聲。** 留一則核可、其餘進 DECISIONS.md，
        # 就是這件事的正確結局；還一直警告的話，下一個人會學會忽略整欄
        # ——§10.11.4 記過一次同樣的教訓（誤報的旗標被當判定，整欄就廢了）。
        approved = sum(1 for _, _, a in group if a)
        pending = [s for _, s, a in group if not a and s not in rejected]
        if approved <= 1 and not pending:
            continue

        for stem, slug, _ in group:
            others = [f"{s}{'（已核可）' if a else ''}" for st, s, a in group if s != slug]
            out[slug] = ("⚠ 與其他 " + str(len(others)) + " 則**逐字相同**："
                         + "、".join(others)
                         + "。只留一則（優先留來源檔最不常動的那支），"
                         + "並回頭確認那句原文是不是複製貼上的——**錯一次會錯很多份**")
    return out


DUPLICATES: dict[str, str] = {}


def rejected_slugs() -> set[str]:
    """已經審過、決定不收的 slug（`.claude/qa-extracts/DECISIONS.md`）。

    🚨 **`approved: false` 同時代表「還沒審」與「審過、不收」**，manifest 分不出這兩者
    ——退回的決定記在 DECISIONS.md，這是該檔開頭第一句話寫的。
    不讀它就把 `approved: false` 全部當待審，會**把 194 則已經裁決過的東西重新排進待審**，
    而審核者要讀到第幾則才發現「這我上次退過」是沒有上限的。

    （2026-08-24 實測：本腳本第一版正是這樣，177 則「待審」有 177 則早就裁決過。）

    🚨 **只認表格列裡的第一個代號，那一格就是〈代號〉欄。**
    先前是對整份檔案 `re.findall`，於是**任何被提到的代號都算退回**——包括
    退回理由裡寫的「與 `X` 逐字相同，保留後者」的那個 `X`。
    2026-08-25 實測踩到：第二批去重時把要保留的 `code-ord-salesordercontroller-da0165cc`
    與 `code-ord-goodsshipmentcontroller-38770e80` 寫進理由欄，兩則**當場從審核頁上消失**，
    而消失的樣子跟「這一則本來就不存在」一模一樣。
    第一批沒事只是因為那幾個保留對象都已經 `approved:true`，
    而 `approved` 在 `collect()` 裡先短路（見該函式），輪不到這裡判。

    **哪些表算退回，看表頭有沒有〈退回〉兩個字**，不看它在哪一節底下。
    第一版是用節標題排除（跳過〈保留待查〉〈尚未審核〉），當天就不夠用了：
    同一節底下新增一張〈保留但改了文字〉的表，5 則要保留的**又被算成退回**。
    節標題是散文，寫的人想怎麼下就怎麼下；表頭是欄位名，寫的人為了讓表讀得懂
    本來就得寫對。**讓判定依賴那個非寫對不可的東西。**

    🚨 **一格裡的代號全都算**（2026-08-26 修，這個家族的**第五次**）。
    上一版每格只取第一個（`pat.search` ＋ `break`），而語意近重複的退回是**按群**記的，
    代號欄用 `<br>` 放三則。於是每列只算到第一則，退回數 212 對不上本檔記的 218。
    改成 `findall` 收整格，但**仍然只認第一個有代號的欄**——理由欄常寫「留 `xxxx`」，
    收進來會把要保留的那則誤判成退回（那正是第二次踩到的那個坑）。
    ⚠️ 這次一如既往是靠人工對帳「共 N 則」發現的：不變式「退回名單 ⊆ 未核可」
    擋得住多算、擋不住漏算，而漏算與待審在定義上機器分不出來。

    🚨 **表頭只認一次**（2026-08-26 修，這個家族的第三次）。
    上一版是「只要 `harvest` 還沒開，就把這一列當表頭候選」，於是表頭沒中之後，
    **資料列會繼續被當成表頭試**——只要某一列的理由欄裡出現「退回」兩個字，
    `harvest` 就從那裡開始打開，**它下面的每一列都被算成退回**。
    實際踩到：〈保留但改了文字〉表裡有一列寫著「舊的退回／爭議紀錄已作廢」，
    於是**它下一列的 `a7a98953` 被判成退回**，而那是一則要保留的。
    症狀一如既往只有一個：DECISIONS.md 的「共 N 則」與 worklist 的總數對不上
    （預期 191、印出 192）。改成進表時只讀第一列非對齊列當表頭，
    表頭不含「退回」就整張表跳過，直到表結束。
    """
    if not DECISIONS.is_file():
        return set()
    pat = re.compile(r"`([a-z0-9-]+-[0-9a-f]{8})`")
    out: set[str] = set()
    in_table = False                 # 是否已經讀過這張表的表頭
    harvest = False                  # 這張表是不是退回名單
    for line in DECISIONS.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|"):
            in_table = harvest = False   # 表格結束（空行／散文／標題）就關掉
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if all(set(c) <= set("-: ") for c in cells) or not cells:
            continue                 # 對齊列（|---|---|）
        if not in_table:
            # 進表後的第一列非對齊列 ＝ 表頭，**只判這一次**。
            in_table = True
            harvest = any("退回" in c for c in cells)
            continue
        if not harvest:
            continue                 # 不是退回名單的表，整張跳過
        for cell in cells:
            found = pat.findall(cell)
            if found:
                # **這一格裡的全部**都算：一列可以退好幾則（同一群的重複，
                # 代號欄用 `<br>` 分行）。2026-08-26 的第五個變體就是只認第一個，
                # 退回數 212 對不上本檔記的 218，差的正是多代號那幾列的第二、三則。
                out.update(found)
                # 但**只認第一個有代號的欄**：理由欄常寫「留 `xxxx`」，
                # 那是保留的那一則，收進來會把它誤判成退回。
                break
    return out


def collect(only: str | None) -> tuple[list[Item], list[str]]:
    items: list[Item] = []
    stale: list[str] = []

    for f in sorted(EXTRACTS.glob("*.json")):
        if only and only not in f.stem:
            continue
        d = json.loads(f.read_text(encoding="utf-8"))
        src = ROOT / d["sourcePath"]
        if not src.is_file():
            stale.append(f"{f.name}（來源檔不存在）")
            continue
        if GEN.sha256_of_source(src) != d["sourceHash"]:   # 去 BOM，見產生器
            # 過期的 manifest 不列入審核：審過的內容會在對帳後被重產，等於白審。
            stale.append(f.name)
            continue
        # 第二個參數不可省：產生器依副檔名分派切段（§10.11.13），
        # 省略時 .cs 會走 markdown 那條路而回 0 段——於是每一則程式碼擷取物
        # 都被標成「對不到原文段落」，審核頁的並排欄整片空白，而它看起來像是標題被改過。
        bodies = {s.heading: s.body
                  for s in GEN.split_sections(src.read_text(encoding="utf-8"), d["sourcePath"])}
        for e in d["entries"]:
            if e["approved"]:
                continue
            if e["slug"] in REJECTED and not INCLUDE_REJECTED:
                continue
            it = Item(f, e, bodies.get(e["headingPath"], ""))
            if e["slug"] in DUPLICATES:
                it.flags.insert(0, DUPLICATES[e["slug"]])
            if e["slug"] in REJECTED:
                it.flags.insert(0, "↩ 這一則 DECISIONS.md 有退回紀錄——重讀是在複審，不是初審")
            items.append(it)
    return items, stale


def order_key(it: Item, invested: dict[str, int]):
    # ① 有旗標的先看　② 該份已投入的審核量高的先看　③ 同份維持原順序
    return (0 if it.flags else 1, -invested.get(it.manifest.name, 0), it.heading)


def summary(items: list[Item], stale: list[str]) -> dict[str, int]:
    total_entries = total_ap = total_rej = 0
    for f in EXTRACTS.glob("*.json"):
        d = json.loads(f.read_text(encoding="utf-8"))
        total_entries += len(d["entries"])
        total_ap += sum(1 for e in d["entries"] if e["approved"])
        total_rej += sum(1 for e in d["entries"]
                         if not e["approved"] and e["slug"] in REJECTED)
    print(f"語料 {total_entries} 段：核可 {total_ap}　已退回 {total_rej}"
          f"（DECISIONS.md 有紀錄）　**還沒審 {total_entries - total_ap - total_rej}**\n")

    per: dict[str, list[Item]] = {}
    for it in items:
        per.setdefault(it.manifest.name, []).append(it)

    invested = {}
    print(f"{'manifest':42} {'未核可':>6} {'其中有旗標':>10} {'該份已審':>8}")
    total = flagged = 0
    for f in sorted(EXTRACTS.glob("*.json")):
        d = json.loads(f.read_text(encoding="utf-8"))
        ap = sum(1 for e in d["entries"] if e["approved"])
        invested[f.name] = ap
        rows = per.get(f.name, [])
        if not rows:
            continue
        fl = sum(1 for r in rows if r.flags)
        total += len(rows)
        flagged += fl
        print(f"  {f.name:40} {len(rows):6} {fl:10} {ap:8}")

    print(f"\n  合計未核可 {total} 則，其中 {flagged} 則有旗標。")
    if stale:
        print(f"  ⚠ 跳過 {len(stale)} 份過期 manifest（審了會被對帳重產，等於白審）："
              f"{'、'.join(stale)}")
        print("    先跑：scripts/qa-extract-reconcile.py")
    return invested


CSS = """
:root{--ink:#1a1a1a;--dim:#6b6b6b;--line:#dcd8d0;--bg:#faf9f7;--card:#fff;
      --out:#f4f8f4;--warn:#8a4b00;--warnbg:#fff6e8}
@media (prefers-color-scheme:dark){:root{--ink:#e8e6e3;--dim:#9a9691;--line:#37342f;
      --bg:#16150f;--card:#1e1d18;--out:#1b241b;--warn:#e0a355;--warnbg:#2b2113}}
*{box-sizing:border-box}
body{margin:0;padding:2rem 1.5rem 5rem;background:var(--bg);color:var(--ink);
     font:15px/1.75 -apple-system,"Noto Sans TC",sans-serif;max-width:1180px;margin-inline:auto}
h1{font-size:1.35rem;margin:0 0 .3rem}
.meta{color:var(--dim);font-size:.85rem;margin-bottom:1.5rem}
.note{background:var(--warnbg);border-left:3px solid var(--warn);padding:.9rem 1.1rem;
      border-radius:0 5px 5px 0;margin-bottom:2rem;font-size:.9rem}
.grp{margin:2.5rem 0 1rem;font-size:1.05rem;border-bottom:2px solid var(--line);padding-bottom:.4rem}
.pair{background:var(--card);border:1px solid var(--line);border-radius:6px;margin-bottom:1.2rem;
      overflow:hidden}
.bar{display:flex;gap:.8rem;align-items:baseline;padding:.6rem .9rem;border-bottom:1px solid var(--line);
     flex-wrap:wrap}
.slug{font-family:ui-monospace,monospace;font-size:.78rem;color:var(--dim)}
.head{color:var(--dim);font-size:.85rem;flex:1;min-width:14rem}
.cols{display:grid;grid-template-columns:1fr 1fr}
@media (max-width:820px){.cols{grid-template-columns:1fr}}
.out{padding:1rem;background:var(--out)}
.src{padding:1rem;border-left:1px solid var(--line);overflow-x:auto}
.src pre{margin:0;white-space:pre-wrap;font-family:ui-monospace,monospace;font-size:.8rem;
         line-height:1.6;color:var(--dim)}
.flag{background:var(--warnbg);color:var(--warn);border-radius:4px;padding:.35rem .6rem;
      font-size:.8rem;margin:.35rem .9rem}
code{font-family:ui-monospace,monospace;font-size:.85em}
"""


def render(items: list[Item], invested: dict[str, int], out: Path) -> None:
    def esc(s: str) -> str:
        return html.escape(s or "")

    parts = [f"<meta charset='utf-8'><title>擷取物審核清單</title><style>{CSS}</style>",
             "<h1>擷取物審核清單</h1>",
             f"<div class='meta'>未核可 {len(items)} 則　"
             f"｜有旗標 {sum(1 for i in items if i.flags)} 則　"
             f"｜左欄是要審的擷取物，右欄是它擷取自哪一段原文</div>",
             "<div class='note'><b>核可＝把 manifest 裡該則的 <code>approved</code> 改成 "
             "<code>true</code> 並送 PR</b>（定案 #45）。這一頁不會改任何東西。<br>"
             "三個要看的：① 有沒有寫出原文沒有的東西　② 有沒有把「規劃中／已推翻」寫成現況　"
             "③ 有沒有寫出可以繞過管制的做法。<br>"
             "省掉手改 JSON：<code>scripts/qa-extract-worklist.py --approve &lt;slug&gt;[,&lt;slug&gt;…]</code>"
             "</div>"]

    last = None
    for it in items:
        if it.manifest.name != last:
            last = it.manifest.name
            parts.append(f"<div class='grp'>{esc(last)}　"
                         f"<span class='meta'>該份已審 {invested.get(last,0)} 則</span></div>")
        parts.append("<div class='pair'><div class='bar'>"
                     f"<span class='slug'>{esc(it.slug)}</span>"
                     f"<span class='head'>{esc(it.heading)}</span></div>")
        for fl in it.flags:
            parts.append(f"<div class='flag'>{esc(fl)}</div>")
        parts.append("<div class='cols'>"
                     f"<div class='out'>{esc(it.entry['text'])}</div>"
                     f"<div class='src'><pre>{esc(it.body[:4000])}</pre></div>"
                     "</div></div>")

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(parts) + "\n", encoding="utf-8")
    print(f"\n審核頁：{out}")


def approve(slugs: list[str], assume_yes: bool) -> int:
    """把指名的幾則改成 `approved: true`。

    🚨 **預設要確認**，而且會先把每一則的標題與旗標印出來。理由不是防手滑，是
    2026-08-24 真的發生過：交付說明裡的範例指令用了真實 slug，被整行貼上執行，
    於是兩則沒有人讀過內容的擷取物變成已核可——其中一則是「第一到第二週會完成
    技術準備」這種開發時程。**核可是唯一防線（§10.11.4），它不該是一個貼上就生效的動作。**
    """
    want = {s.strip() for s in slugs if s.strip()}
    if not want:
        sys.exit("--approve 要指定 slug（逗號分隔）。")
    if any("*" in s or "?" in s for s in want):
        sys.exit("--approve 不接受萬用字元：核可是逐則的動作（§10.11.4）。")

    # 先讀一遍要核可什麼，印出來給人看，再問。
    preview = []
    for f in sorted(EXTRACTS.glob("*.json")):
        d = json.loads(f.read_text(encoding="utf-8"))
        src = ROOT / d["sourcePath"]
        bodies = ({s.heading: s.body
                   for s in GEN.split_sections(src.read_text(encoding="utf-8"), d["sourcePath"])}
                  if src.is_file() else {})
        for e in d["entries"]:
            if e["slug"] in want and not e["approved"]:
                preview.append(Item(f, e, bodies.get(e["headingPath"], "")))

    if preview:
        print("即將核可：")
        for it in preview:
            print(f"  · {it.slug}　{it.heading[-56:]}")
            print(f"    {it.entry['text'][:78]}…")
            for fl in it.flags:
                print(f"    {fl[:100]}")
        if not assume_yes:
            try:
                if input("\n確認核可以上內容？(yes/N) ").strip().lower() not in ("y", "yes"):
                    print("取消，未動任何檔案。")
                    return 1
            except EOFError:
                sys.exit("\n非互動環境下要核可請加 --yes（並確定上面每一則都讀過了）。")

    done: set[str] = set()
    for f in sorted(EXTRACTS.glob("*.json")):
        d = json.loads(f.read_text(encoding="utf-8"))
        hit = [e for e in d["entries"] if e["slug"] in want]
        if not hit:
            continue

        src = ROOT / d["sourcePath"]
        if not src.is_file() or GEN.sha256_of_source(src) != d["sourceHash"]:
            sys.exit(f"{f.name} 已過期，拒絕核可——這些內容會在對帳後被重產。"
                     " 先跑 scripts/qa-extract-reconcile.py。")

        for e in hit:
            if e["approved"]:
                print(f"  · {e['slug']} 早就是 approved:true，未動")
            else:
                e["approved"] = True
                print(f"  ✓ {e['slug']}　{e['headingPath'][:60]}")
            done.add(e["slug"])
        f.write_text(json.dumps(d, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    missing = want - done
    if missing:
        print(f"\n✗ 找不到這些 slug：{'、'.join(sorted(missing))}")
        return 1
    print(f"\n核可 {len(done)} 則。**記得 commit**——核可的稽核紀錄就是這次 commit（定案 #45）。")
    return 0


def source_duplicates() -> list[tuple[str, list[tuple[str, int]]]]:
    """`code-sources.txt` 上**原始註解**逐字相同的群組（比 duplicate_flags 早一步）。

    `duplicate_flags` 比的是擷取物，因此它只在**跑完模型之後**才看得見；
    這一支比的是原文，開跑之前就看得見。兩者不是二選一：

      · 原文相同、擷取物被改寫成不同措辭 → 只有這一支看得見（重複依然存在，
        而且更難發現，因為兩則讀起來不一樣）
      · 原文不同、擷取物碰巧撞成同一句 → 只有 `duplicate_flags` 看得見

    🚨 **不要拿量測用的 `~/.cache/qa-code-gap/scored.jsonl` 做這件事**：
    `harvest.py:79` 在全域用註解雜湊去重、只留第一次出現，於是拿它掃重複**恆為零**
    ——2026-08-25 實測踩過，那個零看起來跟「真的沒有重複」一模一樣。
    切段一律走 `split_sections`，那是產生器實際會用的那一份。
    """
    root = GEN.repo_root(Path.cwd().resolve())
    listed = [l.split()[0] for l in (root / GEN.CODE_SOURCES_FILE).read_text(encoding="utf-8").splitlines()
              if l.strip() and not l.strip().startswith("#")]
    groups: dict[str, list[tuple[str, int]]] = {}
    for rel in listed:
        f = root / rel
        if not f.is_file():
            continue
        for sec in GEN.split_sections(f.read_text(encoding="utf-8"), rel):
            if GEN.CS_UNUSABLE in sec.heading:
                continue
            body = sec.body.split("\n\n宣告：")[0]
            key = re.sub(r"[\s，。、；：（）「」]", "", body)
            if len(key) < 30:      # 與收錄門檻同一條線，免得標點級差異灌出雜訊
                continue
            groups.setdefault(key, []).append((rel, sec.index))
    return [(k, v) for k, v in groups.items() if len({f for f, _ in v}) > 1]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", help="只看檔名含此字串的 manifest")
    ap.add_argument("--html", help="產出審核頁到這個路徑")
    ap.add_argument("--flagged-only", action="store_true", help="只列有旗標的")
    ap.add_argument("--approve", help="把這幾則（逗號分隔的 slug）改成 approved:true")
    ap.add_argument("--yes", action="store_true",
                    help="跳過確認（非互動環境用；用它之前請確定每一則都讀過）")
    ap.add_argument(
        "--source-duplicates", action="store_true",
        help="只掃來源層逐字重複（不讀 manifest，可在生成之前跑）",
    )
    ap.add_argument(
        "--include-rejected", action="store_true",
        help="連 DECISIONS.md 已退回的也列出來（複審用，例如對帳重產過的那幾則）",
    )
    args = ap.parse_args()

    if args.source_duplicates:
        dups = source_duplicates()
        print(f"來源層逐字重複：{len(dups)} 組（`code-sources.txt` 上的檔案）\n")
        for key, where in sorted(dups, key=lambda kv: -len(kv[1])):
            print(f"  {len(where)} 處　{key[:80]}…")
            for rel, idx in where:
                print(f"      {rel}　段 {idx}")
            print()
        # 留一則的準則見 DECISIONS.md：優先留**來源檔最不常動**的那支，
        # 因為過期綁整支 .cs 的雜湊，留在最穩的那支重審次數最少。
        return 0

    global REJECTED, INCLUDE_REJECTED, DUPLICATES
    REJECTED = rejected_slugs()
    INCLUDE_REJECTED = args.include_rejected
    DUPLICATES = duplicate_flags(REJECTED)

    # 逐字重複要在摘要就講，不能只標在個別項目上：**已核可的那幾則不會出現在待審清單裡**，
    # 而重複的另一半往往正是已核可的那一則（這就是 2026-08-25 那次漏網的形狀）。
    if DUPLICATES:
        print(f"⚠ 全語料有 {len(DUPLICATES)} 則逐字重複**尚未處理**（跨 manifest 比對）：")
        for slug in sorted(DUPLICATES):
            print(f"    {slug}")
        print()

    if args.approve:
        return approve(args.approve.split(","), args.yes)

    items, stale = collect(args.manifest)
    invested = summary(items, stale)
    if args.flagged_only:
        items = [i for i in items if i.flags]
    items.sort(key=lambda i: order_key(i, invested))

    if args.html:
        render(items, invested, Path(args.html).expanduser())
    else:
        print("\n加 --html <路徑> 產出並排原文的審核頁。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
