#!/usr/bin/env python3
"""擷取物工具鏈的自我測試：不呼叫模型、不動工作樹，跑得起來就該是綠的。

**為什麼需要它**：這三支腳本沒有任何測試，而它們壞掉的樣子都不會報錯——
2026-08-25 實測抓到兩個：

  ① `.cs` 改了程式碼、沒動註解 → 對帳判「未變」，把已核可的說明沿用下去、
     順手更新 sourceHash。整支檔案的雜湊本來擋得住，逐段對帳把它放掉了。
     症狀是**擷取物描述的行為已經不存在，而檢索照常回答**。
  ② 對帳拿 headingPath 當識別，但 `.cs` 的 headingPath 是註解的第一句話。
     改一句註解 → 「刪掉一則、多出一則」，已審的那則被丟掉，
     而產生器的 slug 綁的是「檔案＋宣告」，本來就設計成不受註解措辭影響。

兩個都是「跑起來很正常、結果是錯的」，所以要用會紅的東西釘住。

用法：scripts/qa-extract-selftest.py
"""

from __future__ import annotations

import collections
import importlib.util
import os
import tempfile
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXTRACTS = ROOT / ".claude" / "qa-extracts"


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod          # dataclass 需要模組在 sys.modules 裡
    spec.loader.exec_module(mod)
    return mod


GEN = load("qa_extract_generate", ROOT / "scripts" / "qa-extract-generate.py")
REC = load("qa_extract_reconcile", ROOT / "scripts" / "qa-extract-reconcile.py")

FAILED: list[str] = []


def check(cond: bool, label: str, detail: str = "") -> bool:
    print(f"  {'✓' if cond else '✗'} {label}{('　' + detail) if detail else ''}")
    if not cond:
        FAILED.append(label)
    return cond


def manifests() -> list[tuple[Path, dict]]:
    out = []
    for p in sorted(EXTRACTS.glob("*.json")):
        d = json.loads(p.read_text(encoding="utf-8"))
        if (ROOT / d["sourcePath"]).is_file():
            out.append((p, d))
    return out


# ── 1. manifest 與切段結果對得起來 ─────────────────────────────────────
def test_manifests(mans) -> None:
    print("\n1. 版控裡的 manifest 都對得回現在的切段結果")
    bad_slug, bad_head, dup = [], [], []
    for p, d in mans:
        src = d["sourcePath"]
        secs = GEN.split_sections((ROOT / src).read_text(encoding="utf-8"), src)
        by_slug = REC.slug_map(secs, src)
        heads = {s.heading for s in secs}
        for e in d["entries"]:
            if e["slug"] not in by_slug:
                bad_slug.append(f"{p.name}／{e['slug']}")
            if e["headingPath"] not in heads:
                bad_head.append(f"{p.name}／{e['slug']}")
        # 檔內標題重複：審核頁「對回原文」會指到其中一則，而兩則分不出來。
        # 產生器會把重複的改成「（章節位置重複）」丟掉，所以有 entry 引用到才算問題。
        cnt = collections.Counter(s.heading for s in secs)
        for e in d["entries"]:
            if cnt[e["headingPath"]] > 1:
                dup.append(f"{p.name}／{e['headingPath'][:40]}")

    check(not bad_slug, f"{sum(len(d['entries']) for _, d in mans)} 則 entry 的 slug 都反推得到段落",
          "" if not bad_slug else f"對不上 {len(bad_slug)}：{bad_slug[:3]}")
    check(not bad_head, "headingPath 都還找得到對應段落",
          "" if not bad_head else f"對不上 {len(bad_head)}：{bad_head[:3]}")
    check(not dup, "沒有 entry 落在標題重複的段落上",
          "" if not dup else f"{len(dup)} 則：{dup[:3]}")

    # 🚨 同一份 manifest 內 slug 重複 ＝ **整批匯入中止**。
    # `ExtractManifestImporter.Validate` 撞到重複就擲例外，而匯入器是「一份壞掉
    # 就整批中止」——1400 段規則會一起從 Q&A 消失，症狀只是「規則類問題突然都答不出來」。
    #
    # 2026-09-29 真的發生過一次：對帳只重跑一部分段落，產生器的 `taken` 集合裡
    # 沒有沿用下來的那些，於是重產的段落拿到一個沿用段落已經在用的 slug。
    # 而且不是雜湊碰撞——程式碼來源的雜湊輸入是「檔案::宣告行」，同一支 controller
    # 裡出現兩次一模一樣的 `return Json(...)` 是常態。對帳端已補唯一性重算，
    # 這一項是它的回歸守門：資料先紅，才不會等到匯入時整批消失。
    dup_slug = []
    for p_, d in mans:
        cnt = collections.Counter(e["slug"] for e in d["entries"])
        dup_slug += [f"{p_.name}／{s_}×{n}" for s_, n in cnt.items() if n > 1]
    check(not dup_slug, "同一份 manifest 內沒有重複的 slug（重複會讓整批匯入中止）",
          "" if not dup_slug else f"{len(dup_slug)} 組：{dup_slug[:3]}")


