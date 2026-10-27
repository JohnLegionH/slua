#!/usr/bin/env python3
# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
"""Assembles the release bundle from the two build jobs' output folders.

Usage: make-bundle.py <repo root> <version> <commit> <win-x64 folder> <linux-x64 folder> <out folder>

Each build folder holds the library, THIRD-PARTY-NOTICES.txt, licenses/ and slua_shim.h as the CI
build job uploads them. The bundle is:

  slua-native-win-x64-<version>.zip         slua_shim.dll
  slua-native-linux-x64-<version>.tar.gz    libslua_shim.so
  slua_shim.h  THIRD-PARTY-NOTICES.txt  licenses.zip  RELEASE-NOTES.md  SHA256SUMS.txt

licenses.zip holds the licence texts under licenses/. The bundle is flat, like the release page:
every file in it is one release asset, and SHA256SUMS.txt lists exactly the other files by those names.

The archives are written with fixed timestamps and owners, so the same libraries always give the
same archive bytes. A library that does not carry the runtime commit from ngc-host/LL-RUNTIME-PIN
is refused.

RELEASE-NOTES.md is the generated text followed by the change list in ngc-host/release-notes/<version>.md
(the folder named by SLUA_RELEASE_NOTES_DIR, for tests), read from the tree the tag points at, so the
release page says what changed and not only what was built. A version without a dash needs the file and is
refused without it; a version with a dash (a dry run or a test) may go without.
"""
import gzip
import hashlib
import io
import os
import re
import shutil
import sys
import tarfile
import zipfile

FIXED_TIME = 1735689600  # 2025-01-01 00:00:00 UTC


def fail(msg):
    sys.exit("make-bundle: " + msg)


def read(path):
    with open(path, "rb") as f:
        return f.read()


def main(argv):
    if len(argv) != 7:
        fail("usage: make-bundle.py <repo root> <version> <commit> <win dir> <linux dir> <out dir>")
    repo, version, commit, win, linux, out = argv[1:]
    pin = read(os.path.join(repo, "ngc-host", "LL-RUNTIME-PIN")).decode().strip()

    for folder, name in [(win, "slua_shim.dll"), (linux, "libslua_shim.so")]:
        path = os.path.join(folder, name)
        if not os.path.isfile(path):
            fail("%s not found in %s" % (name, folder))
        # The library holds the pin as a string constant (the stamp text is put together at run time).
        if pin.encode() not in read(path):
            fail("%s does not carry the runtime commit %s from ngc-host/LL-RUNTIME-PIN; it was not built from the pinned runtime" % (name, pin))

    # Notices, licences and header come from the Linux build; the Windows build must agree.
    shared = ["slua_shim.h", "THIRD-PARTY-NOTICES.txt"] + sorted(
        os.path.join("licenses", f) for f in os.listdir(os.path.join(linux, "licenses")))
    for rel in shared:
        if not os.path.isfile(os.path.join(win, rel)) or read(os.path.join(win, rel)) != read(os.path.join(linux, rel)):
            fail("%s differs between the two build folders" % rel)

    header = read(os.path.join(linux, "slua_shim.h")).decode()
    m = re.search(r"^#define\s+SLUA_API_VERSION\s+(\d+)", header, re.M)
    if not m:
        fail("SLUA_API_VERSION not found in slua_shim.h")
    api = m.group(1)

    os.makedirs(out, exist_ok=True)

    with zipfile.ZipFile(os.path.join(out, "slua-native-win-x64-%s.zip" % version), "w") as z:
        info = zipfile.ZipInfo("slua_shim.dll", date_time=(2025, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        z.writestr(info, read(os.path.join(win, "slua_shim.dll")))

    data = read(os.path.join(linux, "libslua_shim.so"))
    tinfo = tarfile.TarInfo("libslua_shim.so")
    tinfo.size, tinfo.mtime, tinfo.mode = len(data), FIXED_TIME, 0o755
    tinfo.uid = tinfo.gid = 0
    tinfo.uname = tinfo.gname = ""
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as t:
        t.addfile(tinfo, io.BytesIO(data))
    with open(os.path.join(out, "slua-native-linux-x64-%s.tar.gz" % version), "wb") as f:
        with gzip.GzipFile(filename="", mode="wb", fileobj=f, mtime=FIXED_TIME) as g:
            g.write(raw.getvalue())

    for rel in shared:
        if not rel.startswith("licenses"):
            shutil.copyfile(os.path.join(linux, rel), os.path.join(out, rel))

    with zipfile.ZipFile(os.path.join(out, "licenses.zip"), "w") as z:
        for rel in shared:
            if rel.startswith("licenses"):
                info = zipfile.ZipInfo(rel.replace(os.sep, "/"), date_time=(2025, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                z.writestr(info, read(os.path.join(linux, rel)))

    notes_dir = os.environ.get("SLUA_RELEASE_NOTES_DIR") or os.path.join(repo, "ngc-host", "release-notes")
    notes_file = os.path.join(notes_dir, version + ".md")
    change_list = ""
    if os.path.isfile(notes_file):
        change_list = read(notes_file).decode("utf-8").strip() + "\n"
    elif "-" not in version:
        fail("no release notes for %s: add ngc-host/release-notes/%s.md with the change list (looked in %s)" % (version, version, notes_dir))

    notes = """# Release %(version)s

Native library for hosting the SLua runtime: `slua_shim` for Windows x64 and Linux x64. The package
built from these files carries no managed code; the host binds to the C interface in `slua_shim.h`.

- Runtime pin: %(pin)s (the commit of the SLua tree the libraries are built against)
- API version: %(api)s (a host must check `slua_api_version()` for an exact match)
- Saved-state stamp format: `runtime=<pin> api=<n>`; `slua_version` returns the same text,
  here `runtime=%(pin)s api=%(api)s`
- Built from commit: %(commit)s

Every file was built or copied by one CI run from that commit. `SHA256SUMS.txt` lists the SHA-256 of
every other file in the release (check with `sha256sum -c SHA256SUMS.txt`).

| File | Contents |
|---|---|
| `slua-native-win-x64-%(version)s.zip` | `slua_shim.dll` |
| `slua-native-linux-x64-%(version)s.tar.gz` | `libslua_shim.so`, built on Ubuntu 22.04 |
| `slua_shim.h` | the C interface |
| `THIRD-PARTY-NOTICES.txt`, `licenses.zip` | licences of everything linked into the libraries |
""" % dict(version=version, pin=pin, api=api, commit=commit)
    if change_list:
        notes += "\n" + change_list
    with open(os.path.join(out, "RELEASE-NOTES.md"), "w", newline="\n") as f:
        f.write(notes)

    names = []
    for root, _, files in os.walk(out):
        for fn in files:
            rel = os.path.relpath(os.path.join(root, fn), out).replace(os.sep, "/")
            if rel != "SHA256SUMS.txt":
                names.append(rel)
    with open(os.path.join(out, "SHA256SUMS.txt"), "w", newline="\n") as f:
        for rel in sorted(names):
            f.write("%s  %s\n" % (hashlib.sha256(read(os.path.join(out, rel))).hexdigest(), rel))
    print("bundle %s: %d files in %s" % (version, len(names) + 1, out))


main(sys.argv)
