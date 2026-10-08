#!/usr/bin/env python3
"""Audit every packaged ELF LOAD/RELRO and uncompressed ZIP offset, no extraction."""
import hashlib
import json
import pathlib
import struct
import sys
import zipfile


def audit_elf(data, required):
    if len(data) < 16 or data[:4] != b'\x7fELF' or data[5] != 1:
        raise ValueError('Expected little-endian ELF')
    bits = data[4]
    if bits == 2:
        header = struct.unpack_from('<HHIQQQIHHHHHH', data, 16)
        phoff, phsize, phnum = header[4], header[8], header[9]
        fmt = '<IIQQQQQQ'
    elif bits == 1:
        header = struct.unpack_from('<HHIIIIIHHHHHH', data, 16)
        phoff, phsize, phnum = header[4], header[8], header[9]
        fmt = '<IIIIIIII'
    else:
        raise ValueError('Unsupported ELF class')
    loads, relros = [], []
    for index in range(phnum):
        entry = struct.unpack_from(fmt, data, phoff + phsize * index)
        if bits == 2:
            kind, flags, offset, vaddr, _, size, memsize, align = entry
        else:
            kind, offset, vaddr, _, size, memsize, flags, align = entry
        if kind == 1:
            valid = align >= required and align & (align - 1) == 0 and offset % required == vaddr % required
            loads.append({'offset': offset, 'virtualAddress': vaddr, 'memorySize': memsize,
                          'flags': flags, 'alignment': align, 'passed': valid})
        if kind == 0x6474E552:
            relros.append({'virtualAddress': vaddr, 'memorySize': memsize, 'endAligned': (vaddr + memsize) % required == 0})
    for relro in relros:
        start = relro['virtualAddress']
        end = start + relro['memorySize']
        protected_start = start // required * required
        protected_end = (end + required - 1) // required * required
        # bionic rounds RELRO protection to pages. Padding outside LOAD data is
        # harmless; actual writable bytes outside RELRO must never be protected.
        extra_writable = []
        covered = False
        for load in loads:
            if not load['flags'] & 2:
                continue
            lo, hi = load['virtualAddress'], load['virtualAddress'] + load['memorySize']
            covered |= lo <= start and end <= hi
            for a, b in [(protected_start, start), (end, protected_end)]:
                if max(a, lo) < min(b, hi):
                    extra_writable.append([max(a, lo), min(b, hi)])
        relro.update({'protectedStart': protected_start, 'protectedEnd': protected_end,
                      'extraWritableRanges': extra_writable, 'coveredByWritableLoad': covered,
                      'passed': covered and not extra_writable})
    return {'bits': bits * 32, 'requiredAlignment': required, 'loadSegments': loads,
            'relroSegments': relros, 'passed': bool(loads) and all(x['passed'] for x in loads + relros)}


def audit(apk, abi):
    raw = pathlib.Path(apk).read_bytes()
    libraries = []
    with zipfile.ZipFile(apk) as archive:
        for entry in archive.infolist():
            if not entry.filename.startswith('lib/') or not entry.filename.endswith('.so'):
                continue
            if entry.filename.split('/')[1] != abi:
                raise ValueError('APK contains an unexpected ABI')
            if entry.compress_type != zipfile.ZIP_STORED:
                raise ValueError('Expected uncompressed native libraries')
            name_size, extra_size = struct.unpack_from('<HH', raw, entry.header_offset + 26)
            offset = entry.header_offset + 30 + name_size + extra_size
            data = archive.read(entry)
            required = 4096 if abi == 'armeabi-v7a' else 16384
            elf = audit_elf(data, required)
            libraries.append({'path': entry.filename, 'sha256': hashlib.sha256(data).hexdigest(),
                              'bytes': len(data), 'zipDataOffset': offset, 'zipAligned16KB': offset % 16384 == 0,
                              'elf': elf, 'passed': elf['passed'] and offset % 16384 == 0})
    names = {pathlib.PurePosixPath(x['path']).name for x in libraries}
    return {'schemaVersion': 1, 'apk': pathlib.Path(apk).name, 'abi': abi, 'bytes': len(raw),
            'sha256': hashlib.sha256(raw).hexdigest(), 'libraries': libraries,
            'passed': {'libflutter.so', 'libapp.so'} <= names and all(x['passed'] for x in libraries),
            'evidenceKind': 'static-archive-and-elf', 'runtimeVerified': False}


if __name__ == '__main__':
    report = audit(sys.argv[1], sys.argv[2])
    print(json.dumps(report, indent=2))
    sys.exit(0 if report['passed'] else 1)