# ── 2. 對帳的四種情境 ──────────────────────────────────────────────────
def pick_code_manifest(mans):
    """挑一份 `.cs` manifest：段數夠多，且要動的那一段不是最後一段。"""
    for p, d in mans:
        if not d["sourcePath"].endswith(".cs") or len(d["entries"]) < 3:
            continue
        text = (ROOT / d["sourcePath"]).read_text(encoding="utf-8")
        secs = GEN.split_sections(text, d["sourcePath"])
        for k, s in enumerate(secs[:-1]):
            if GEN.CS_UNUSABLE in s.heading:
                continue
            if any(e["headingPath"] == s.heading for e in d["entries"]):
                return p, d, text, s
    return None


def triage(data, src, old, new):
    t = REC.Triage.__new__(REC.Triage)
    t.data, t.src, t.old_text, t.new_text = data, src, old, new
    t.unchanged, t.changed, t.deleted, t.added = [], [], [], []
    t._classify()
    return t


def counts(t):
    return len(t.unchanged), len(t.changed), len(t.deleted), len(t.added)


def test_reconcile(mans) -> None:
    print("\n2. 對帳對 `.cs` 的四種情境")
    picked = pick_code_manifest(mans)
    if picked is None:
        check(False, "找得到可測的 .cs manifest")
        return
    p, d, orig, s = picked
    src, n = d["sourcePath"], len(d["entries"])
    lines = orig.splitlines()
    span = list(range(s.index - 1, s.index - 1 + len(s.fingerprint.splitlines())))
    print(f"  用 {p.name}　第 {s.index} 行那一段「{s.heading[:34]}」")

    # A. 只改程式碼：插在**宣告行的下一行**。
    #    body ＝ 註解＋宣告行，所以插在這裡 body 一字未改，只有 fingerprint 會變。
    #    ⚠ 插在宣告行**上面**（例如註解後的空行）會把新那行變成 decl，body 跟著變，
    #    這一項就會在缺陷還在的情況下照樣綠——實測踩過。
    ins = next((i for i in span
                if lines[i].strip() and not lines[i].strip().startswith(("//", "[", "#"))), None)
    if ins is None:
        check(False, "這一段找得到程式碼可以改")
        return
    a = "\n".join(lines[:ins + 1] + ["            var __selftest_probe = 1;"] + lines[ins + 1:])
    ta = triage(d, src, orig, a)
    check(counts(ta) == (n - 1, 1, 0, 0), "A 只改程式碼（註解不動）→ 判為『已變』",
          f"得到 {counts(ta)}")

    # B. 只改註解那句話：slug 綁宣告，所以該判「已變」，不是「刪掉再新增」。
    #    要改的必須是**標題取自的那一行**，改別行只證明得了 body 有變、證明不到
    #    「標題變了 slug 也不變」——而後者才是這一項在守的東西。
    #    字要插在那句話**裡面**：加在行尾多半落在句號之後，取出來的第一句根本沒變。
    head_text = s.heading.split(" › ")[-1]
    mark = head_text[:4]
    first = next((i for i in span if mark and mark in lines[i]), None)
    if first is None:
        first = next(i for i in span if lines[i].strip().startswith("//"))
        edited = lines[first] + "的"
    else:
        edited = lines[first].replace(mark, mark + "的", 1)
    b = "\n".join(lines[:first] + [edited] + lines[first + 1:])
    tb = triage(d, src, orig, b)
    ok = check(counts(tb) == (n - 1, 1, 0, 0), "B 只改註解措辭 → 判為『已變』（slug 不因措辭改變）",
               f"得到 {counts(tb)}")
    if ok:
        e = tb.changed[0]
        moved = tb.new_heading(e) != e["headingPath"]
        check(moved, "　 且 new_heading() 給的是現檔標題（--apply 拿它去比對重跑結果）",
              "" if moved else "這次的改動沒有動到標題，這一項沒驗到")

    # C. 什麼都沒改。
    tc = triage(d, src, orig, orig)
    check(counts(tc) == (n, 0, 0, 0), "C 原封不動 → 全部『未變』", f"得到 {counts(tc)}")

    # D. 整段連註解帶程式碼刪掉。
    dd = "\n".join(lines[:span[0]] + lines[span[-1] + 1:])
    td = triage(d, src, orig, dd)
    check(counts(td)[2] == 1, "D 整段刪掉 → 判為『刪除』", f"得到 {counts(td)}")


