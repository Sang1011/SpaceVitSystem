import itertools
import json
import sys
from pathlib import Path

import jiwer

from bench import normalize

RESULTS = Path("results")
lang = sys.argv[1]

data = {}
for f in RESULTS.glob(f"*_{lang}.json"):
    model = f.stem.rsplit("_", 1)[0]
    rows = json.loads(f.read_text(encoding="utf-8"))
    data[model] = {r["file"]: r["hypothesis"] for r in rows}

models = sorted(data)
files = sorted(set().union(*[set(d) for d in data.values()]))

for name in files:
    print(f"\n=== {name} ===")
    for m in models:
        print(f"[{m}] {data[m].get(name, '(không có)')}\n")
    for a, b in itertools.combinations(models, 2):
        if name in data[a] and name in data[b]:
            ta, tb = normalize(data[a][name]), normalize(data[b][name])
            if ta and tb:
                diff = jiwer.wer(ta, tb) * 100
                print(f"Khác nhau {a} vs {b}: {diff:.1f}%")