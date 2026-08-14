#!/usr/bin/env python3
"""
doc/knowledge-base/ 链接校验脚本。

校验维度：
  1. 所有 markdown 链接（含 text/url 一致性、相对路径可达性、外链不查）
  2. file:// 残留检测（应已全部改为相对路径）
  3. 行号锚点范围（L1..L2 必须落在目标文件总行数内）
  4. 中文字符 / 特殊字符在 URL 中的兼容性（warn 提示，不阻断）
  5. 文本/URL 路径前缀一致性（"MainWindowViewModel.cs:243-279" 必须指向 MainWindowViewModel.cs，不能误指 GalleryViewModel.cs）
  6. 方括号、圆括号、代码栅栏（```）平衡
  7. 表格列数一致性

退出码：
  0 = 全部通过
  1 = 存在错误
  2 = 仅警告
"""

import os, re, sys, subprocess, json
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
# 本脚本位于 StartTooler/doc/knowledge-base/scripts/validate.py
# ROOT 回退应得到 StartTooler 仓库根目录
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../../.."))
KB_DIR = os.path.join(ROOT, "doc", "knowledge-base")
CODE_DIRS = [
    os.path.join(ROOT, "StartTooler", "Views"),
    os.path.join(ROOT, "StartTooler", "Controls"),
    os.path.join(ROOT, "StartTooler", "ViewModels"),
]

# 仓库全部 markdown 入口（用于跨 repo 一致性）
ALL_MD = []
for d in [ROOT, os.path.join(ROOT, "doc"), os.path.join(ROOT, "doc", "knowledge-base"),
          os.path.join(ROOT, "doc", "skills"),
          os.path.join(ROOT, "doc", "skills", "kb-builder")]:
    if not os.path.isdir(d):
        continue
    for f in sorted(os.listdir(d)):
        if f.endswith(".md"):
            ALL_MD.append(os.path.join(d, f))
# 也包括 doc/skills/kb-builder 子目录（如嵌套后续场景）
skills_root = os.path.join(ROOT, "doc", "skills")
if os.path.isdir(skills_root):
    for sub in os.listdir(skills_root):
        sub_p = os.path.join(skills_root, sub)
        if os.path.isdir(sub_p):
            for f in sorted(os.listdir(sub_p)):
                if f.endswith(".md") and sub_p not in ALL_MD:
                    ALL_MD.append(os.path.join(sub_p, f))
# 去重
ALL_MD = sorted(set(ALL_MD))


def line_count(path):
    if not os.path.isfile(path):
        return None
    with open(path, "rb") as fp:
        return fp.read().decode("utf-8", errors="ignore").count("\n") + 1


def file_links(p):
    """抽取 markdown 中所有 ](url) 链接（link-text 可在之前另行匹配）。"""
    s = open(p, encoding="utf-8").read()
    matches = []
    for m in re.finditer(r"\[([^\]]+?)\]\(([^)\s]+)\)", s):
        text = m.group(1).strip()
        url = m.group(2).strip()
        matches.append((text, url))
    return matches


def check_url(p, url, ctx):
    out = []
    if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", url):
        out.append(("info", f"external: {url}"))
        return out
    if url.startswith("#"):
        out.append(("info", f"anchor-only: {url}"))
        return out
    u_path = url.split("#")[0]
    frag = url[len(u_path):] if "#" in url else ""
    if not u_path:
        out.append(("info", f"pure anchor: {url}"))
        return out

    target_abs = os.path.normpath(os.path.join(os.path.dirname(p), u_path))
    if not os.path.exists(target_abs):
        out.append(("error", f"unreachable: {url} -> {target_abs}"))
        return out
    if os.path.isdir(target_abs):
        out.append(("info", f"dir: {url}"))
        return out

    if frag:
        mm = re.match(r"#L(\d+)(?:-L?(\d+))?", frag)
        if mm:
            l1 = int(mm.group(1)); l2 = int(mm.group(2)) if mm.group(2) else None
            total_lines = line_count(target_abs)
            if l1 < 1 or l1 > total_lines:
                out.append(("error", f"L1 OOR: {frag}, file has {total_lines} lines"))
            if l2 is not None:
                if l2 < l1 or l2 > total_lines:
                    out.append(("error", f"L2 OOR/reversed: {frag}, file has {total_lines} lines"))

    if re.search(r"[\u4e00-\u9fff]", url):
        out.append(("warn", f"URL 包含中文（可能在某些 Markdown 渲染器中失配）：{url}"))

    return out


def check_text_url_consistency(p, text, url):
    """检查 link-text 中的代码文件名 basename 与 url 的 basename 是否一致。"""
    out = []
    txt_names = re.findall(r"([\w./-]+\.(axaml|cs|toml))", text)
    url_path_match = re.search(r"([\w./-]+\.(axaml|cs|toml))(#[^)]+)?$", url)
    if not txt_names or not url_path_match:
        return out
    url_base = os.path.basename(url_path_match.group(1))
    mismatches = [t for t in txt_names if os.path.basename(t[0]) != url_base]
    if mismatches:
        out.append(("error",
                    f"text/url basename mismatch: text_basenames={[os.path.basename(t[0]) for t in txt_names]} vs url_basename={url_base}"))
    return out