# ── 3. 判過期的雜湊：三個呼叫端必須是同一支 ────────────────────────────
def test_stale_hash(mans) -> None:
    """🚨 2026-08-25 第二批抓到的：worklist 與 reconcile 各自寫了一份**不去 BOM** 的
    雜湊，於是來源檔一有 BOM，那份 manifest 從產出的那一刻就永遠過期。
    症狀兩邊都是安靜地少收：審核清單把它當「已過期」跳過（`--approve` 一併拒收），
    對帳則在 git 歷史裡找不到相符雜湊而把每一則都判成「已變」。
    第一批一支 BOM 都沒有，所以這個洞在第一批完全看不出來——**要用合成的 BOM 來測**。
    """
    print("\n判過期的雜湊（去 BOM 必須三處一致）")

    WL = load("qa_extract_worklist", ROOT / "scripts" / "qa-extract-worklist.py")
    body = b"// \xe8\xa6\x8f\xe5\x89\x87\npublic class X { }\n"
    with tempfile.TemporaryDirectory() as td:
        f = Path(td) / "WithBom.cs"
        f.write_bytes(b"\xef\xbb\xbf" + body)
        (Path(td) / "NoBom.cs").write_bytes(body)

        check(GEN.sha256_of_source(f) == GEN.sha256_of_source(Path(td) / "NoBom.cs"),
              "產生器：帶 BOM 與不帶 BOM 的同一份內容雜湊相同")
        check(REC.sha_source(f.read_bytes()) == GEN.sha256_of_source(f),
              "對帳 sha_source 與產生器一致")
        # worklist 沒有把雜湊包成函式，直接驗它對真實 manifest 的判定：
        # 若它仍用裸 sha256，帶 BOM 的那幾份會被判過期。
        bom = [(m, d) for m, d in mans
               if (ROOT / d["sourcePath"]).read_bytes().startswith(b"\xef\xbb\xbf")]
        stale = [m.name for m, d in bom
                 if GEN.sha256_of_source(ROOT / d["sourcePath"]) != d["sourceHash"]]
        # ⚠ 這一項**測不到 worklist/reconcile**（它用產生器自己的函式），退回修正照樣綠。
        #   留著是因為它驗的是另一件事：manifest 裡的 sourceHash 確實是去 BOM 版本。
        check(not stale,
              f"帶 BOM 的 {len(bom)} 份 manifest，其 sourceHash 是去 BOM 版本",
              "、".join(stale) if stale else "")
        # 這一項是**探針**：BOM 檔案哪天從清單裡消失，上面那項會退化成恆真。
        check(bool(bom), "語料裡確實有帶 BOM 的來源檔（否則上一項驗不到東西）",
              f"{len(bom)} 份")

        os.chdir(ROOT)
        _items, wl_stale = WL.collect(None)
        check(not any(n.startswith("code-") for n in wl_stale),
              "審核清單沒有把任何程式碼 manifest 判成過期",
              "、".join(wl_stale) if wl_stale else "")


