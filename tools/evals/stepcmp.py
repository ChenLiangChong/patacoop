import re, sys
def steps(path, start_marker):
    lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
    if start_marker:
        idx = max(i for i, l in enumerate(lines) if start_marker in l)
        lines = lines[idx:]
    xs = [int(m.group(1)) for l in lines if '[Info   :     state] GamePhase_Play base=' in l for m in [re.search(r'base=(\d+)', l)]]
    return [(a, b - a) for a, b in zip(xs, xs[1:]) if b - a > 3]   # moving 3-second steps: (where, how far)
solo = steps(sys.argv[1], "mission 74 'MissionId_10020'")
coop = steps(sys.argv[2], None)
for name, st in (('solo (you)', solo), ('co-op now', coop)):
    seg = [d for x, d in st if x < 1500]
    print(f"{name:11}: {len(seg)} moving steps before x=1500, average {sum(seg)/max(1,len(seg)):.0f} per 3 s, steps: {[d for d in seg][:14]}")
