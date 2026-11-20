"""Render the plain-text handbook bundled with the apps for GitHub readers."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
lines = (root / "使用说明.txt").read_text(encoding="utf-8-sig").splitlines()
start = lines.index("快速查找目录")
end = lines.index("", start + 1)
chapters = lines[start + 1:end]
assert len(chapters) == 16 and all(line[:2] == f"{i:02}" for i, line in enumerate(chapters, 1))
out = ["# " + lines[0], ""]
for i, line in enumerate(lines[1:], 1):
    if i == start:
        out.extend(["## " + line, ""])
    elif start < i < end:
        out.append(f"- [{line}](#section-{line[:2]})")
    elif line in chapters:
        out.extend([f'<a id="section-{line[:2]}"></a>', "", "## " + line])
    elif not line:
        out.append("")
    elif line.startswith("• "):
        if i and not lines[i-1].startswith("• "):
            out.append("")
        out.append("- " + line[2:])
    elif re.match(r"^[0-9]+\. ", line):
        if line.startswith("1. "):
            out.append("")
        out.append(line)
    elif i + 1 < len(lines) and not lines[i-1] and lines[i+1] and len(line) < 48 and not line.endswith(("。", "：")):
        out.extend(["### " + line, ""])
    else:
        out.extend([line, ""])
(root / "操作手册.md").write_bytes((re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip() + "\n").encode("utf-8"))