# ── 4. DECISIONS.md 的退回名單：只認〈代號〉欄 ─────────────────────────
def test_rejected_parse() -> None:
    """🚨 2026-08-25 抓到的第二個靜默排除：`rejected_slugs()` 原本對整份 DECISIONS.md
    `re.findall`，於是**退回理由裡提到的代號也被當成退回**——而理由欄最常寫的正是
    「與 `X` 逐字相同，保留後者」的那個 `X`，也就是要**留下來**的那一則。
    症狀是它從審核頁上消失，而消失跟「本來就沒這則」長得一模一樣。
    """
    print("\n退回名單只認〈代號〉欄")
    WL = load("qa_extract_worklist", ROOT / "scripts" / "qa-extract-worklist.py")

    doc = (
        "# 退回紀錄\n\n"
        "| 來源 | 代號 | 段落 | 退回理由 |\n|---|---|---|---|\n"
        "| A.cs | `code-a-aaaaaaaa` | 某段 | 與 `code-b-bbbbbbbb` 逐字相同，保留後者 |\n"
        # 🚨 一格放三個代號（同一群的重複，代號欄用 <br> 分行）。
        #    上一版每格只取第一個 → 後兩則靜默漏算，而「漏算」與「待審」機器分不出來。
        "| G.cs | `code-g-11111111`<br>`code-g-22222222`<br>`code-g-33333333` | 某段 | 同一條規則三處擷取 |\n"
        "| H.cs | `code-h-99999999` | 某段 | 與 `code-h-88888888` 同一條，**留後者** |\n\n"
        "## 保留待查（已讀過，但需要懂規則的人裁決）\n\n"
        "| 來源 | 代號 | 段落 | 保留原因 |\n|---|---|---|---|\n"
        "| C.cs | `code-c-cccccccc` | 某段 | 等人裁決 |\n\n"
        "### 保留但改了文字\n\n"
        "| 代號 | 改了什麼 |\n|---|---|\n"
        # 🚨 這一列的理由裡出現「退回」兩個字。上一版會把它當成表頭而打開 harvest，
        #    於是**它下面那一列**被算成退回——而那是一則要保留的。
        "| `code-e-eeeeeeee` | 舊的退回／爭議紀錄已作廢，改寫成使用者面的說法 |\n"
        "| `code-f-ffffffff` | 觸發條件列漏了，補齊 |\n\n"
        "散文裡提到 `code-d-dddddddd` 是為了說明，不是退回。\n"
    )
    with tempfile.TemporaryDirectory() as td:
        f = Path(td) / "DECISIONS.md"
        f.write_text(doc, encoding="utf-8")
        orig, WL.DECISIONS = WL.DECISIONS, f
        try:
            got = WL.rejected_slugs()
        finally:
            WL.DECISIONS = orig

    check(got == {"code-a-aaaaaaaa", "code-g-11111111", "code-g-22222222",
                  "code-g-33333333", "code-h-99999999"},
          "退回名單剛好是那五則（〈代號〉欄的，含一格三則的那一列）", f"得到 {sorted(got)}")
    check("code-b-bbbbbbbb" not in got, "理由欄寫的「保留後者」沒有被誤判成退回")
    check("code-c-cccccccc" not in got, "〈保留待查〉一節不算退回")
    check("code-d-dddddddd" not in got, "散文提及不算退回")
    # 🚨 節標題是散文，寫的人想怎麼下就怎麼下——第一版用節標題排除，同一天就被
    #    新增的〈保留但改了文字〉繞過（5 則要保留的被算成退回）。判定改看表頭。
    check("code-e-eeeeeeee" not in got, "表頭沒有「退回」的表（例如〈保留但改了文字〉）不算退回")
    # 🚨 第三次：表頭沒中之後，資料列不可以繼續被當成表頭試。
    #    理由欄寫著「退回」的那一列會把 harvest 打開，於是它**下面**的每一列都被算成退回。
    check({"code-g-11111111", "code-g-22222222", "code-g-33333333"} <= got,
          "同一格用 <br> 放多個代號時，整格都算退回")
    check("code-h-88888888" not in got,
          "理由欄寫的「留 `code-h-…`」不算退回（只認第一個有代號的欄）")
    check("code-f-ffffffff" not in got,
          "理由欄出現「退回」二字的那一列，不會讓它下面的列被算成退回")


