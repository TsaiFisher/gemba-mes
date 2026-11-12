#!/usr/bin/env python3
"""擷取物語料的來源交叉比對 —— 發布前的側面洩漏防線。

擷取物是從某份原始文件「提煉」出來的自然語言規則。如果那份來源文件被
scripts/public-filter.txt 擋下，卻沒有一併擋下由它產生的語料，等於**從側面把
擋掉的內容放出去**：文件沒公開，但它的內容以另一種形式公開了。

這種洩漏用 public-filter-check.sh 看不出來——排除清單上那份文件明明在，
看起來一切正常。只有把每份語料的 src 拿去對過濾規則才會浮現。

用法：
    check-extract-sources.py [ref]      預設 HEAD；有問題時 exit 1
"""
import importlib.util
import json
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
CORPUS_DIR = ".claude/qa-extracts"


def _load_filter():
    spec = importlib.util.spec_from_file_location("public_filter", HERE / "public_filter.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    ref = sys.argv[1] if len(sys.argv) > 1 else "HEAD"
    root = pathlib.Path(subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        capture_output=True, text=True, check=True).stdout.strip())

    pf = _load_filter()
    rules = pf.load_rules(root)

    at_ref = set(subprocess.run(
        ["git", "ls-tree", "-r", "--name-only", "-z", ref],
        capture_output=True, text=True, check=True).stdout.split("\0")) - {""}
    published = {f for f in at_ref if pf.keep(f, rules)}

    manifests = sorted(f for f in published
                       if f.startswith(CORPUS_DIR + "/") and f.endswith(".json"))

    leaked, unreadable, untracked = [], [], []
    for m in manifests:
        raw = subprocess.run(["git", "show", f"{ref}:{m}"],
                             capture_output=True, text=True)
        if raw.returncode != 0:
            unreadable.append((m, "讀不到")); continue
        try:
            doc = json.loads(raw.stdout)
        except json.JSONDecodeError as e:
            unreadable.append((m, f"JSON 解析失敗：{e}")); continue

        src = doc.get("src") or doc.get("source") or doc.get("sourcePath")
        if not src:
            unreadable.append((m, "manifest 沒有 src 欄位")); continue

        if src not in at_ref:
            untracked.append((m, src))
        elif src not in published:
            leaked.append((m, src))

    print(f"擷取物 {len(manifests)} 份")

    bad = False
    if leaked:
        bad = True
        print(f"\n🚨 來源文件被擋下，語料卻會發布（{len(leaked)} 份）——"
              f"等於從側面公開了擋掉的內容：")
        for m, s in leaked:
            print(f"   {pathlib.Path(m).name}  ←  {s}")
        print("\n   處置：把這些語料也加進 scripts/public-filter.txt 的排除，"
              "或重新評估那份來源文件是否真的不能公開。")

    if untracked:
        bad = True
        print(f"\n⚠️ 來源文件不在這個 ref 裡（{len(untracked)} 份）——"
              f"語料指向一份不存在的文件：")
        for m, s in untracked:
            print(f"   {pathlib.Path(m).name}  ←  {s}")

    if unreadable:
        bad = True
        print(f"\n⚠️ 無法判定（{len(unreadable)} 份）：")
        for m, why in unreadable:
            print(f"   {pathlib.Path(m).name}：{why}")

    if not bad:
        print("  ✅ 每一份語料的來源都會一併發布")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
