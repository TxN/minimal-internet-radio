#!/usr/bin/env python3
# Generates C# constant tables from the public-domain minimp3 reference (CC0).
#
# The tables are transcribed verbatim, but multi-dimensional initializers are
# parsed structurally and each row is zero-padded to its *declared* size. This
# reproduces the C compiler's implicit zero-padding, which minimp3 actually
# relies on (e.g. g_scf_mixed rows have 37..40 values in a [8][40] array).
import re, os

SRC = r'C:\TestWorkspace\reference\minimp3.h'
OUT = r'C:\TestWorkspace\src\InternetRadio\Mp3\Mp3Tables.cs'

# (c_name, cs_name, cs_type)
TABLES = [
    ("tabs",            "Tabs",            "short"),
    ("tab32",           "Tab32",           "byte"),
    ("tab33",           "Tab33",           "byte"),
    ("tabindex",        "TabIndex",        "short"),
    ("g_linbits",       "Linbits",         "byte"),
    ("g_pow43",         "Pow43",           "float"),
    ("g_scf_long",      "ScfLong",         "byte"),
    ("g_scf_short",     "ScfShort",        "byte"),
    ("g_scf_mixed",     "ScfMixed",        "byte"),
    ("g_scf_partitions","ScfPartitions",   "byte"),
    ("g_scfc_decode",   "ScfcDecode",      "byte"),
    ("g_mod",           "Mod",             "byte"),
    ("g_preamp",        "Preamp",          "byte"),
    ("g_expfrac",       "ExpFrac",         "float"),
    ("g_pan",           "Pan",             "float"),
    ("g_aa",            "Aa",              "float"),
    ("g_twid9",         "Twid9",           "float"),
    ("g_twid3",         "Twid3",           "float"),
    ("g_mdct_window",   "MdctWindow",      "float"),
    ("g_sec",           "Sec",             "float"),
    ("g_win",           "Win",             "float"),
    ("halfrate",        "Halfrate",        "byte"),
]

src = open(SRC, 'r', encoding='utf-8', errors='replace').read()

NUM = re.compile(r'-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?[fF]?')


def find_def(name):
    pat = re.compile(
        r'static\s+const\s+[A-Za-z_][A-Za-z0-9_]*\s+' + re.escape(name) +
        r'(\s*(?:\[[^\]]*\])*)?\s*=\s*\{')
    m = pat.search(src)
    if not m:
        raise SystemExit('definition not found: ' + name)
    dims_txt = m.group(1) or ''
    return m.end() - 1, dims_txt  # index of '{' and the "[...][...]" text


def parse_dims(dims_txt):
    """Return list of declared dimensions, or None if any dimension is unknown ('[]')."""
    if not dims_txt.strip():
        return []  # flat scalar array
    parts = re.findall(r'\[([^\]]*)\]', dims_txt)
    dims = []
    for p in parts:
        p = p.strip()
        if p == '':
            return None  # unknown length -> flatten by tokens
        try:
            dims.append(int(eval(p, {'__builtins__': {}}, {})))
        except Exception:
            return None
    return dims


def extract_body(open_brace):
    """Return the initializer text including its outer braces."""
    depth = 0
    i = open_brace
    while i < len(src):
        c = src[i]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return src[open_brace:i + 1]
        i += 1
    raise SystemExit('unbalanced braces')


def tokenize(text):
    toks = []
    i = 0
    while i < len(text):
        c = text[i]
        if c == '{' or c == '}':
            toks.append(c)
            i += 1
        else:
            m = NUM.match(text, i)
            if m:
                toks.append(m.group(0))
                i = m.end()
            else:
                i += 1
    return toks


def parse_array(toks, pos, dims):
    """Parse a (sub)array of remaining dims from toks at pos. Returns (value, new_pos)."""
    if not dims:
        assert pos < len(toks) and toks[pos] not in ('{', '}'), 'expected number at %d' % pos
        return toks[pos], pos + 1

    size = dims[0]
    assert toks[pos] == '{', 'expected { at %d, got %r' % (pos, toks[pos])
    pos += 1
    arr = []
    while pos < len(toks) and toks[pos] != '}':
        val, pos = parse_array(toks, pos, dims[1:])
        arr.append(val)
        if pos < len(toks) and toks[pos] == ',':
            pos += 1
    if pos < len(toks) and toks[pos] == '}':
        pos += 1

    leaf = zero_leaf(dims[1:])
    while len(arr) < size:
        arr.append(leaf)
    return arr, pos


def zero_leaf(dims):
    if not dims:
        return '0'
    return [zero_leaf(dims[1:]) for _ in range(dims[0])]


def flatten(x):
    if isinstance(x, list):
        out = []
        for e in x:
            out.extend(flatten(e))
        return out
    return [x]


def get_values(name):
    open_brace, dims_txt = find_def(name)
    body = extract_body(open_brace)
    dims = parse_dims(dims_txt)

    if dims is None or dims == []:
        return [m.group(0) for m in NUM.finditer(body)]  # flat by tokens

    toks = tokenize(body)
    if not toks or toks[0] != '{':
        return [m.group(0) for m in NUM.finditer(body)]
    val, pos = parse_array(toks, 0, dims)
    return flatten(val)


def fmt(val, cstype):
    if cstype == 'float':
        v = val[:-1] if val.endswith(('f', 'F')) else val
        return v + 'f'
    return val.rstrip('fF')


out = []
out.append('// Auto-generated from minimp3 (CC0 public domain) by tools/gen_tables.py.')
out.append('// Do not edit by hand.')
out.append('namespace InternetRadio')
out.append('{')
out.append('    internal static class Mp3Tables')
out.append('    {')

for cname, csname, cstype in TABLES:
    vals = get_values(cname)
    vals = [fmt(v, cstype) for v in vals]
    out.append('        // %s  (element count: %d)' % (cname, len(vals)))
    out.append('        internal static readonly %s[] %s = new %s[]' % (cstype, csname, cstype))
    out.append('        {')
    for i in range(0, len(vals), 16):
        out.append('            ' + ', '.join(vals[i:i + 16]) + ',')
    out.append('        };')
    out.append('')

out.append('    }')
out.append('}')
out.append('')

os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, 'w', encoding='utf-8', newline='\n').write('\n'.join(out))
print('generated', OUT)
for cname, csname, cstype in TABLES:
    print('%-16s %6d' % (cname, len(get_values(cname))))