# ── 5. 殘留清理：清該清的，不清設計如此的 ─────────────────────────────
def test_harm_without_rule_flag() -> None:
    """「只剩災害、沒有規則」旗標：三次已知實例都要標得到，正常的預設值規則不可以標。

    ⚠️ **這條測試釘的是下限不是精確率**。實測全語料 1288 則命中 21 則，
    其中只有約兩成是真的有問題，其餘八成是「規則 ＋ 為什麼」——那是好的擷取物。
    旗標的價值在於把要用力讀的從 1288 則縮到 21 則，不在於它判得準。

    `keep` 那組是**沒有災害詞**的正常規則：有災害詞的正常規則本來就會被標，
    那是已知且接受的誤報，不該拿來當測試案例（會逼人把規則改成迎合資料）。
    """
    WL = load("qa_extract_worklist", ROOT / "scripts" / "qa-extract-worklist.py")

    def flagged(text: str) -> bool:
        body = text.split("｜", 1)[-1]
        return bool(WL.COND_NEG.search(body) and WL.HARM.search(body))

    # 三次已知實例的**原始文字**（後兩則現在的版本已改寫過，這裡用 git 裡的原文）
    known_bad = [
        "備料單接手預留的庫存釋放規則｜如果沒有將備料單接手的部分進行釋放，"
        "工單結案後會導致備料單永久鎖住庫存，使得原本可用的數量無法回流。",
        "排程顯示的時長計算規則｜如果沒有進行批次平移，則在甘特圖上所顯示"
        "「正在做的那條」的長度永遠不會反映實際的進度。",
        "重複過帳的資料處理規則｜系統在進行簽核流程時，如果沒有清除先前已存在的行項目紀錄，"
        "會導致原本已經標記為刪除的舊行項目，也會被重新計算並計入本次的過帳總額中。",
    ]
    keep = [
        "獲取系統日期的規則說明｜如果沒有指定特定的時間區域，則會使用目前登入使用者的時間區域來判斷今天的日期。",
        "廠別選擇格式說明｜如果沒有勾選任何廠別，系統會將該欄位顯示為空白的字串。",
        "容忍值依系統參數決定｜如果沒有在系統參數中設定或設定的值不符合規定，則會使用預設的數值。",
    ]
    missed = [t for t in known_bad if not flagged(t)]
    noisy = [t for t in keep if flagged(t)]
    check(not missed, f"{len(known_bad)} 則已知的「只剩災害」都標得到",
          "漏標：" + "／".join(t[:26] for t in missed))
    check(not noisy, f"{len(keep)} 則正常的預設值規則沒有被標",
          "誤標：" + "／".join(t[:26] for t in noisy))

    # `8af99eb8` 這個形狀（承接詞開頭、完全沒有條件句）**標不到，而且這是已知的**。
    # 寫成斷言是為了讓它別再被當成「還沒做完」——原本的設計就是掃承接詞，
    # 掃了全語料只命中它自己一則，所以那條路放棄了。
    anaphor_case = ("顯示期間的絕對區間必須呈現｜這樣做是為了讓使用者可以清楚知道"
                    "系統計算出來的「上一個月」具體指的是哪個時間範圍。")
    check(not flagged(anaphor_case),
          "承接詞開頭那一種**刻意不由這條旗標負責**（掃承接詞全語料只命中它自己）")


