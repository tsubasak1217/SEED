# -*- coding: utf-8 -*-
"""魚図鑑カタログ（FishCatalog.cs）の生成スクリプト。

エディタの「図鑑画像を生成」（cmd: generate_fish_thumbnails）が行う C# 生成と
**同一の出力**を、エディタ無しでも作れるようにしたフォールバック実装。
CI や、ランタイム／エディタをビルドできない環境での再生成に使う。

生成元は `runtime/assets/mainGame/actors/Fish/Lv<N>/<name>.actor` 群のみで、
レベルはディレクトリ名（`Lv<N>`）を唯一の情報源とする。
（実行時のレベルは FishManager の levels 配列から引かれるが、あれはシーン内データで
　図鑑生成のためだけにシーンを読むのは依存が重いので、prefab の置き場所を正典とする）

使い方:
    python tools/gen_fish_catalog.py [--repo <SEEDのルート>] [--check]

    --check を付けると書き込まずに、既存ファイルと一致するかだけを検証する
    （終了コード 0 = 一致 / 1 = 不一致・未生成）。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

# ─── 定数（マジックナンバー・マジックストリング禁止）─────────────────────

#: このスクリプトから見たリポジトリルート（tools/ の 1 つ上）。
DEFAULT_REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: assets ルート（リポジトリルートからの相対）。
ASSETS_REL_ROOT = os.path.join("runtime", "assets")

#: 魚 prefab の置き場所（assets ルートからの相対）。
FISH_ACTOR_REL_DIR = "mainGame/actors/Fish"

#: 図鑑画像の出力先（assets ルートからの相対）。
ZUKAN_TEXTURE_REL_DIR = "mainGame/textures/zukan"

#: 生成する C# ファイル（assets ルートからの相対）。
CATALOG_CS_REL_PATH = "mainGame/scripts/FishCatalog.cs"

#: エディタ／ランタイムが使うアセット URI のスキーム接頭辞。
ASSET_URI_PREFIX = "assets://"

#: レベルディレクトリ名の書式（`Lv3` → 3）。
LEVEL_DIR_PATTERN = re.compile(r"^Lv(\d+)$")

#: .actor の拡張子。
ACTOR_EXT = ".actor"

#: 図鑑画像の拡張子。
IMAGE_EXT = ".png"

#: 魚スクリプトのファイル名（ScriptComponent.type_name の末尾で判定する）。
FISH_SCRIPT_FILE_NAME = "Fish.cs"

#: 表示名が入っている Fish スクリプトのフィールド名。
DISPLAY_NAME_FIELD = "fishName"

#: 自動生成ファイルの先頭に置く警告ヘッダ。
GENERATED_HEADER = (
    "// 自動生成 — 編集しないで「図鑑画像を生成」で再生成\n"
    "// （エディタ: Tools > 図鑑画像を生成 / cmd: generate_fish_thumbnails /\n"
    "//   エディタ無し: python tools/gen_fish_catalog.py）\n"
    "// 生成元: runtime/assets/mainGame/actors/Fish/Lv<N>/*.actor\n"
)


# ─── データ収集 ───────────────────────────────────────────────────────


def _read_json(path: str) -> dict:
    """.actor（JSON）を読む。BOM 付きでも読めるように utf-8-sig で開く。"""
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def _fish_display_name(actor: dict, fallback: str) -> str:
    """.actor の Fish スクリプト設定から表示名を取り出す。

    ScriptComponent の fields は「既定値と異なる値だけ」が保存されるため、
    表示名が未入力の prefab には `fishName` キー自体が存在しない。
    その場合はアクタ名（＝ローマ字の種別名）で代替する。
    """
    for component in actor.get("components", []):
        inner = component.get("component", {})
        if inner.get("type") != "ScriptComponent":
            continue
        data = inner.get("data", {})
        # type_name は Windows の絶対パス（"\\" 区切り）で保存されている
        script_file = os.path.basename(str(data.get("type_name", "")).replace("\\", "/"))
        if script_file != FISH_SCRIPT_FILE_NAME:
            continue
        name = str(data.get("fields", {}).get(DISPLAY_NAME_FIELD, "")).strip()
        if name:
            return name
    return fallback


def collect_entries(repo_root: str) -> list[dict]:
    """魚 prefab を走査して、カタログ 1 行ぶんの辞書のリストを返す。

    並び順は「レベル昇順 → アクタ名の辞書順」で決定的（差分が安定する）。
    """
    assets_root = os.path.join(repo_root, ASSETS_REL_ROOT)
    fish_dir = os.path.join(assets_root, *FISH_ACTOR_REL_DIR.split("/"))
    entries: list[dict] = []

    if not os.path.isdir(fish_dir):
        raise FileNotFoundError(f"魚 prefab のディレクトリが見つからない: {fish_dir}")

    for level_dir_name in sorted(os.listdir(fish_dir)):
        matched = LEVEL_DIR_PATTERN.match(level_dir_name)
        if not matched:
            continue  # FishBase.actor など、レベル配下でないものは対象外
        level = int(matched.group(1))
        level_dir = os.path.join(fish_dir, level_dir_name)
        if not os.path.isdir(level_dir):
            continue

        for file_name in sorted(os.listdir(level_dir)):
            if not file_name.endswith(ACTOR_EXT):
                continue
            stem = file_name[: -len(ACTOR_EXT)]
            actor_path = os.path.join(level_dir, file_name)
            actor = _read_json(actor_path)
            actor_name = str(actor.get("name") or stem)

            entries.append({
                "level": level,
                "actorName": actor_name,
                "displayName": _fish_display_name(actor, actor_name),
                # ランタイム／スクリプト API が解決できる assets:// URI で持つ
                "actorPath": f"{ASSET_URI_PREFIX}{FISH_ACTOR_REL_DIR}/{level_dir_name}/{file_name}",
                "imagePath": f"{ASSET_URI_PREFIX}{ZUKAN_TEXTURE_REL_DIR}/{level_dir_name}/{stem}{IMAGE_EXT}",
            })

    entries.sort(key=lambda e: (e["level"], e["actorName"]))
    return entries


# ─── C# ソース生成 ────────────────────────────────────────────────────


def _cs_string_literal(value: str) -> str:
    """C# の文字列リテラルへエスケープする（" と \\ のみで足りる）。"""
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def render_catalog_cs(entries: list[dict]) -> str:
    """FishCatalog.cs のソース全文を組み立てる。"""
    max_level = max((e["level"] for e in entries), default=0)

    lines: list[str] = []
    lines.append(GENERATED_HEADER.rstrip("\n"))
    lines.append("")
    lines.append("using System;")
    lines.append("using System.Collections.Generic;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// 図鑑 1 種ぶんの静的データ（prefab から自動抽出した内容）。")
    lines.append("/// </summary>")
    lines.append("[Serializable]")
    lines.append("public struct FishCatalogEntry")
    lines.append("{")
    lines.append("    /// <summary>魚レベル（1〜<see cref=\"FishCatalog.MaxLevel\"/>）。prefab の置き場所 Lv&lt;N&gt; が正典。</summary>")
    lines.append("    public int level;")
    lines.append("")
    lines.append("    /// <summary>アクタ名（.actor の name。ローマ字の種別 ID として使える）。</summary>")
    lines.append("    public string actorName;")
    lines.append("")
    lines.append("    /// <summary>表示名（Fish スクリプトの「表示名」。未入力なら <see cref=\"actorName\"/> と同じ）。</summary>")
    lines.append("    public string displayName;")
    lines.append("")
    lines.append("    /// <summary>prefab の assets:// パス。</summary>")
    lines.append("    public string actorPath;")
    lines.append("")
    lines.append("    /// <summary>図鑑画像（透過 PNG）の assets:// パス。<c>SEED.Sprite.TexturePath</c> にそのまま入る。</summary>")
    lines.append("    public string imagePath;")
    lines.append("}")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// 全魚種の図鑑データ表。エディタの「図鑑画像を生成」で丸ごと再生成される。")
    lines.append("/// 並び順は「レベル昇順 → アクタ名の辞書順」で固定。")
    lines.append("/// </summary>")
    lines.append("public static class FishCatalog")
    lines.append("{")
    lines.append("    /// <summary>収録されている最大の魚レベル。</summary>")
    lines.append(f"    public const int MaxLevel = {max_level};")
    lines.append("")
    lines.append("    /// <summary>全エントリ（レベル昇順 → アクタ名順）。</summary>")
    lines.append("    public static readonly FishCatalogEntry[] Entries =")
    lines.append("    {")

    for entry in entries:
        fields = ", ".join([
            f"level = {entry['level']}",
            f"actorName = {_cs_string_literal(entry['actorName'])}",
            f"displayName = {_cs_string_literal(entry['displayName'])}",
            f"actorPath = {_cs_string_literal(entry['actorPath'])}",
            f"imagePath = {_cs_string_literal(entry['imagePath'])}",
        ])
        lines.append(f"        new FishCatalogEntry {{ {fields} }},")

    lines.append("    };")
    lines.append("")
    lines.append("    /// <summary>指定レベルのエントリだけを列挙する（並び順は <see cref=\"Entries\"/> と同じ）。</summary>")
    lines.append("    /// <param name=\"lv\">魚レベル。該当が無ければ空列挙を返す。</param>")
    lines.append("    public static IEnumerable<FishCatalogEntry> ForLevel(int lv)")
    lines.append("    {")
    lines.append("        foreach (FishCatalogEntry entry in Entries)")
    lines.append("        {")
    lines.append("            if (entry.level == lv) { yield return entry; }")
    lines.append("        }")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>アクタ名で 1 件引く。見つからなければ false。</summary>")
    lines.append("    /// <param name=\"actorName\">.actor の name（大文字小文字は区別しない）。</param>")
    lines.append("    /// <param name=\"found\">見つかったエントリ。</param>")
    lines.append("    public static bool TryGetByActorName(string actorName, out FishCatalogEntry found)")
    lines.append("    {")
    lines.append("        foreach (FishCatalogEntry entry in Entries)")
    lines.append("        {")
    lines.append("            if (string.Equals(entry.actorName, actorName, StringComparison.OrdinalIgnoreCase))")
    lines.append("            {")
    lines.append("                found = entry;")
    lines.append("                return true;")
    lines.append("            }")
    lines.append("        }")
    lines.append("        found = default;")
    lines.append("        return false;")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>表示名で 1 件引く。見つからなければ false。</summary>")
    lines.append("    /// <param name=\"displayName\">Fish スクリプトの表示名（大文字小文字は区別しない）。</param>")
    lines.append("    /// <param name=\"found\">見つかったエントリ。</param>")
    lines.append("    public static bool TryGetByDisplayName(string displayName, out FishCatalogEntry found)")
    lines.append("    {")
    lines.append("        foreach (FishCatalogEntry entry in Entries)")
    lines.append("        {")
    lines.append("            if (string.Equals(entry.displayName, displayName, StringComparison.OrdinalIgnoreCase))")
    lines.append("            {")
    lines.append("                found = entry;")
    lines.append("                return true;")
    lines.append("            }")
    lines.append("        }")
    lines.append("        found = default;")
    lines.append("        return false;")
    lines.append("    }")
    lines.append("}")
    lines.append("")

    return "\n".join(lines)


