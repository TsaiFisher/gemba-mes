#!/usr/bin/env python3
"""盤點「使用手冊有寫、但 UITests 一次都沒碰過」的畫面。

── 為什麼要有這支（2026-09-09）──────────────────────────────────────────────
這個清單本來是手抄的。這個 repo 已經因為手抄聚合吃過虧——同一組數字在
manual-test-plan.md 的計數表寫 39／7、§13.6 散文寫 34／12，兩邊都湊得出 47，
而沒有任何東西會發現它們對不起來。所以這裡**不寫清單，只寫怎麼算出清單**。

── 判定方式與它的極限 ───────────────────────────────────────────────────────
母體取自截圖清單（~/.cache/manual-i18n/shots.json，可用 --shots 指定），
那是「手冊有頁、且有對應路由」的畫面。對每個路由，在 SmartFactory.MES.UITests/
底下逐檔找該路由字串。

⚠️ **這是下限，不是精確值。** 靠選單點擊導覽、不寫死路由字串的測試會被算成
「沒有測試」。反過來不會漏報：字串出現了就一定有檔案提到它。
所以「有 UI 測試」的那一欄可能低估，「沒有 UI 測試」那一欄可能包含誤判——
拿它當**待查清單**，不是當結論。

寫入動作的判定是 `[HttpPost`（含 `[HttpPost, ValidateAntiForgeryToken]`）。
⚠️ 第一版寫成 `\\[HttpPost\\]|\\[HttpPost\\(`，漏掉本專案最常見的
`[HttpPost, ValidateAntiForgeryToken]`，於是 MonthEnd／SystemParam／
ReportSchedule 全被誤判成唯讀畫面。改動這條正則前請先拿那三支覆驗。

用法：
    scripts/audit-ui-test-coverage.py
    scripts/audit-ui-test-coverage.py --shots /path/to/shots.json
    scripts/audit-ui-test-coverage.py --format md    # 貼進 PR／規格用
"""
import argparse
import json
import pathlib
import re
import sys

DEFAULT_SHOTS = pathlib.Path.home() / ".cache/manual-i18n/shots.json"


def load_routes(shots_path):
    data = json.loads(shots_path.read_text(encoding="utf-8"))
    return {s["route"].lstrip("/"): s["page"] for s in data if s.get("route")}


def routes_seen_in_uitests(repo, routes):
    ui = repo / "SmartFactory.MES.UITests"
    if not ui.is_dir():
        sys.exit(f"找不到 {ui}")
    seen = set()
    for f in ui.rglob("*.cs"):
        text = f.read_text(encoding="utf-8", errors="replace")
        for r in routes:
            if r in seen:
                continue
            # 前面必須是引號或斜線，避免 "WorkOrder" 誤配到 "WorkOrderProgress"
            if re.search(r'["/]' + re.escape(r) + r"\b", text):
                seen.add(r)
    return seen


def controllers(repo):
    out = {}
    for p in repo.rglob("*Controller.cs"):
        s = str(p)
        if "node_modules" in s or "Tests" in s or "/obj/" in s or "/bin/" in s:
            continue
        out[p.stem[:-len("Controller")]] = p
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shots", type=pathlib.Path, default=DEFAULT_SHOTS)
    ap.add_argument("--format", choices=["text", "md"], default="text")
    args = ap.parse_args()

    repo = pathlib.Path(__file__).resolve().parent.parent
    if not args.shots.is_file():
        sys.exit(f"找不到截圖清單 {args.shots}（用 --shots 指定）")

    routes = load_routes(args.shots)
    seen = routes_seen_in_uitests(repo, routes)
    missing = sorted(set(routes) - seen)
    ctrls = controllers(repo)

    writes, readonly, unknown = [], [], []
    for m in missing:
        p = ctrls.get(m)
        if p is None:
            unknown.append(m)
        elif re.search(r"\[HttpPost\b", p.read_text(encoding="utf-8", errors="replace")):
            writes.append(m)
        else:
            readonly.append(m)

    if args.format == "md":
        print(f"手冊涵蓋畫面 **{len(routes)}**，UITests 出現過 **{len(seen)}**，"
              f"未出現 **{len(missing)}**。\n")
        print(f"### 有寫入動作卻無 UI 測試（{len(writes)}）\n")
        for w in sorted(writes):
            print(f"- `/{w}` — {routes[w]}")
        print(f"\n### 純唯讀、無 UI 測試（{len(readonly)}）\n")
        print(", ".join(f"`{r}`" for r in sorted(readonly)))
        if unknown:
            print(f"\n### 找不到對應 Controller（{len(unknown)}）\n")
            print(", ".join(f"`{u}`" for u in sorted(unknown)))
    else:
        print(f"手冊涵蓋畫面 {len(routes)}／UITests 出現過 {len(seen)}／未出現 {len(missing)}\n")
        print(f"■ 有寫入動作卻無 UI 測試：{len(writes)} 支")
        for w in sorted(writes):
            print(f"    /{w:26s} {routes[w]}")
        print(f"\n■ 純唯讀、無 UI 測試：{len(readonly)} 支")
        print("    " + ", ".join(sorted(readonly)))
        if unknown:
            print(f"\n■ 找不到對應 Controller：{len(unknown)} 支")
            print("    " + ", ".join(sorted(unknown)))

    # 這支是盤點工具，不是守門：一律 exit 0。
    # 要當守門用（例如「寫入型畫面不得再增加未測的」）請另外訂門檻，
    # 而門檻要有人維護——沒人維護的門檻會被一路略過，等於沒有守門。
    return 0


if __name__ == "__main__":
    sys.exit(main())
