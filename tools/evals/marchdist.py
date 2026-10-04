import sys, statistics
MARCH = {1, 8, 15, 22, 29}  # Work / Advance actions (the multiplayer march's own type-1 id-1 commands)
rows = []
for line in open(sys.argv[1]):
    try:
        hb, x, c = line.split(); rows.append((int(hb), float(x), int(c)))
    except ValueError:
        pass
rows.sort()
dist = []
for i in range(len(rows)):
    j = i
    while j < len(rows) and rows[j][0] - rows[i][0] < 16:
        j += 1
    if j >= len(rows) or rows[j][0] - rows[i][0] != 16:
        continue
    if all(r[2] in MARCH for r in rows[i:j + 1]):
        dist.append(rows[j][1] - rows[i][1])
moving = [d for d in dist if d > 5]
print(f"{sys.argv[1]}: {len(rows)} half beats, {len(dist)} all-march windows of 16 half beats, {len(moving)} of them moving")
if moving:
    q = statistics.quantiles(moving, n=4) if len(moving) > 3 else moving
    print(f"  distance per 16 half beats while marching: median {statistics.median(moving):.0f}, upper quartile {q[-1]:.0f}, max {max(moving):.0f}")
