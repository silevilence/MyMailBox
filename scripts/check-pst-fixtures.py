"""Re-run the independent reader on existing Outlook fixtures and assert exact fidelity."""
import hashlib
import html
import json
import subprocess
import sys
from pathlib import Path

repo = Path(__file__).resolve().parent.parent
root = Path(sys.argv[1] if len(sys.argv) > 1 else repo / "artifacts/outlook-acceptance/final").resolve()
expected = {item["Name"]: item for item in json.loads((root / "attachments-expected.json").read_text(encoding="utf-8-sig"))}
comparisons = []
probe = repo / "tools/MyMailBox.PstProbe/bin/Debug/net10.0/win-x64/MyMailBox.PstProbe.exe"
subprocess.run([str(probe), "--self-test"], check=True, timeout=20)


def attachments(messages):
    for message in messages:
        for attachment in message["files"]:
            if attachment["type"] == "message":
                yield from attachments([attachment["content"]])
            elif attachment["type"] == "file":
                yield attachment


for kind in ("unicode", "ansi"):
    subprocess.run([str(probe), str(root / f"read-{kind}.pst"), str(root / f"{kind}-report.json"), "936"], check=True, timeout=60)
    data = json.loads((root / f"{kind}-report.json").read_text())
    passed, losses = [], []

    def check(condition, description):
        (passed if condition else losses).append(description)

    check(data["sourceUnchanged"], "source hash unchanged")
    check(not data["errors"], "no reader errors")
    check(any(f["Path"].endswith("测试邮件\\嵌套目录") for f in data["folders"]), "Chinese nested folder exact")
    check(data["messageCount"] == (37 if kind == "unicode" else 4), "item count")
    check(data["embedded"] == 1, "embedded message count")
    for attachment in attachments(data["messages"]):
        original = expected[attachment["name"]]
        check(attachment["sha256"] == original["Sha256"] and attachment["bytes"] == original["Bytes"], "attachment bytes: " + attachment["name"])
    plain = next(m for m in data["messages"] if m["subject"].startswith("MMB-plain"))
    check(plain["subject"] == "MMB-plain-中文", "Chinese subject exact")
    check(plain["text"] == "纯文本正文 Alpha 中文", "plain body exact")
    rich = next(m for m in data["messages"] if m["subject"].startswith("MMB-html"))
    check("HTML 中文" in html.unescape(rich["html"] or ""), "HTML Chinese text")
    check("cid:mmb-pixel" in (rich["html"] or "") and any(a.get("cid") == "mmb-pixel" for a in rich["files"]), "CID link and image")
    rtf = next(m for m in data["messages"] if m["subject"].startswith("MMB-rtf"))
    check("Gamma" in (rtf["rtf"] or ""), "RTF body recoverable with CRC validation")
    check(rtf["text"] == "RTF 正文 Gamma", "RTF Chinese body exact")
    nested = next(a["content"] for m in data["messages"] for a in m["files"] if a["type"] == "message")
    check(nested["subject"] == plain["subject"], "embedded subject exact")
    check(nested["text"] == plain["text"], "embedded body exact")
    check(all("\u0000" not in m["subject"] and "\ufffd" not in m["subject"] for m in data["messages"]), "no subject padding or replacement characters")
    oracle = json.loads((root / "outlook-expected.json").read_text(encoding="utf-8-sig"))
    expected_html = next(m["Html"] for m in oracle if m["Pst"] == kind and m["Subject"] == "MMB-html-CID")
    check(html.unescape(rich["html"]) == html.unescape(expected_html), "HTML exact after entity decoding against Outlook")
    if kind == "unicode":
        check(sum(m["nonMail"] for m in data["messages"]) == 1, "non-mail contact explicitly identified")
    comparisons.append({"format": kind, "passedChecks": len(passed), "fidelityLosses": losses,
                        "seconds": data["seconds"], "peakWorkingSet": data["peakWorkingSet"], "bytes": data["bytes"]})

source = root / "read-unicode.pst"
broken = root / "truncated-copy.pst"
assert broken.parent == root and source != broken
with source.open("rb") as stream:
    header = stream.read(512)
if not broken.exists():
    broken.write_bytes(header)
assert broken.read_bytes() == header
run = subprocess.run([str(probe), str(broken), str(root / "truncated-report.json")], capture_output=True, text=True, timeout=20)
damage = json.loads((root / "truncated-report.json").read_text())
assert run.returncode == 2 and damage["errors"] and damage["sourceUnchanged"]
corrupt = root / "corrupt-item.pst"
subprocess.run([str(probe), "--corrupt-rtf-copy", str(root / "read-ansi.pst"), str(corrupt)], check=True, timeout=20)
run = subprocess.run([str(probe), str(corrupt), str(root / "corrupt-item-report.json"), "936"], capture_output=True, text=True, timeout=20)
item_damage = json.loads((root / "corrupt-item-report.json").read_text())
assert run.returncode == 2 and item_damage["sourceUnchanged"] and len(item_damage["errors"]) == 1
assert "CRC did not match" in item_damage["errors"][0] and len(item_damage["messages"]) == 3
assert any(m["subject"] == "MMB-embedded" for m in item_damage["messages"]), "Continue after corrupt message"
for kind in ("unicode", "ansi"):
    original = root / f"{kind}.pst"
    copy = root / f"read-{kind}.pst"
    with original.open("rb") as a, copy.open("rb") as b:
        assert hashlib.file_digest(a, "sha256").digest() == hashlib.file_digest(b, "sha256").digest()
result = {"comparisons": comparisons, "truncatedCopyRejected": True, "corruptItemIsolated": True, "originalPstsRetained": True}
(root / "comparison.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(result, ensure_ascii=False, indent=2))
# A research comparison must expose a fidelity failure, rather than silently treating parsing as success.
sys.exit(2 if any(item["fidelityLosses"] for item in comparisons) else 0)
