#!/usr/bin/env bash
# 前端函式庫漏洞掃描（wwwroot/lib）。
#
# 為什麼要有：CI 的「相依套件漏洞掃描」只看 NuGet（dotnet list package --vulnerable）。
# wwwroot/lib 的 JS／CSS 是手動放進來的檔案，沒有 package.json，任何工具都不會替它查公告——
# 2026-10-19 以 npm audit 補查，DOMPurify 3.2.4 有 15 筆、jquery-validation 1.19.5 有 XSS，
# 都在 repo 裡待了幾個月沒人知道。
#
# 作法：從「實際被頁面載入的那個檔」的檔頭讀出版本（沒有檔頭的以 SHA-256 對照），
# 組成暫時的 package.json 交給 npm audit。不另外維護版本清單——清單會跟檔案漂移，檔頭不會。
#
# 規則：
#   - wwwroot/lib 底下出現未登錄的目錄 → 失敗（新函式庫要在下方 RULES 登錄，否則永遠不會被掃）
#   - 登錄了但讀不出版本 → 失敗（換了檔案或檔頭格式，要更新規則）
#   - npm audit 有 moderate 以上 → 失敗
# Core 與部署專案共用同一份腳本：規則表列的是「可能出現的」函式庫，目錄不存在就略過。
#
# 用法：scripts/check-frontend-libs.sh            （repo 根目錄執行；需要 node／npm 與網路）
set -euo pipefail
cd "$(dirname "$0")/.."
LIB=wwwroot/lib
[ -d "$LIB" ] || { echo "找不到 $LIB"; exit 1; }

python3 - "$LIB" > /tmp/frontend-libs.$$.json <<'PY'
import hashlib, json, os, re, sys
lib = sys.argv[1]
# 目錄 → (npm 套件名, 被載入的檔案, 版本正則；None 表示用 SHA-256 對照)
RULES = {
    "bootstrap":                     ("bootstrap", "dist/js/bootstrap.bundle.min.js", r"Bootstrap v(\d+\.\d+\.\d+)"),
    "bootstrap-icons":               ("bootstrap-icons", "font/bootstrap-icons.min.css", r"Bootstrap Icons v(\d+\.\d+\.\d+)"),
    "chart.js":                      ("chart.js", "chart.umd.min.js", r"Chart\.js v(\d+\.\d+\.\d+)"),
    "dompurify":                     ("dompurify", "dist/purify.min.js", r"DOMPurify (\d+\.\d+\.\d+)"),
    "html5-qrcode":                  ("html5-qrcode", "html5-qrcode.min.js", None),
    "jquery":                        ("jquery", "dist/jquery.min.js", r"jQuery v(\d+\.\d+\.\d+)"),
    "jquery-validation":             ("jquery-validation", "dist/jquery.validate.min.js", r"Validation Plugin - v(\d+\.\d+\.\d+)"),
    "jquery-validation-unobtrusive": ("jquery-validation-unobtrusive", "jquery.validate.unobtrusive.js", r"v(\d+\.\d+\.\d+)"),
    "select2":                       ("select2", "dist/js/select2.min.js", r"Select2 (\d+\.\d+\.\d+(?:-[a-z0-9.]+)?)"),
    "select2-bootstrap-5-theme":     ("select2-bootstrap-5-theme", "dist/select2-bootstrap-5-theme.min.css", r"theme v(\d+\.\d+\.\d+)"),
    "timelines-chart":               ("timelines-chart", "timelines-chart.min.js", r"Version (\d+\.\d+\.\d+)"),
}
# 檔頭沒有版本號的檔案：以 npm 發行包內同名檔的 SHA-256 對照（換版時補一列）
HASHES = {
    "html5-qrcode": {"660b12437b1d": "2.3.8"},
}
errors, deps = [], {}
for d in sorted(os.listdir(lib)):
    if not os.path.isdir(os.path.join(lib, d)):
        continue
    if d not in RULES:
        errors.append(f"{lib}/{d}：未登錄的前端函式庫——請在 scripts/check-frontend-libs.sh 的 RULES 加一列，否則它永遠不會被掃描")
        continue
    name, rel, rx = RULES[d]
    path = os.path.join(lib, d, rel)
    if not os.path.isfile(path):
        errors.append(f"{path}：找不到規則指定的檔案（換了檔名？請更新 RULES）")
        continue
    data = open(path, "rb").read()
    if rx is None:
        ver = HASHES.get(d, {}).get(hashlib.sha256(data).hexdigest()[:12])
    else:
        m = re.search(rx, data[:2000].decode("utf-8", "replace"))
        ver = m.group(1) if m else None
    if not ver:
        errors.append(f"{path}：讀不出版本（檔頭格式變了或換了版本；請更新 RULES／HASHES）")
        continue
    deps[name] = ver
    print(f"  {name}@{ver}  ←  {path}", file=sys.stderr)
if errors:
    print("\n".join("::error::" + e for e in errors), file=sys.stderr)
    sys.exit(2)
print(json.dumps({"name": "frontend-libs-audit", "version": "0.0.0", "private": True, "dependencies": deps}))
PY

work=$(mktemp -d)
trap 'rm -rf "$work" /tmp/frontend-libs.$$.json' EXIT
mv /tmp/frontend-libs.$$.json "$work/package.json"
(cd "$work" && npm install --package-lock-only --ignore-scripts --no-audit --no-fund >/dev/null)
if (cd "$work" && npm audit --audit-level=moderate); then
  echo "✅ wwwroot/lib 無 moderate 以上的已知漏洞"
else
  echo "::error::wwwroot/lib 有已知漏洞——請升級該函式庫（替換檔案後本腳本會自動讀到新版本）。"
  exit 1
fi
