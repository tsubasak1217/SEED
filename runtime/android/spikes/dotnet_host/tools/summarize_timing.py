"""
summarize_timing.py - logs/x86_64_t<i>_<scenario>.txt から主要な所要時間を抜き出し、中央値・最小・最大を表にする。
"""
import glob, os, re, statistics, sys
LOG_DIR = sys.argv[1]
KEYS = [
    ("hostfxr.load_from_path", "hostfxr読込"),
    ("hostfxr.initialize_for_runtime_config|hostfxr.initialize_for_dotnet_command_line", "初期化(設定解決)"),
    ("runtime.start(get_delegate_loader_for_assembly)|runtime.start(get_delegate_loader)", "ランタイム起動"),
    ("load_assembly_from_bytes(Default ALC)", "DLLロード(bytes)"),
    ("get_function(Add)", "初回関数取得(Add)"),
    ("call.Add#1", "Add初回呼出"),
    ("call.Add#2", "Add2回目呼出"),
    ("call.RunChecks#1", "RunChecks初回"),
    ("call.RunChecks#2", "RunChecks2回目"),
    ("process.total", "プロセス内合計"),
    ("process.wall_ms", "壁時計(exec含む)"),
]
data = {}
for path in glob.glob(os.path.join(LOG_DIR, "x86_64_t[0-9]_*.txt")):
    m = re.match(r"x86_64_t(\d)_(.+)\.txt$", os.path.basename(path))
    if not m or path.endswith(".logcat.txt"):
        continue
    scenario = m.group(2)
    times = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        parts = line.rstrip("\r\n").split("\t")
        if len(parts) >= 3 and parts[0] in ("TIME", "WALL"):
            times[parts[1]] = float(parts[2])
    for key, _ in KEYS:
        for alt in key.split("|"):
            if alt in times:
                data.setdefault(scenario, {}).setdefault(key, []).append(times[alt])
scenarios = sorted(data)
print("項目\t" + "\t".join(scenarios))
for key, label in KEYS:
    row = [label]
    for sc in scenarios:
        v = data[sc].get(key)
        row.append("-" if not v else f"{statistics.median(v):.1f} ({min(v):.0f}-{max(v):.0f})")
    print("\t".join(row))
