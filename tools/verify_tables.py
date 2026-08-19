#!/usr/bin/env python3
# Independently re-extract tables from minimp3.h and compare against the
# generated Mp3Tables.cs to catch any table-generation bug.
import re

SRC = r'C:\TestWorkspace\reference\minimp3.h'
CS  = r'C:\TestWorkspace\src\InternetRadio\Mp3\Mp3Tables.cs'

src = open(SRC, encoding='utf-8', errors='replace').read()
cs  = open(CS,  encoding='utf-8', errors='replace').read()

NUM = re.compile(r'-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?[fF]?')

TABLES = {
    "tabs": "Tabs", "tab32": "Tab32", "tab33": "Tab33", "tabindex": "TabIndex",
    "g_linbits": "Linbits", "g_pow43": "Pow43", "g_scf_long": "ScfLong",
    "g_scf_short": "ScfShort", "g_scf_mixed": "ScfMixed",
    "g_scf_partitions": "ScfPartitions", "g_scfc_decode": "ScfcDecode",
    "g_mod": "Mod", "g_preamp": "Preamp", "g_expfrac": "ExpFrac",
    "g_pan": "Pan", "g_aa": "Aa", "g_twid9": "Twid9", "g_twid3": "Twid3",
    "g_mdct_window": "MdctWindow", "g_sec": "Sec", "g_win": "Win",
    "halfrate": "Halfrate",
}


def find_initializer(name):
    m = re.search(r'static\s+const\s+[A-Za-z_][A-Za-z0-9_]*\s+' + re.escape(name) +
                  r'\s*(?:\[[^\]]*\])*\s*=\s*\{', src)
    if not m:
        return None
    start = m.end() - 1
    depth = 0
    i = start
    while i < len(src):
        c = src[i]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return src[start:i + 1]
        i += 1
    return None


def parse_dims(name):
    m = re.search(r'static\s+const\s+[A-Za-z_][A-Za-z0-9_]*\s+' + re.escape(name) +
                  r'((?:\s*\[[^\]]*\])*)', src)
    if not m:
        return None
    parts = re.findall(r'\[([^\]]*)\]', m.group(1))
    dims = []
    for p in parts:
        p = p.strip()
        if p == '':
            return None
        try:
            dims.append(int(eval(p, {'__builtins__': {}}, {})))
        except Exception:
            return None
    return dims


def extract_c_values(name):
    init = find_initializer(name)
    if init is None:
        return None
    dims = parse_dims(name)
    if dims is None or len(dims) == 0:
        # flat
        return [m.group(0) for m in NUM.finditer(init)]
    # nested: parse structure and pad
    def tok(text):
        out = []
        i = 0
        while i < len(text):
            if text[i] in '{}':
                out.append(text[i]); i += 1
            else:
                mm = NUM.match(text, i)
                if mm:
                    out.append(mm.group(0)); i = mm.end()
                else:
                    i += 1
        return out
    toks = tok(init)
    pos = 0

    def parse(dim_idx):
        nonlocal pos
        assert toks[pos] == '{', toks[pos]
        pos += 1
        arr = []
        while toks[pos] != '}':
            if dim_idx + 1 < len(dims):
                arr.append(parse(dim_idx + 1))
            else:
                arr.append(toks[pos]); pos += 1
            if toks[pos] == ',':
                pos += 1
        pos += 1  # }
        # pad to declared size
        while len(arr) < dims[dim_idx]:
            arr.append(zero(dim_idx + 1))
        return arr

    def zero(dim_idx):
        if dim_idx >= len(dims):
            return '0'
        return [zero(dim_idx + 1) for _ in range(dims[dim_idx])]

    tree = parse(0)

    def flat(x):
        if isinstance(x, list):
            r = []
            for e in x:
                r.extend(flat(e))
            return r
        return [x]
    return flat(tree)


def extract_cs_values(cs_name):
    m = re.search(r'readonly\s+\w+\[\]\s+' + cs_name + r'\s*=\s*new\s+\w+\[\]\s*\{(.*?)\};', cs, re.S)
    if not m:
        return None
    return [mm.group(0) for mm in NUM.finditer(m.group(1))]


def norm(v):
    v = v.rstrip('fF')
    if '.' in v or 'e' in v.lower():
        return float(v)
    return int(v)


bad = 0
for cname, csname in TABLES.items():
    cv = extract_c_values(cname)
    csv = extract_cs_values(csname)
    if cv is None or csv is None:
        print('%-16s MISSING (c=%s cs=%s)' % (cname, cv is not None, csv is not None))
        bad += 1
        continue
    nv = [norm(x) for x in cv]
    ncs = [norm(x) for x in csv]
    if len(nv) != len(ncs):
        print('%-16s LEN MISMATCH c=%d cs=%d' % (cname, len(nv), len(ncs)))
        bad += 1
        continue
    diffs = [i for i in range(len(nv)) if abs(nv[i] - ncs[i]) > 1e-6]
    if diffs:
        print('%-16s VALUE DIFF at %d positions (first: %s)' % (cname, len(diffs), diffs[:10]))
        bad += 1
    else:
        print('%-16s OK (%d values)' % (cname, len(nv)))

print('BAD TABLES:', bad)