def test_abstention_at_end() -> None:
    """模型把「無」寫在最後一行的棄權，必須判成棄權——**且不能誤傷正常擷取物**。

    釘的是 2026-08-26 第五批的實跑：`PlantFormEnhancementFilter.cs` 有一段，
    模型先解釋「這是開發實作取捨」再補一句「輸出：無」——判斷完全正確，
    而既有的棄權偵測只認**開頭**，於是整段解釋變成說明、章節位置變成標題，
    產出「曾因此而失效｜規則判斷：…輸出：無」並一路通過所有檢查進了語料。

    棄權被當成內容比擷取失敗嚴重：失敗會計數、會被看到，這個長得跟正常擷取物一樣。
    """
    abstain = [
        "規則判斷：這段註解在講程式碼如何處理表單資料的技術細節。\n輸出：無",
        "這一段是實作取捨，沒有使用者需要知道的規則。\n無",
        "本段描述的是開發過程。\n判定：無",
    ]
    keep = [
        "標題：出貨單可重複確認\n說明：出貨單確認過一次之後仍然可以再確認，系統會擋掉重複過帳。",
        "停用的品號不會出現在選單裡\n系統只列出啟用中的品號，停用之後既有單據不受影響。",
        # 收尾剛好是「無」字結尾的正常句子，不可以被當成棄權
        "標題：查無資料時的顯示\n說明：查詢條件找不到任何資料時，清單會顯示一列「查無資料」而不是空白。",
    ]
    def verdict(body: str) -> str:
        """棄權／擷取到／擷取失敗——**三種，不是兩種**。

        「擷取失敗」也不算通過：棄權與失敗在統計上是不同的東西，
        失敗數正是用來決定哪些檔案值得重跑的訊號（見 `parse` 的說明）。
        """
        try:
            return "棄權" if GEN.parse(body, min_text=20, fallback_title="章節位置") is None else "擷取到"
        except RuntimeError:
            return "算成失敗"

    leaked = [(b, v) for b in abstain if (v := verdict(b)) != "棄權"]
    lost = [(b, v) for b in keep if (v := verdict(b)) != "擷取到"]
    check(not leaked, f"{len(abstain)} 種「收尾寫無」的棄權都判成棄權",
          "、".join(f"{b[:20]}→{v}" for b, v in leaked))
    check(not lost, f"{len(keep)} 則正常擷取物沒有被誤判成棄權",
          "、".join(f"{b[:20]}→{v}" for b, v in lost))


def test_rejected_matches_manifest(mans) -> None:
    """退回名單與 manifest 的核可狀態必須一致——**兩個方向都要**。

    為什麼要有這一條：`rejected_slugs()` 是在解析人手維護的 markdown 表格，
    到 2026-08-26 為止已經錯了**四個變體**（只有〈代號〉欄的、理由欄出現「退回」
    二字的、表頭判斷每列重跑的、表頭寫「退回」但列是「保留」的）。
    每次都是靠人工對帳「共 N 則」才發現，而每一次的修法都是再補一個特例。

    這一條不修解析器，**釘不變式**：解析器判錯、或人只改了一邊，都會紅。
    第四個變體就是這樣抓到的（`code-fin-arinvoiceservice-e3cf8c7e`
    在退回名單上卻是 `approved:true`——它其實是「保留但改了文字」）。
    """
    WL = load("qa_extract_worklist", ROOT / "scripts" / "qa-extract-worklist.py")
    rejected = WL.rejected_slugs()
    approved_but_rejected = []
    unknown = set(rejected)
    for _, d in mans:
        for e in d.get("entries", []):
            slug = e.get("slug", "")
            unknown.discard(slug)
            if slug in rejected and e.get("approved"):
                approved_but_rejected.append(slug)

    check(not approved_but_rejected,
          f"退回名單 {len(rejected)} 則，manifest 裡沒有一則還是 approved:true",
          "不一致：" + "、".join(approved_but_rejected[:5]))
    check(not unknown,
          "退回名單上的代號都找得到對應的 entry",
          "找不到：" + "、".join(sorted(unknown)[:5]))


