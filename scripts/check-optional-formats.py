"""Local feasibility samples for the optional formats in spec 000. No account or network."""
import email.policy
import mailbox
import shutil
import subprocess
import tempfile
import time
import zipfile
from email.message import EmailMessage
from email.parser import BytesParser
from pathlib import Path
from xml.etree import ElementTree


def mail_samples(root):
    message = EmailMessage(policy=email.policy.SMTP)
    message["From"] = "alice@example.invalid"
    message["To"] = "bob@example.invalid"
    message["Subject"] = "迁移样本"
    message["Message-ID"] = "<sample@example.invalid>"
    message.set_content("From message line\n>From existing quote\n")
    message.add_attachment(bytes(range(64)), maintype="application", subtype="octet-stream", filename="样本.bin")
    raw = message.as_bytes()
    (root / "input.eml").write_bytes(raw)
    for box in [mailbox.mbox(root / "sample.mbox"), mailbox.Maildir(root / "sample-maildir")]:
        try:
            key = box.add(raw)
            box.add(raw)
            box.flush()
            restored = box.get_bytes(key)
            parsed = BytesParser(policy=email.policy.default).parsebytes(restored)
            assert len(box) == 2 and parsed["Message-ID"] == message["Message-ID"]
            assert next(parsed.iter_attachments()).get_payload(decode=True) == bytes(range(64))
            body = parsed.get_body().get_content().splitlines()
            print(f"{type(box).__name__}: two equal IDs retained, attachment identical, "
                  f"raw identical={restored == raw}, From lines={body!r}")
        finally:
            box.close()
    def extract(path, limit=65536):
        with zipfile.ZipFile(path) as archive:
            part = archive.getinfo("word/document.xml")
            if part.file_size > limit:
                raise ValueError("part too large for sample extractor")
            xml = ElementTree.fromstring(archive.read(part))
            return "".join(node.text or "" for node in xml.iter("{http://schemas.openxmlformats.org/wordprocessingml/2006/main}t"))

    docx = root / "sample.docx"
    with zipfile.ZipFile(docx, "w") as file:
        file.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        file.writestr("_rels/.rels", '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        file.writestr("word/document.xml", '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>附件命中词</w:t></w:r></w:p></w:body></w:document>')
    assert extract(docx) == "附件命中词"
    try:
        extract(docx, limit=8)
        raise AssertionError("Oversize part accepted")
    except ValueError:
        pass
    damaged = root / "damaged.docx"
    damaged.write_bytes(docx.read_bytes()[:16])
    try:
        extract(damaged)
        raise AssertionError("Truncated archive accepted")
    except zipfile.BadZipFile:
        pass
    print("DOCX: document.xml text and source identified, oversize and truncated inputs rejected.")


def tool(name):
    if found := shutil.which(name):
        return found
    bundled = Path("C:/Program Files/Git/usr/bin", name + ".exe")
    if bundled.is_file():
        return str(bundled)
    raise SystemExit(f"Local {name} executable required for this assessment")


def run(root, args, *, ok=True):
    result = subprocess.run(args, cwd=root, stdin=subprocess.DEVNULL, capture_output=True, timeout=40)
    if (result.returncode == 0) != ok:
        raise RuntimeError(f"{Path(args[0]).stem} {args[1]} unexpected exit {result.returncode}: "
                           + result.stderr.decode(errors="replace")[-400:])
    return result.stdout


def cms(root):
    ssl = tool("openssl")
    run(root, [ssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj",
               "/CN=Local fixture", "-keyout", "key.pem", "-out", "cert.pem"])
    run(root, [ssl, "cms", "-sign", "-binary", "-in", "input.eml", "-signer", "cert.pem",
               "-inkey", "key.pem", "-outform", "DER", "-out", "signed.der"])
    verify = [ssl, "cms", "-verify", "-binary", "-inform", "DER", "-in", "signed.der",
              "-no-CApath", "-no-CAstore", "-out", "verified.eml"]
    run(root, verify + ["-content", "input.eml", "-CAfile", "cert.pem"])
    assert (root / "verified.eml").read_bytes() == (root / "input.eml").read_bytes()
    run(root, verify + ["-content", "input.eml", "-no-CAfile"], ok=False)
    run(root, verify + ["-content", "input.eml", "-CAfile", "cert.pem", "-attime", str(int(time.time()) + 172800)], ok=False)
    (root / "changed.eml").write_bytes((root / "input.eml").read_bytes() + b"changed")
    run(root, verify + ["-content", "changed.eml", "-CAfile", "cert.pem"], ok=False)
    run(root, [ssl, "cms", "-encrypt", "-binary", "-aes256", "-in", "input.eml", "-outform", "DER",
               "-out", "encrypted.der", "cert.pem"])
    decrypt = [ssl, "cms", "-decrypt", "-inform", "DER", "-in", "encrypted.der", "-recip", "cert.pem"]
    run(root, decrypt, ok=False)
    assert run(root, decrypt + ["-inkey", "key.pem"]) == (root / "input.eml").read_bytes()
    print("CMS: detached signature, explicit trust, expiry, changed content, encrypted MIME, missing key and byte round-trip")


def pgp(root):
    home = root / "gnupg"
    home.mkdir(mode=0o700)
    # A disposable home keeps all public and secret keys out of the user's keyring.
    gpg = tool("gpg")
    def home_path(path):
        cygpath = Path(gpg).with_name("cygpath.exe")
        return run(root, [str(cygpath), "-u", str(path)]).decode().strip() if cygpath.exists() else str(path)

    base = [gpg, "--homedir", home_path(home), "--no-options", "--batch", "--no-auto-key-retrieve",
            "--pinentry-mode", "loopback", "--passphrase", ""]
    address = "fixture@example.invalid"
    try:
        run(root, base + ["--quick-generate-key", address, "rsa2048", "sign,encr", "1d"])
        run(root, base + ["--detach-sign", "--output", "signature.pgp", "input.eml"])
        run(root, base + ["--verify", "signature.pgp", "input.eml"])
        run(root, base + ["--verify", "signature.pgp", "changed.eml"], ok=False)
        run(root, base + ["--encrypt", "--recipient", address, "--output", "encrypted.pgp", "input.eml"])
        assert run(root, base + ["--decrypt", "encrypted.pgp"]) == (root / "input.eml").read_bytes()
        empty = root / "empty-keyring"
        empty.mkdir(mode=0o700)
        run(root, [gpg, "--homedir", home_path(empty), "--no-options", "--batch", "--no-autostart",
                   "--decrypt", "encrypted.pgp"], ok=False)
        print("OpenPGP: detached signature, tampering, encrypted packet, missing recipient key and byte round-trip")
    finally:
        run(root, [tool("gpgconf"), "--homedir", home_path(home), "--kill", "gpg-agent"])


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mmb-formats-") as temp:
        root = Path(temp)
        mail_samples(root)
        cms(root)
        pgp(root)
