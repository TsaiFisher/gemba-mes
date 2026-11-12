#!/usr/bin/env python3
"""擷取物對帳：把「來源檔變了」的代價從整份重審縮到真的改到的那幾段（規格 §10.11.3）。

**它解的問題**：staleness 是**整份檔案**的 SHA256 比對，所以規格改一行就會把整份
凍結——`mfg-domain.json` 155 段全部退出檢索，其中 96 段是人審過的。而實際受影響的
段落，實測是 3 段。

**它不繞過核可**（定案 #45／§10.11.4）：沿用 `approved: true` 的條件是
**該段來源原文 byte 完全相同**。文字一字未改，人核可過的就是這段文字。
真的改過的段落一律 `approved: false`，重新產出的內容由 `qa-extract-generate.py`
負責——本腳本不自己呼叫模型、不自己拼提示詞，那份契約只能有一份。

**擷取當時的原文從 git 找回來**：manifest 記著 `sourceHash`，逐一比對該檔的歷史版本
即可還原。找不到就退回整份重跑，並明講是退回，不會假裝對帳過了。

用法：
    scripts/qa-extract-reconcile.py                       # 盤點全部（不呼叫模型）
    scripts/qa-extract-reconcile.py --apply <manifest>    # 重跑受影響段落並合併
    scripts/qa-extract-reconcile.py --apply <manifest> --url http://127.0.0.1:11435
    scripts/qa-extract-reconcile.py --prune [--write]     # 不呼叫模型：刪掉過期段落、解凍未改的段落
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import urllib.request
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXTRACTS = ROOT / ".claude" / "qa-extracts"
GENERATOR = ROOT / "scripts" / "qa-extract-generate.py"


def load_generator():
    """把產生器當模組載入，只為了共用 `split_sections`。

    **切段規則必須與產生器同一份實作**：兩份實作會漂開，而漂開的症狀是
    「對帳說沒變、重跑出來卻不一樣」——那比不對帳更糟。
    """
    spec = importlib.util.spec_from_file_location("qa_extract_generate", GENERATOR)
    mod = importlib.util.module_from_spec(spec)
    sys.modules["qa_extract_generate"] = mod    # dataclass 需要模組在 sys.modules 裡
    spec.loader.exec_module(mod)
    return mod


GEN = load_generator()


def sha(b: bytes) -> str:
    """段落文字用的雜湊（`sourceSectionHash`）。**不要拿去判來源檔過期**——
    那一條要跟 `ExtractManifestImporter` 逐位元組對齊（含去 BOM），走 `sha_source`。"""
    return hashlib.sha256(b).hexdigest()


# 來源檔／git blob 的雜湊：**唯一定義在產生器**，見 `sha256_of_source_bytes` 的說明。
sha_source = GEN.sha256_of_source_bytes


def source_at_hash(src: str, want: str) -> str | None:
    """從 git 還原「擷取當時」的原文。找不到回 None。

    兩道搜尋，順序有意義：

    1. **可達歷史**（`rev-list --all`）——絕大多數情況命中，一次 `git show` 就結束。
    2. **不可達物件**——`rev-list` 只走得到 refs 指得到的提交，但 manifest 常常是拿
       **工作區裡還沒提交的那一版**產出來的：改規格 → 跑擷取 → 又改了幾行才提交。
       那一版的 blob 仍然躺在物件庫裡（`git add` 過就會有，即使那次 commit 沒帶上它），
       只是沒有任何 ref 指得到。

    第二道不是理論上的可能性——`mfg-domain.json` 就是這樣：只靠第一道會判成
    「原文不在 git 歷史裡，只能整份重跑」，而整份重跑會讓 **110 則已審核可歸零**。
    實際比對之後 155 段裡只有 1 段真的變了。把「找不到」當成「不存在」，
    代價是重審 154 段人已經讀過的內容。

    第二道會掃全部物件，所以先用**檔案大小**篩（±25%）再算雜湊——
    不篩的話兩萬多個物件每個都要解壓＋SHA256。
    """
    def blob_matches(data: bytes) -> str | None:
        return data.decode("utf-8") if sha_source(data) == want else None

    revs = subprocess.run(
        ["git", "-C", str(ROOT), "rev-list", "--all", "--", src],
        capture_output=True, text=True,
    ).stdout.split()
    for rev in revs:
        blob = subprocess.run(
            ["git", "-C", str(ROOT), "show", f"{rev}:{src}"], capture_output=True
        ).stdout
        hit = blob_matches(blob)
        if hit is not None:
            return hit

    return _unreachable_blob_at_hash(src, want)


def _unreachable_blob_at_hash(src: str, want: str) -> str | None:
    """在**所有** git 物件（含不可達的）裡找內容雜湊為 `want` 的 blob。

    以 `src` 現檔的大小 ±25% 當篩選窗。來源檔在兩次提交之間很少改動超過四分之一；
    真的改超過了，這一道落空就退回原本的「整份重跑」，不會比沒有它更糟。
    """
    current = ROOT / src
    if not current.exists():
        return None
    size = current.stat().st_size
    lo, hi = size * 0.75, size * 1.25

    listing = subprocess.run(
        ["git", "-C", str(ROOT), "cat-file", "--batch-all-objects",
         "--batch-check=%(objecttype) %(objectname) %(objectsize)"],
        capture_output=True, text=True,
    ).stdout

    for line in listing.splitlines():
        parts = line.split()
        if len(parts) != 3 or parts[0] != "blob":
            continue
        if not (lo < int(parts[2]) < hi):
            continue
        data = subprocess.run(
            ["git", "-C", str(ROOT), "cat-file", "blob", parts[1]], capture_output=True
        ).stdout
        if sha_source(data) == want:
            return data.decode("utf-8")
    return None


# 切段一律要帶來源路徑：產生器依副檔名分派（§10.11.13），
# `.cs` 少了它會走 markdown 那條路回 0 段——對帳會把整份判成「每一段都不見了」。
def sections_of(text: str, src: str):
    return GEN.split_sections(text, src)


def stale_text(s) -> str:
    """判「這段變了沒有」該比的文字。

    `.md` 是 body（原文本身）。`.cs` 是 fingerprint——body 只有註解＋宣告行，
    改了程式碼沒動註解在 body 上看不出來，而擷取物講的是行為。
    """
    return s.fingerprint or s.body


def slug_map(sections, src: str) -> dict[str, object]:
    """slug → Section，重放產生器的 `slug_of`（含碰撞加尾號那條路）。

    **識別要用 slug，不能用 headingPath**：`.cs` 的 headingPath 取自註解的第一句話，
    改一個字它就變了，而 slug 綁「檔案＋宣告」——產生器刻意這樣設計，就是為了
    「改一句註解」不會讓已核可的那則失效（見 Section.key 的註解）。
    對帳若拿 headingPath 比，同一件事會被判成「刪掉一則、多出一則」：已審的那則被丟掉，
    報表也看不出它其實只是被改寫。實測 MaterialStagingController 改一句註解即重現。
    """
    stem = GEN.slug_stem_for(src)
    taken: set[str] = set()
    out: dict[str, object] = {}
    for s in sections:
        out[GEN.slug_of(stem, s.key or s.heading, taken, quiet=True)] = s
    return out


class Triage:
    def __init__(self, manifest_path: Path):
        self.path = manifest_path
        self.data = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.src = self.data["sourcePath"]
        self.recorded = self.data["sourceHash"]
        src_file = ROOT / self.src
        self.missing_source = not src_file.is_file()
        self.current = "" if self.missing_source else sha_source(src_file.read_bytes())
        self.stale = self.missing_source or self.current != self.recorded

        self.old_text = None if self.missing_source else source_at_hash(self.src, self.recorded)
        self.new_text = "" if self.missing_source else src_file.read_text(encoding="utf-8")

        self.unchanged: list[dict] = []
        self.changed: list[dict] = []
        self.deleted: list[dict] = []
        self.added: list[str] = []
        if self.stale and self.old_text is not None:
            self._classify()

    def _classify(self):
        old_secs = sections_of(self.old_text, self.src)
        new_secs = sections_of(self.new_text, self.src)
        old_by, new_by = slug_map(old_secs, self.src), slug_map(new_secs, self.src)
        old_head = {s.heading: s for s in old_secs}
        self.new_head = {s.heading: s for s in new_secs}

        # entry → 它在現檔對應的那一段。slug 對不上就退回標題比對：
        # 產生器的 `taken` 只累積「真的產出擷取物」的段落，所以碰撞加尾號的 slug
        # 不保證重放得回來（實測 634 則裡有 3 則，全都是兩段共用同一行宣告）。
        self.new_of: dict[str, object] = {}
        matched: set[int] = set()
        for e in self.data["entries"]:
            slug, h = e["slug"], e["headingPath"]
            ns = new_by.get(slug) or self.new_head.get(h)
            os_ = old_by.get(slug) or old_head.get(h)
            if ns is None:
                self.deleted.append(e)
                continue
            self.new_of[slug] = ns
            matched.add(id(ns))
            if os_ is not None and stale_text(os_) == stale_text(ns):
                self.unchanged.append(e)
            else:
                self.changed.append(e)

        # 新增段落。章節位置產不出來／重複的那些不算——產生器本來就不送它們，
        # 列進來只會讓 --apply 拿著一個產生器拒收的段號去跑，然後整支中止。
        old_slugs = set(old_by)
        self.added = [s.heading for s in new_secs
                      if id(s) not in matched
                      and GEN.CS_UNUSABLE not in s.heading
                      and GEN.slug_of(GEN.slug_stem_for(self.src), s.key or s.heading,
                                      set(), quiet=True) not in old_slugs]

    # ── 報表 ────────────────────────────────────────────────────────────
    def report(self) -> None:
        name = self.path.name
        n = len(self.data["entries"])
        approved = sum(1 for e in self.data["entries"] if e["approved"])

        if not self.stale:
            print(f"  {name:42} {n:3} 段（已審 {approved:3}）　✓ 未過期")
            return
        if self.missing_source:
            print(f"  {name:42} ✗ 來源檔不存在：{self.src}")
            return
        if self.old_text is None:
            print(f"  {name:42} ⚠ 過期，且**擷取當時的原文不在 git 歷史裡**"
                  f"（記 {self.recorded[:9]}）→ 無法逐段對帳，只能整份重跑（已審 {approved} 段會歸零）")
            return

        ca = sum(1 for e in self.changed if e["approved"])
        da = sum(1 for e in self.deleted if e["approved"])
        ua = sum(1 for e in self.unchanged if e["approved"])
        print(f"  {name:42} {n:3} 段（已審 {approved:3}）　⚠ 過期")
        print(f"{'':44}未變 {len(self.unchanged):3}（已審 {ua:3} → 可沿用）"
              f"　已變 {len(self.changed):3}（已審 {ca}）"
              f"　刪除 {len(self.deleted):3}（已審 {da}）"
              f"　新增段落 {len(self.added):3}")

    def sections_to_run(self) -> list[int]:
        secs = [self.new_of[e["slug"]] for e in self.changed if e["slug"] in self.new_of]
        secs += [self.new_head[h] for h in self.added if h in self.new_head]
        return sorted({s.index for s in secs})

    def new_heading(self, e: dict) -> str:
        """entry 在現檔的標題。改過註解的那則，這個值與 manifest 裡的不同。"""
        s = self.new_of.get(e["slug"])
        return s.heading if s is not None else e["headingPath"]


def cmd_plan(paths: list[Path]) -> int:
    print("擷取物對帳盤點（不呼叫模型）\n")
    tri = [Triage(p) for p in paths]
    print(f"  {'manifest':42} 狀態")
    for t in tri:
        t.report()

    stale = [t for t in tri if t.stale and t.old_text is not None]
    if not stale:
        print("\n沒有可對帳的過期 manifest。")
        return 0

    frozen = sum(sum(1 for e in t.unchanged if e["approved"]) for t in stale)
    runs = sum(len(t.sections_to_run()) for t in stale)
    print(f"\n合計：重跑 {runs} 段即可解凍 {frozen} 段已審擷取物。")
    print("下一步（一份一份做，每份重跑完都要看審核頁再送 PR）：")
    for t in stale:
        s = t.sections_to_run()
        if s:
            print(f"  scripts/qa-extract-reconcile.py --apply {t.path.relative_to(ROOT)}"
                  f"   # 重跑 {len(s)} 段：{','.join(map(str, s))}")
        else:
            print(f"  scripts/qa-extract-reconcile.py --apply {t.path.relative_to(ROOT)}"
                  f"   # 0 段要重跑，只需更新 sourceHash")
    return 0


def cmd_prune(paths: list[Path], write: bool) -> int:
    """不呼叫模型，把過期的部分直接刪掉（2026-10-16 使用者裁決「過期用不到的可刪」）。

    - 來源檔不存在、或擷取當時的原文不在 git 歷史裡（無法逐段對帳）→ **整份刪除**。
    - 其餘過期 manifest → 只留**原文一字未改**的段落（核可狀態照舊），已變／已刪的段落移除，
      `sourceHash` 更新成現檔。新長出來的段落不補（沒有模型就沒有擷取物），要補走 `--apply`。

    與 `--apply` 的差別只在「已變的段落」：apply 重產並送審，prune 直接丟掉。
    丟掉的那些本來就因整份過期而不在檢索裡，所以 prune 不會讓 Q&A 少答任何現在答得出的題目，
    只會把「被一兩段改動拖累、整份凍結」的已核可段落解凍。
    """
    print(("擷取物剪除（寫回）" if write else "擷取物剪除盤點（不寫檔；加 --write 才動手）") + "\n")
    removed_files = kept = dropped = 0
    for p in paths:
        t = Triage(p)
        if not t.stale:
            continue
        rel = p.relative_to(ROOT)
        if t.missing_source or t.old_text is None:
            why = "來源檔不存在" if t.missing_source else "擷取當時的原文不在 git 歷史裡"
            n = len(t.data["entries"])
            print(f"  ✗ 整份刪除 {rel}（{why}，{n} 段）")
            removed_files += 1
            dropped += n
            if write:
                p.unlink()
            continue

        new_sections = sections_of(t.new_text, t.src)
        order = {s.heading: s.index for s in new_sections}
        finger = {s.heading: stale_text(s) for s in new_sections}
        entries = []
        for e in t.unchanged:
            e = dict(e)
            e["sourceSectionHash"] = sha(finger[t.new_heading(e)].encode("utf-8"))
            entries.append(e)
        entries.sort(key=lambda e: order.get(e["headingPath"], 10**6))
        gone = len(t.changed) + len(t.deleted)
        kept += len(entries)
        dropped += gone
        if not entries:
            # 匯入端拒收空 manifest（見 cmd_apply 同一段）
            print(f"  ✗ 整份刪除 {rel}（沒有一段原文未改，{gone} 段）")
            removed_files += 1
            if write:
                p.unlink()
            continue
        print(f"  ✂ {rel}：保留 {len(entries)} 段（已審 {sum(1 for e in entries if e['approved'])}）、"
              f"刪 {gone} 段" + (f"、新段落 {len(t.added)} 未補" if t.added else ""))
        if write:
            t.data["entries"] = entries
            t.data["sourceHash"] = t.current
            p.write_text(json.dumps(t.data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"\n合計：整份刪除 {removed_files} 份；保留 {kept} 段；刪除 {dropped} 段。")
    if not write:
        print("確認無誤後加 --write 寫回；寫回後重建檢索並跑 ManualIndexIntegrationTests／ExtractManifestImporterTests。")
    return 0


def endpoint_reachable(url: str) -> bool:
    """推論端點通不通。只用來分辨「被閘門擋掉」與「連不上」，不驗模型在不在
    ——模型不在的話產生器自己的 preflight 會擋，而那條路會留下產物以外的訊息。"""
    try:
        with urllib.request.urlopen(url.rstrip("/") + "/api/tags", timeout=4) as r:
            return r.status == 200
    except Exception:
        return False


DROP_FAILED = False


def cmd_apply(path: Path, model: str, url: str, timeout: int) -> int:
    t = Triage(path)
    if not t.stale:
        print(f"{path.name} 未過期，不需要對帳。")
        return 0
    if t.missing_source:
        sys.exit(f"來源檔不存在：{t.src}")
    if t.old_text is None:
        sys.exit("擷取當時的原文不在 git 歷史裡，無法逐段對帳。請整份重跑 qa-extract-generate.py。")

    todo = t.sections_to_run()
    print(f"{path.name}：未變 {len(t.unchanged)}、已變 {len(t.changed)}、"
          f"刪除 {len(t.deleted)}、新增段落 {len(t.added)} → 要重跑 {len(todo)} 段")

    patch: dict[str, dict] = {}
    if todo:
        out = Path.home() / ".cache" / "qa-extract-reconcile" / path.stem
        out.mkdir(parents=True, exist_ok=True)
        patch_file = out / path.name

        # 🚨 **先刪掉上一輪的產物**。這個目錄是固定路徑、跨執行沿用的，而下面那道
        #    `is_file()` 守門的全部意義是「產生器這一次有沒有產出東西」。留著舊檔，
        #    它就永遠成立——於是：
        #      · 這一次一段都沒產出（全被閘門擋掉／模型全棄權）→ 讀到上一輪的產物，
        #        當成本次結果合併進 manifest。實際踩到：mfg-domain 併進一則 8 月的舊擷取物，
        #        內容與另一則已核可的一字不差，slug 因此重複。
        #      · **更糟的形狀**：端點根本連不上、產生器一段都沒跑 → 照樣讀到舊產物 →
        #        寫回 manifest 並**把 sourceHash 更新成現檔**，整份就此被標記為「新鮮」。
        #        那正是這支腳本最想避免的半套更新，而且沒有任何訊號。
        #    刪掉之後，「檔案不存在」重新等於「這一次沒產出」，守門才說得上是守門。
        patch_file.unlink(missing_ok=True)

        # 🚨 產生器收到被閘門擋掉的段號（紅線／太短／程式碼訊號）會**整份中止**、
        #    一段都不跑——它要的是「補跑的人別以為少跑的幾段也跑了」。但對帳這邊
        #    沒產物就會走下面「全被閘門擋掉」那條路，把其餘**根本沒送模型**的段落
        #    一起移除。實際踩到：Program.cs 第 120 段命中紅線，同一批另外 5 段沒跑，
        #    3 則被刪、sourceHash 照樣更新。所以讀出被擋的段號，扣掉後重跑。
        gated: set[int] = set()
        rc = 0
        while True:
            run = [i for i in todo if i not in gated]
            if not run:
                break
            cmd = [sys.executable, str(GENERATOR), t.src, "--sections", ",".join(map(str, run)),
                   "--out", str(out), "--model", model, "--url", url, "--timeout", str(timeout)]
            print("→ " + " ".join(cmd))
            proc = subprocess.run(cmd, cwd=ROOT, stderr=subprocess.PIPE, text=True)
            sys.stderr.write(proc.stderr)
            rc = proc.returncode
            m = re.search(r"這幾段不在待送模型的清單裡：\[([\d, ]+)\]", proc.stderr)
            if not m:
                break
            newly = {int(x) for x in m.group(1).split(",")} - gated
            if not newly:
                break
            gated |= newly
            print(f"↻ 第 {sorted(newly)} 段被產生器的閘門擋掉，扣掉後重跑其餘 {len(todo) - len(gated)} 段。")
        if not patch_file.is_file():
            # 一份產物都沒有，兩種成因、處置相反：
            #   · 端點連不上／參數錯 → 這一次什麼都沒跑，**絕不能動 manifest**。
            #   · 要重跑的段落**全部**被產生器的閘門擋掉（程式碼訊號過高、太短、棄權）
            #     → 那就是「這幾段本來就產不出規則」，與逐段 missing 的處置相同：
            #       移除該則、更新 sourceHash，讓其餘沒變的段落解凍。
            #
            # 這兩者靠猜分不出來，但可以**直接問端點**。實際擋住的情況：
            # `mfg-domain` 唯一變動的是「11. 下一步建議 › Phase B」這種路線圖章節，
            # 開發導向章節排除規則本來就該擋掉它；分不出來的代價是整份 155 段
            # （含 110 則已核可）永遠留在檢索之外。
            if DROP_FAILED and endpoint_reachable(url):
                print(f"\n⚠ 產生器一段都沒產出，但端點 {url} 是通的"
                      f"——判定為「這 {len(todo)} 段全部被閘門擋掉」，"
                      f"依 --drop-failed 移除對應的則並更新 sourceHash。")
            else:
                hint = "" if endpoint_reachable(url) else f"（端點 {url} 不通）"
                sys.exit(f"產生器沒有產出 manifest{hint}，**本檔未動**。"
                         "\n端點正常而確認是被閘門擋掉，才加 --drop-failed。")
        else:
            produced = json.loads(patch_file.read_text(encoding="utf-8"))
            patch = {e["headingPath"]: e for e in produced["entries"]}

        # 🚨 **產生器非零退出不等於整支炸掉**：它把「某一段解析不出合格擷取物」
        #    （模型棄權、說明太短、被程式碼密度擋掉）也算進失敗數。5 段成功 1 段沒產物
        #    照樣是 rc≠0。
        #
        #    但兩者的處置完全不同，而**這支腳本分不出來**——產生器的失敗桶同時裝著
        #    「這段本來就沒有規則可寫」與「這次連線逾時」。前者該移除該則，
        #    後者移除就是拿一次網路抖動刪掉一條規則。
        #
        #    所以預設中止，把判斷交給人；看過原因之後要放行就加 --drop-failed。
        # 用**現檔**的標題去比對重跑結果：改過註解的那則，manifest 裡記的是舊標題，
        # 拿舊標題比會一律落空，然後把「重跑成功」誤報成「這幾段沒有產物」而中止。
        missing = [h for h in ([t.new_heading(e) for e in t.changed] + t.added)
                   if h not in patch]
        if missing and not DROP_FAILED:
            print("\n✗ 這幾段重跑後沒有產物：")
            for h in missing:
                print(f"    {h[:70]}")
            sys.exit(
                "\n**manifest 未動、sourceHash 沒有更新**——半套更新會讓沒重跑到的段落"
                "被當成新鮮的。\n"
                "先看上面產生器印的原因：\n"
                "  · 「說明只有 NN 字，太短」「棄權」「程式碼訊號」→ 這段本來就產不出規則，"
                "加 --drop-failed 移除該則後重跑本指令\n"
                "  · 逾時／連不上 → 端點的問題，修好再跑一次，不要用 --drop-failed 掩蓋"
            )
        if rc != 0 and not missing:
            print("⚠ 產生器回非零，但要重跑的段落全部有產物（失敗數來自其他段落），繼續。")

    # 🚨 有段落改了、卻沒產出對應的擷取物（模型棄權或被閘門擋掉）——那一則要**移除**，
    #    不能留著舊文字：舊文字是根據舊原文寫的，留著再把 sourceHash 更新成現檔，
    #    等於宣告「這段是照現在的原文寫的」，而它不是。
    dropped_silent = [e for e in t.changed if t.new_heading(e) not in patch]

    new_sections = sections_of(t.new_text, t.src)
    order = {s.heading: s.index for s in new_sections}
    # slug 的雜湊輸入是 `s.key`（程式碼來源是「檔案::宣告行」），不是 headingPath，
    # 所以重算 slug 時要拿得到它——只能在這裡順手備起來。
    section_key = {s.heading: (s.key or s.heading) for s in new_sections}
    slug_stem = GEN.slug_stem_for(t.src)
    # 逐段雜湊比的是 staleness 用的那份文字（`.cs` 是 fingerprint），
    # 與 _classify 同一把尺；兩邊用不同文字會讓「這次說未變、下次說變了」。
    finger = {s.heading: stale_text(s) for s in new_sections}

    entries = []
    for e in t.unchanged:
        e = dict(e)
        # 逐段雜湊：下一次對帳就不必再去 git 挖原文（那條路只在原文曾被 commit 時走得通）。
        e["sourceSectionHash"] = sha(finger[t.new_heading(e)].encode("utf-8"))
        entries.append(e)
    for h, e in patch.items():
        e = dict(e)
        e["approved"] = False          # 產生器本來就寫 false，這裡再釘一次：它是唯一防線
        e["sourceSectionHash"] = sha(finger[h].encode("utf-8")) if h in finger else ""
        entries.append(e)

    entries.sort(key=lambda e: order.get(e["headingPath"], 10**6))

    # 🚨 **合併之後才輪得到唯一性檢查。**
    #
    # 產生器在自己那一輪用 `slug_of` 的 `taken` 集合保證 slug 唯一，但對帳只重跑
    # **一部分**段落——那個集合裡沒有沿用下來的那些。於是重產的段落可以拿到一個
    # 沿用段落已經在用的 slug，而兩邊都沒有人會發現。
    #
    # 這不是理論上的雜湊碰撞，是 **key 相同**：程式碼來源的 slug 雜湊輸入是
    # 「檔案::宣告行」，而同一支 controller 裡出現兩次一模一樣的宣告行是常態
    # （`code-fin-appaymentcontroller` 的 Confirm 與 Void 各有一行
    # `return Json(new { success = false, message = _localizer["ConcurrencyConflict"].Value });`）。
    #
    # 代價不是「少一則」：匯入器的 `Validate` 要求同一份內 slug 唯一，撞到就擲例外，
    # 而它是**一份壞掉就整批中止**——整個擷取物語料會一起從 Q&A 消失，
    # 而症狀只是「規則類問題突然都答不出來」。
    #
    # 沿用的段落優先保留原 slug（`DECISIONS.md` 與既有核可都以 slug 對回去），
    # 重產的那幾則才重算。
    taken: set[str] = set()
    for e in entries:
        if e["slug"] not in taken:
            taken.add(e["slug"])
            continue
        key = section_key.get(e["headingPath"]) or e["headingPath"]
        e["slug"] = GEN.slug_of(slug_stem, key, taken)

    t.data["entries"] = entries
    t.data["sourceHash"] = t.current
    if not entries:
        # 匯入端（ExtractManifestImporter.Validate）拒收空 manifest：「要停用整份請刪檔」。
        # 寫一份空的出去，單元測試與自檢都不會紅，要到 CI 的整合測試才炸。
        # 實際踩到：InventoryValuationController 唯一一段改完後模型棄權。
        path.unlink()
        print(f"\n✓ 已刪除 {path.relative_to(ROOT)}：對帳後一則都不剩，匯入端不收空 manifest。")
        return 0
    path.write_text(json.dumps(t.data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # 🚨 重產的那幾則若在 DECISIONS.md 有退回紀錄，**那筆紀錄已經過期**：
    #    它退的是舊文字，而舊文字已經不在了。不講的話，這幾則會同時
    #    「不在待審清單裡」（因為有退回紀錄）又「內容從沒被人看過」——
    #    語料裡最安靜的一種洞。
    decisions = ROOT / ".claude" / "qa-extracts" / "DECISIONS.md"
    if decisions.is_file() and patch:
        recorded = set(re.findall(r"`([a-z0-9-]+-[0-9a-f]{8})`",
                                  decisions.read_text(encoding="utf-8")))
        restale = [e["slug"] for e in patch.values() if e["slug"] in recorded]
    else:
        restale = []

    ua = sum(1 for e in t.unchanged if e["approved"])
    print(f"\n✓ 已寫回 {path.relative_to(ROOT)}")
    print(f"  沿用 {len(t.unchanged)} 段（其中 {ua} 段已審，解凍）")
    print(f"  重產 {len(patch)} 段（一律 approved=false，要重審）")
    for e in t.deleted:
        print(f"  ⚠ 來源已無此段，移除：{e['slug']}　{e['headingPath'][:50]}"
              + ("（原本已審）" if e["approved"] else ""))
    for e in dropped_silent:
        print(f"  ⚠ 段落已變但重跑沒有產物（模型棄權或被閘門擋掉），移除：{e['slug']}"
              f"　{e['headingPath'][:50]}")
    if restale:
        print(f"\n  ⚠ 這 {len(restale)} 則在 DECISIONS.md 有退回紀錄，但**退的是已經不存在的舊文字**——"
              "紀錄已過期，要重新看過並更新該檔：")
        for slug in restale:
            print(f"      {slug}")
        print("      複審清單：scripts/qa-extract-worklist.py --include-rejected --html <路徑>")

    print("\n審核頁在 ~/.cache/qa-extract-reconcile/ 底下；重產的段落審過再把 approved 改 true 送 PR。")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("manifest", nargs="?", help="要對帳的 manifest（不給＝盤點全部）")
    ap.add_argument("--apply", action="store_true", help="重跑受影響段落並寫回 manifest")
    ap.add_argument("--model", default="gemma4:latest")
    # 端點的來源與產生器**必須一模一樣**（`qa-extract-generate.py` 的 `--url`）：
    # 這支腳本會把 `--url` 原樣傳給產生器，兩邊的預設值不同的話，
    # 症狀是本腳本印出「要讓它在這個 shell 一直有效：export QA_OLLAMA_URL=…」
    # 這種正確的提示，然後**自己不照做**——照著設了環境變數，它照樣打 11434 然後回報
    # 「產生器沒有產出 manifest（多半是連不上或參數錯）」。2026-08-26 實際踩到。
    ap.add_argument("--url", default=(
        os.environ.get("QA_OLLAMA_URL")
        or os.environ.get("Features__QaAssistant__OllamaUrl")
        or "http://127.0.0.1:11434"))
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument(
        "--drop-failed", action="store_true",
        help="重跑後沒有產物的段落，移除該則擷取物（先看過產生器印的原因再用）",
    )
    ap.add_argument("--prune", action="store_true",
                    help="不呼叫模型：過期的段落直接刪掉、未改的段落解凍（不給 manifest＝全部）")
    ap.add_argument("--write", action="store_true", help="--prune 時真的寫回（預設只盤點）")
    args = ap.parse_args()

    global DROP_FAILED
    DROP_FAILED = args.drop_failed

    if args.apply:
        if not args.manifest:
            sys.exit("--apply 要指定 manifest：一份一份做，重跑完要看審核頁。")
        return cmd_apply(Path(args.manifest).resolve(), args.model, args.url, args.timeout)

    paths = ([Path(args.manifest).resolve()] if args.manifest
             else sorted(EXTRACTS.glob("*.json")))
    if args.prune:
        return cmd_prune(paths, args.write)
    return cmd_plan(paths)


if __name__ == "__main__":
    raise SystemExit(main())
