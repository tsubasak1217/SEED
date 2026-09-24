"""
stage.py - Android へ push するディレクトリ一式を ABI ごとに組み立てる。

作るレイアウト（stage/<abi>/ 配下）:
  spike_host                       … Rust ホスト
  sc/                              … 自己完結の平坦ディレクトリ（dotnet publish --self-contained の出力そのまま）
                                     SpikeLib.runtimeconfig.json は includedFrameworks 形式
  dotnet/host/fxr/<ver>/libhostfxr.so
  dotnet/shared/Microsoft.NETCore.App/<ver>/...
                                   … PC にインストールされる .NET と同じ「dotnet ルート」形式（SEED の Windows 同梱と同じ形）
  app/                             … フレームワーク依存の SpikeLib（runtimeconfig は framework 形式）

使い方:
  python stage.py <publish_dir> <rid> <version> <spike_lib_build_dir> <spike_plugin_dll> <host_binary> <out_dir>
"""
import json
import os
import shutil
import sys

# 端末へ送らないファイル（Mono を静的リンクする場合にだけ使う .a と、デバッグシンボル）
EXCLUDED_SUFFIXES = (".a", ".pdb", ".dbg")
# 共有フレームワーク名
FRAMEWORK_NAME = "Microsoft.NETCore.App"
# hostfxr の実体名
HOSTFXR_FILE = "libhostfxr.so"
# ランタイムパックのライブラリ名の接頭辞（自己完結 deps.json 内の名前）
RUNTIME_PACK_PREFIX = "runtimepack.Microsoft.NETCore.App.Runtime."


def copy_filtered(src_dir: str, dst_dir: str, skip_names=()) -> None:
    """src_dir 直下のファイルを、除外拡張子・除外名を除いて dst_dir へコピーする。"""
    os.makedirs(dst_dir, exist_ok=True)
    for name in os.listdir(src_dir):
        path = os.path.join(src_dir, name)
        if not os.path.isfile(path):
            continue
        if name.endswith(EXCLUDED_SUFFIXES) or name in skip_names:
            continue
        shutil.copy2(path, os.path.join(dst_dir, name))


def build_framework_deps(sc_deps_path: str, rid: str, version: str) -> dict:
    """
    自己完結 publish の deps.json に載っているランタイムパックの資産一覧から、
    共有フレームワーク用の Microsoft.NETCore.App.deps.json を組み立てる（インストール版と同じ形）。
    """
    with open(sc_deps_path, encoding="utf-8") as f:
        sc = json.load(f)
    target_name = sc["runtimeTarget"]["name"]
    pack_key = f"{RUNTIME_PACK_PREFIX}{rid}/{version}"
    pack = sc["targets"][target_name][pack_key]
    runtime = dict(pack.get("runtime", {}))
    # hostfxr は host/fxr 側に置くのでフレームワークの資産から外す。.a も端末に送らない
    native = {k: v for k, v in pack.get("native", {}).items()
              if k != HOSTFXR_FILE and not k.endswith(EXCLUDED_SUFFIXES)}
    lib_key = f"{FRAMEWORK_NAME}.Runtime.{rid}/{version}"
    return {
        "runtimeTarget": {"name": target_name, "signature": ""},
        "compilationOptions": {},
        "targets": {
            target_name.split("/")[0]: {},
            target_name: {lib_key: {"runtime": runtime, "native": native}},
        },
        "libraries": {
            lib_key: {"type": "package", "serviceable": True, "sha512": "",
                      "path": f"{FRAMEWORK_NAME.lower()}.runtime.{rid}/{version}"},
        },
    }


def write_json(path: str, data: dict) -> None:
    """整形して JSON を書く。"""
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def write_runtimeconfig_variants(dir_path: str, base_name: str) -> None:
    """
    runtimeconfig.json から Invariant 無し版（*.noinv.runtimeconfig.json）を派生させる。
    ICU の無い bionic で Invariant を外すと何が起きるかを確かめるため。
    """
    src = os.path.join(dir_path, f"{base_name}.runtimeconfig.json")
    with open(src, encoding="utf-8") as f:
        cfg = json.load(f)
    props = cfg["runtimeOptions"].setdefault("configProperties", {})
    props.pop("System.Globalization.Invariant", None)
    props.pop("System.Globalization.PredefinedCulturesOnly", None)
    write_json(os.path.join(dir_path, f"{base_name}.noinv.runtimeconfig.json"), cfg)


def main() -> None:
    publish_dir, rid, version, lib_build_dir, plugin_dll, host_bin, out_dir = sys.argv[1:8]
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)

    # ── ホスト ──
    shutil.copy2(host_bin, os.path.join(out_dir, "spike_host"))

    # ── レイアウト1: 自己完結の平坦ディレクトリ ──
    sc_dir = os.path.join(out_dir, "sc")
    copy_filtered(publish_dir, sc_dir)
    shutil.copy2(plugin_dll, sc_dir)
    write_runtimeconfig_variants(sc_dir, "SpikeLib")

    # ── レイアウト2: dotnet ルート形式 ──
    root = os.path.join(out_dir, "dotnet")
    fxr_dir = os.path.join(root, "host", "fxr", version)
    fw_dir = os.path.join(root, "shared", FRAMEWORK_NAME, version)
    os.makedirs(fxr_dir)
    shutil.copy2(os.path.join(publish_dir, HOSTFXR_FILE), fxr_dir)
    # ランタイムパックの資産だけをフレームワークへ（アプリ本体 SpikeLib.* は app/ 側）
    copy_filtered(publish_dir, fw_dir, skip_names=(HOSTFXR_FILE, "SpikeLib.dll", "SpikeLib.deps.json",
                                                   "SpikeLib.runtimeconfig.json"))
    write_json(os.path.join(fw_dir, f"{FRAMEWORK_NAME}.deps.json"),
               build_framework_deps(os.path.join(publish_dir, "SpikeLib.deps.json"), rid, version))
    write_json(os.path.join(fw_dir, f"{FRAMEWORK_NAME}.runtimeconfig.json"),
               {"runtimeOptions": {"tfm": "net" + ".".join(version.split(".")[:2])}})

    # ── フレームワーク依存のアプリ（SEED の SEEDScripting.dll と同じ立場）──
    app_dir = os.path.join(out_dir, "app")
    os.makedirs(app_dir)
    for name in ("SpikeLib.dll", "SpikeLib.deps.json", "SpikeLib.runtimeconfig.json"):
        shutil.copy2(os.path.join(lib_build_dir, name), app_dir)
    shutil.copy2(plugin_dll, app_dir)
    write_runtimeconfig_variants(app_dir, "SpikeLib")

    # ── 集計 ──
    for sub in ("sc", "dotnet", "app"):
        total = 0
        count = 0
        for dirpath, _, files in os.walk(os.path.join(out_dir, sub)):
            for name in files:
                total += os.path.getsize(os.path.join(dirpath, name))
                count += 1
        print(f"{sub}: files={count} bytes={total} ({total / 1024 / 1024:.1f} MiB)")


if __name__ == "__main__":
    main()