# ─── エントリポイント ─────────────────────────────────────────────────


def main() -> int:
    parser = argparse.ArgumentParser(description="FishCatalog.cs を prefab から生成する")
    parser.add_argument("--repo", default=DEFAULT_REPO_ROOT, help="SEED リポジトリのルート")
    parser.add_argument("--check", action="store_true", help="書き込まず、既存ファイルとの一致だけ検証する")
    args = parser.parse_args()

    entries = collect_entries(args.repo)
    source = render_catalog_cs(entries)
    out_path = os.path.join(args.repo, ASSETS_REL_ROOT, *CATALOG_CS_REL_PATH.split("/"))

    if args.check:
        if not os.path.isfile(out_path):
            print(f"[NG] 未生成: {out_path}")
            return 1
        with open(out_path, "r", encoding="utf-8-sig", newline="") as f:
            current = f.read().replace("\r\n", "\n")
        if current != source:
            print(f"[NG] 内容が最新でない: {out_path}")
            return 1
        print(f"[OK] 最新: {out_path}（{len(entries)} 種）")
        return 0

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    # 改行は LF 固定（エディタ側の生成と 1 バイトも違わないようにするため）
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(source)
    print(f"[OK] 生成: {out_path}（{len(entries)} 種 / MaxLevel={max((e['level'] for e in entries), default=0)}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