def test_redline_body() -> None:
    """段落層紅線：檔名乾淨、但本文在講授權／金鑰的那一段，必須攔得下來。

    這一條釘的是 2026-08-26 查到的洞：`CODE_REDLINE` 比對檔名，
    而 `Program.cs:665` 是整段啟動授權驗證（含 Internal License 的 clientCode），
    檔名一個字都不命中。**過濾的維度與內容所在的維度不同就一定漏。**

    同時釘反向：資料權限、單號、稽核這些正常規則**不可以**被連坐——
    紅線寬到擋掉「誰看得到哪些單」的話，代價就不是少一則而已了。
    """
    must_block = [
        "啟動時授權驗證：任何環境都需要有效的 license.json，驗證失敗則拒絕啟動。",
        "DataProtection 金鑰持久化：預設存於容器內臨時路徑，容器重建即遺失。",
        "雙因素驗證啟用後，備援碼只顯示一次。",
        "環境指紋比對不過就拒絕啟動。",
    ]
    must_pass = [
        "資料權限：使用者只看得到自己建單角色所屬組織路徑底下的單據。",
        "單號規則以單別加廠別為鍵，不含年月時不會跨年重置。",
        "稽核紀錄保留「某欄位有異動」的信號，值以遮罩存放。",
        "簽核跳過的步驟仍會留一筆紀錄，讓後續看得出來為什麼沒人簽。",
        "推播權杖失效時整批略過，不影響其餘收件人。",
    ]
    blocked = [t for t in must_block if not GEN.CODE_REDLINE_BODY.search(t)]
    leaked = [t for t in must_pass if GEN.CODE_REDLINE_BODY.search(t)]
    check(not blocked, f"{len(must_block)} 段講機密機制的本文都被紅線攔下",
          "漏放：" + "／".join(t[:24] for t in blocked))
    check(not leaked, f"{len(must_pass)} 段正常規則沒有被紅線連坐",
          "誤擋：" + "／".join(t[:24] for t in leaked))

    # 真的跑一次切段：regex 對得上不代表 main() 有用它。
    src = ROOT / "Program.cs"
    if src.is_file():
        secs = GEN.split_sections(src.read_text(encoding="utf-8"), "Program.cs")
        hit = [x for x in secs if GEN.CODE_REDLINE_BODY.search(x.body)]
        # 用段落標題認那一段，不寫死行號：原本是 `x.index == 665`（2026-08-26 當時的位置），
        # Program.cs 一改，段落移到 707，紅線明明攔得下、這條卻轉紅，看起來像防護失效。
        target = [x for x in secs if "啟動時授權驗證" in x.heading]
        check(len(target) == 1 and target[0] in hit,
              "Program.cs 的啟動授權驗證那一段實際切出來也命中紅線",
              (f"標題含「啟動時授權驗證」的段落有 {len(target)} 段（應為 1；標題被改名就要更新這裡）"
               if len(target) != 1 else
               f"該段在第 {target[0].index} 行；紅線命中 {sorted(x.index for x in hit)}"))


