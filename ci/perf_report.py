"""Navigation test report: matches the test's STEP lines with the app's lag log (perf.txt).

Usage: python perf_report.py UITEST.log PERF.txt
A "hang" is a time the app's main (UI) thread did not respond: >=100 ms is logged,
>=250 ms is a noticeable stutter, >=500 ms feels frozen.
Note: XCUITest reads the screen through accessibility on the app's main thread between
actions, which itself can cause short hangs; hangs during swipes and typing are the real signal.
"""
import re
import sys

log_path, perf_path = sys.argv[1], sys.argv[2]
log = open(log_path, encoding="utf-8", errors="replace").read()

steps = []
for m in re.finditer(r"^STEP (\d+\.\d+) (\d+\.\d+) (\d+) (.+)$", log, re.M):
    steps.append({"start": float(m[1]), "end": float(m[2]), "ms": int(m[3]), "name": m[4].strip(), "hangs": []})
issues = re.findall(r"^ISSUE \S+ (.+)$", log, re.M)
waits = [(int(a), b) for a, b in re.findall(r"^WAIT (\d+) (.+)$", log, re.M)]

hangs, searches, launches, marks, timings = [], [], [], [], {}
try:
    for line in open(perf_path, encoding="utf-8"):
        parts = line.split()
        if len(parts) < 3:
            continue
        ts, kind, value = float(parts[0]), parts[1], int(parts[2])
        if kind == "hang":
            hangs.append((ts - value / 1000, value))
        elif kind == "search":
            searches.append(value)
        elif kind == "launch":
            launches.append(ts)
        if kind == "mark":
            marks.append((ts, " ".join(parts[3:])))
        elif kind != "hang":
            timings.setdefault(kind, []).append(value)
except FileNotFoundError:
    print("perf.txt missing: the app did not write the lag log")

for start, ms in hangs:
    for s in steps:
        if s["start"] - 0.2 <= start <= s["end"]:
            s["hangs"].append(ms)
            break

def verdict(ms_list):
    if not ms_list:
        return "سلس"
    worst = max(ms_list)
    return "تجمّد" if worst >= 500 else ("تقطيع" if worst >= 250 else "خفيف")

print(f"{'الخطوة':<34} {'المدة':>7} {'تجمّد':>6} {'أسوأ':>6}  الحكم")
for s in steps:
    worst = max(s["hangs"]) if s["hangs"] else 0
    print(f"{s['name']:<34} {s['ms']:>6}ms {len(s['hangs']):>6} {worst:>5}ms  {verdict(s['hangs'])}")

print()
print(f"steps={len(steps)} hangs>=100ms={len(hangs)} hangs>=250ms={sum(1 for _, m in hangs if m >= 250)} "
      f"hangs>=500ms={sum(1 for _, m in hangs if m >= 500)} worst={max((m for _, m in hangs), default=0)}ms")
if searches:
    searches.sort()
    print(f"search_ms: count={len(searches)} median={searches[len(searches)//2]} max={searches[-1]}")
if waits:
    slow = sorted(waits, reverse=True)[:5]
    print("slowest_waits: " + "; ".join(f"{w}ms {n}" for w, n in slow))
m = re.search(r"Executed (\d+) tests?, with (\d+) failures?", log)
print(f"xctest: {m.group(0) if m else 'no summary (test did not run?)'}")
print(f"issues={len(issues)}")
for i in issues:
    print(f"  - {i}")
    print(f"::warning::{i}", file=sys.stderr)
for start, ms in hangs:
    if ms >= 250:
        name = next((s["name"] for s in steps if ms in s["hangs"] and s["start"] - 0.2 <= start <= s["end"]), "?")
        print(f"::warning::UI hang {ms}ms during: {name}", file=sys.stderr)

print()
print("timings: " + "; ".join(f"{k}={v}" for k, v in timings.items()))
if marks:
    print()
    print("Autopilot (in-app, no XCUITest):")
    for i, (ts, name) in enumerate(marks):
        end = marks[i + 1][0] if i + 1 < len(marks) else ts + 3
        inside = [ms for start, ms in hangs if ts - 0.05 <= start < end]
        worst = max(inside, default=0)
        print(f"  {name:<24} hangs={len(inside):>2} worst={worst:>5}ms  {verdict(inside)}")
        if worst >= 250:
            print(f"::warning::Autopilot hang {worst}ms during: {name}", file=sys.stderr)