def check_balance(p):
    s = open(p, encoding="utf-8").read()
    out = []
    in_code = False
    txt = []
    for line in s.split("\n"):
        if line.startswith("```"):
            in_code = not in_code
            continue
        if not in_code:
            txt.append(line)
    text = "\n".join(txt)
    if text.count("[") != text.count("]"):
        out.append(("error", f"unbalanced square brackets: [ = {text.count('[')}, ] = {text.count(']')}"))
    if text.count("(") != text.count(")"):
        out.append(("error", f"unbalanced parens: ( = {text.count('(')}, ) = {text.count(')')}"))
    fences = sum(1 for line in s.split("\n") if line.startswith("```"))
    if fences % 2 != 0:
        out.append(("error", f"unmatched code fences: {fences} (must be even)"))
    return out


def check_tables(p):
    s = open(p, encoding="utf-8").read()
    out = []
    expected_cols = None
    for i, line in enumerate(s.split("\n"), 1):
        if not re.match(r"\|.*\|", line):
            expected_cols = None
            continue
        if re.match(r"\s*\|[-:]+\|", line):
            continue
        cols = line.count("|") - 1
        if expected_cols is None:
            expected_cols = cols
        elif cols != expected_cols:
            out.append(("warn", f"L{i}: table column count {cols} != {expected_cols}"))
    return out


def check_file_url_residue(p):
    s = open(p, encoding="utf-8").read()
    out = []
    for m in re.finditer(r"file:///\S+", s):
        url = m.group(0)
        if "/Users/hex/code/StartTooler/StartTooler/" in url:
            out.append(("warn", f"still using file:// absolute: {url[:80]}..."))
    return out


def main():
    report = {
        "files": {},
        "summary": {},
    }
    totals = Counter()
    issues = defaultdict(list)

    for p in ALL_MD:
        sfile = os.path.relpath(p, ROOT)
        report["files"][sfile] = {"links": 0, "checks": []}
        for text, url in file_links(p):
            report["files"][sfile]["links"] += 1
            for sev, msg in check_url(p, url, sfile):
                totals[sev] += 1
                issues[sfile].append((sev, msg, text, url))
                report["files"][sfile]["checks"].append({"sev": sev, "msg": msg, "text": text, "url": url})
            for sev, msg in check_text_url_consistency(p, text, url):
                totals[sev] += 1
                issues[sfile].append((sev, msg, text, url))
                report["files"][sfile]["checks"].append({"sev": sev, "msg": msg, "text": text, "url": url})
        for sev, msg in check_balance(p):
            totals[sev] += 1
            issues[sfile].append((sev, msg, None, None))
            report["files"][sfile]["checks"].append({"sev": sev, "msg": msg})
        for sev, msg in check_tables(p):
            totals[sev] += 1
            issues[sfile].append((sev, msg, None, None))
            report["files"][sfile]["checks"].append({"sev": sev, "msg": msg})
        for sev, msg in check_file_url_residue(p):
            totals[sev] += 1
            issues[sfile].append((sev, msg, None, None))
            report["files"][sfile]["checks"].append({"sev": sev, "msg": msg})

    report["summary"] = {
        "files_scanned": len(ALL_MD),
        "links_total": sum(f["links"] for f in report["files"].values()),
        "errors": totals["error"],
        "warns": totals["warn"],
        "infos": totals["info"],
    }

    print("=" * 70)
    print("doc/knowledge-base/ 校验报告")
    print("=" * 70)
    print(f"扫描 .md 文件数: {report['summary']['files_scanned']}")
    print(f"链接总数:       {report['summary']['links_total']}")
    print(f"  errors:       {report['summary']['errors']}")
    print(f"  warns:        {report['summary']['warns']}")
    print(f"  infos:        {report['summary']['infos']}")
    print()

    any_print = False
    for sfile, lst in sorted(issues.items()):
        if not lst:
            continue
        any_print = True
        print(f"--- {sfile}")
        for sev, msg, text, url in lst[:50]:
            extra = f"  text=[{text}] url=[{url}]" if text is not None else ""
            print(f"  [{sev.upper()}] {msg}{extra}")
        if len(lst) > 50:
            print(f"  ... 还有 {len(lst) - 50} 项未显示")
        print()

    if not any_print:
        print("✓ 没有检测到 error/warn 项")
        print("✓ 所有链接本地可达、行号合规、文本与 URL 一致")

    with open("/tmp/kb_validate_report.json", "w") as fp:
        json.dump(report, fp, ensure_ascii=False, indent=2)
    print()
    print("完整 JSON 报告已写到 /tmp/kb_validate_report.json")

    if totals["error"] > 0:
        sys.exit(1)
    elif totals["warn"] > 0:
        sys.exit(2)
    sys.exit(0)


if __name__ == "__main__":
    main()