def test_scrub(mans) -> None:
    """2026-08-25 使用者逐則審時盤出三種殘留，`scrub()` 是那次補的確定性後處理。

    這一組真正在守的是**它不要清過頭**：全語料有 5 則的標題就等於章節位置的最後一節，
    那是 `parse()` 第四種形狀刻意的行為（模型沒給標題時用章節位置補一個標籤）。
    把它當殘留清掉，那幾則會變成沒有標題——**把設計當成缺陷修**。
    """
    print("\n殘留清理（清該清的，不清設計如此的）")

    cases = [
        (("規則說明", "採購流程完成就直接過帳。無"),
         ("規則說明", "採購流程完成就直接過帳。"), "尾端棄權字元「無」"),
        (("檢驗時機", "說明：當製程要求抽樣檢驗時，系統會依此執行。"),
         ("檢驗時機", "當製程要求抽樣檢驗時，系統會依此執行。"), "說明開頭殘留「說明：」"),
        (("上限＝訂購量", "以訂購量為上限。說明：計算基礎與訂單行同單位。"),
         ("上限＝訂購量", "以訂購量為上限。計算基礎與訂單行同單位。"), "句末終止符後的「說明：」"),
        (("系統共用 › 既有呼叫端不需調整", "若未提供時間服務則以 UTC 替代。"),
         ("既有呼叫端不需調整", "若未提供時間服務則以 UTC 替代。"), "標題抄了整條章節位置"),
        (("版本標籤說明：已上線的功能範圍", "版本編號涵蓋 v1.0 到 v1.2。"),
         ("版本標籤說明：已上線的功能範圍", "版本編號涵蓋 v1.0 到 v1.2。"), "**標題**裡的「說明：」不動"),
        (("全額認列", "說明：規則說明：比例基礎是已上架數量。"),
         ("全額認列", "比例基礎是已上架數量。"), "疊兩層的標籤（清到不再變為止）"),
        (("權限說明", "可查看的範圍為無"),
         ("權限說明", "可查看的範圍為無"), "句末終止符**之前**的「無」不動"),
    ]
    for src, exp, label in cases:
        got = GEN.scrub(*src)
        check(got == exp, label, "" if got == exp else f"得到 {got}")

    # 語料層：跑完之後應該一則都不剩，而 fallback_title 那 5 則必須原封不動。
    left, fb = [], []
    for _p, d in mans:
        for e in d["entries"]:
            ti, tx = e["text"].split("｜", 1)
            if GEN.scrub(ti, tx) != (ti, tx):
                left.append(e["slug"])
            if ti == e["headingPath"].split(" › ")[-1][:24]:
                fb.append(e["slug"])
    check(not left, "全語料已經沒有殘留", "、".join(left[:3]) if left else "")
    check(bool(fb), "標題＝章節位置最後一節的那幾則還在（沒有被清過頭）", f"{len(fb)} 則")


def test_meta_output(mans) -> None:
    """後設回覆閘門：擋模型對讀者說話，不擋規則句裡恰好出現的字。

    2026-10-01 放寬前是單一關鍵字比對，誤擋過兩則正常規則（下面 ok 的前兩條），
    而已核可語料裡也有兩則會命中（後兩條）——閘門只在產出當下跑，所以沒人發現。
    """
    print("\n後設回覆閘門（擋對讀者說話，不擋規則裡的字）")
    meta = [
        "無法處理此類內容，因為原文中沒有明確描述使用者會遇到的規則或限制。",
        "無法在沒有程式碼和註解的情況下進行轉換。請提供需要處理的原文。",
        "作為一個AI語言模型，我無法判斷。",
        "沒有收到任何原文。",
    ]
    ok = [
        "系統會將標題和對應的值組合在一起作為一個單一的資料項目呈現。",
        "收付款總金額與核銷合計不相等時，則無法完成分錄。",
        "目的是為了回答使用者「為什麼這個月沒有收到報表」的問題。",
        "如果是由排程或大型語言模型呼叫系統，則以當天日期作為基準日。",
        "若科目未設定，系統無法處理這張單據的過帳。",
    ]
    miss = [t for t in meta if not GEN.META_OUTPUT.search(t)]
    check(not miss, f"{len(meta)} 種已知後設回覆都擋下", "漏放：" + "、".join(m[:20] for m in miss))
    bad = [t for t in ok if GEN.META_OUTPUT.search(t)]
    check(not bad, f"{len(ok)} 則規則句沒有被誤擋", "誤擋：" + "、".join(b[:20] for b in bad))
    hit = [e["slug"] for _p, d in mans for e in d["entries"]
           if e["approved"] and GEN.META_OUTPUT.search(e["text"].split("｜", 1)[1])]
    check(not hit, "已核可語料沒有一則會被閘門擋下", "、".join(hit[:3]))


def main() -> int:
    mans = manifests()
    print(f"擷取物工具鏈自我測試（{len(mans)} 份 manifest，不呼叫模型）")
    test_manifests(mans)
    test_reconcile(mans)
    test_stale_hash(mans)
    test_rejected_parse()
    test_harm_without_rule_flag()
    test_abstention_at_end()
    test_rejected_matches_manifest(mans)
    test_redline_body()
    test_scrub(mans)
    test_meta_output(mans)
    if FAILED:
        print(f"\n✗ {len(FAILED)} 項不符預期：")
        for f in FAILED:
            print(f"    {f}")
        return 1
    print("\n✓ 全綠")
    return 0


if __name__ == "__main__":
    sys.exit(main())
