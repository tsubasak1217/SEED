"""
Icons.xaml 生成スクリプト（開発時のみ使用。実行結果の Icons.xaml がビルド対象）。

Iconify API から Material Design Icons (mdi / Apache 2.0) の 24x24 パス data を取得し、
editor/src/Resources/Icons.xaml を丸ごと生成し直す。
アイコンを追加するときは下の CATALOG に 1 行足して再実行するだけでよい。

使い方:  python editor/gen_icons.py
"""

import json
import re
import sys
import urllib.request

# ── 用途キー -> MDI アイコン名 の正典カタログ ────────────────────────────
# 文字列 1 個だけのタプル要素（第2要素 None）は XAML へ出力する見出しコメント。
# キー命名規約は Icon.<分類>.<用途> （分類なしの汎用操作は Icon.<用途>）。
CATALOG = [
    ("── 再生・実行制御 ──", None),
    ("Icon.Play", "play"),
    ("Icon.Pause", "pause"),
    ("Icon.Stop", "stop"),

    ("── 汎用編集操作 ──", None),
    ("Icon.Save", "content-save"),
    ("Icon.Undo", "undo"),
    ("Icon.Redo", "redo"),
    ("Icon.Add", "plus"),
    ("Icon.Remove", "minus"),
    ("Icon.Close", "close"),
    ("Icon.Delete", "trash-can-outline"),
    ("Icon.Reset", "backup-restore"),
    ("Icon.Search", "magnify"),
    ("Icon.Back", "arrow-left"),
    ("Icon.MoveUp", "chevron-up"),
    ("Icon.MoveDown", "chevron-down"),
    ("Icon.Settings", "cog"),
    ("Icon.ProjectSettings", "application-cog"),
    ("Icon.Apply", "check"),
    ("Icon.Warning", "alert-outline"),
    ("Icon.Error", "alert-circle-outline"),
    ("Icon.Info", "information-outline"),
    ("Icon.Build", "hammer-wrench"),
    ("Icon.Browse", "dots-horizontal"),
    ("Icon.DragHandle", "drag-horizontal-variant"),
    ("Icon.Lock", "lock-outline"),
    ("Icon.Dirty", "circle-medium"),
    ("Icon.Prefab", "package-variant-closed"),

    ("── スクリプトデバッグ ──", None),
    ("Icon.Debug.StepOver", "debug-step-over"),
    ("Icon.Debug.StepInto", "debug-step-into"),
    ("Icon.Debug.StepOut", "debug-step-out"),

    ("── ギズモ / ビューポート ──", None),
    ("Icon.Tool.Select", "cursor-default"),
    ("Icon.Tool.Move", "arrow-all"),
    ("Icon.Tool.Rotate", "rotate-3d-variant"),
    ("Icon.Tool.Scale", "resize"),

    ("── ドッキングパネル ──", None),
    ("Icon.Panel.Hierarchy", "file-tree"),
    ("Icon.Panel.OpenDocuments", "tab"),
    ("Icon.Panel.Viewport", "monitor"),
    ("Icon.Panel.ScriptEditor", "file-code"),
    ("Icon.Panel.Project", "folder-open"),
    ("Icon.Panel.Output", "console"),
    ("Icon.Panel.AnimationTimeline", "animation"),
    ("Icon.Panel.ErrorList", "alert-circle-outline"),
    ("Icon.Panel.Profiler", "speedometer"),
    ("Icon.Panel.Inspector", "tune-variant"),
    ("Icon.Panel.AiAssistant", "robot-outline"),
    ("Icon.Panel.Terrain", "terrain"),

    ("── コンポーネント種別（ComponentKind 対応）──", None),
    ("Icon.Component.Transform", "axis-arrow"),
    ("Icon.Component.Model", "cube-outline"),
    ("Icon.Component.Skybox", "panorama-sphere"),
    ("Icon.Component.WaterVolume", "water"),
    ("Icon.Component.WaterLink", "pipe"),
    ("Icon.Component.InteractionSource", "grass"),
    ("Icon.Component.CoverEmitter", "snowflake"),
    ("Icon.Component.Canvas", "rectangle-outline"),
    ("Icon.Component.Sprite", "image-outline"),
    ("Icon.Component.Light", "lightbulb-on-outline"),
    ("Icon.Component.JointAttach", "bone"),
    ("Icon.Component.ParticleEmitter", "shimmer"),
    ("Icon.Component.ControlPoint", "vector-polyline"),
    ("Icon.Component.Camera", "camera"),
    ("Icon.Component.Collider", "shape-outline"),
    ("Icon.Component.Collider2d", "vector-rectangle"),
    ("Icon.Component.Audio", "volume-high"),
    ("Icon.Component.Animator", "animation-play-outline"),
    ("Icon.Component.InputMap", "gamepad-variant-outline"),
    ("Icon.Component.Script", "script-text-outline"),
    ("Icon.Component.Plugin", "puzzle-outline"),
    ("Icon.Component.TerrainChunk", "terrain"),
    ("Icon.Component.Unknown", "hexagon-outline"),

    ("── ヒエラルキーのノード種別 ──", None),
    ("Icon.Node.Folder", "folder-outline"),
    ("Icon.Node.Group", "folder-multiple-outline"),
    ("Icon.Node.Actor3D", "cube"),
    ("Icon.Node.Actor2D", "vector-square"),

    ("── ファイル形式（プロジェクトパネル）──", None),
    ("Icon.File.Generic", "file-outline"),
    ("Icon.File.Folder", "folder"),
    ("Icon.File.FolderEmpty", "folder-outline"),
    ("Icon.File.Scene", "movie-open-outline"),
    ("Icon.File.Actor", "cube"),
    ("Icon.File.Actor2D", "vector-square"),
    ("Icon.File.Model", "cube-outline"),
    ("Icon.File.Image", "file-image"),
    ("Icon.File.Script", "language-csharp"),
    ("Icon.File.Shader", "brush-variant"),
    ("Icon.File.ScriptGeneric", "script-text-outline"),
    ("Icon.File.InputMap", "gamepad-variant-outline"),
    ("Icon.File.Anim", "animation-play-outline"),
    ("Icon.File.Material", "palette-outline"),
    ("Icon.File.PostFx", "auto-fix"),
    ("Icon.File.Audio", "file-music-outline"),
    ("Icon.File.Json", "code-json"),
    ("Icon.File.Config", "file-cog-outline"),
    ("Icon.File.Text", "file-document-outline"),
    ("Icon.File.Terrain", "terrain"),

    ("── パッケージング対象プラットフォーム ──", None),
    ("Icon.Platform.Windows", "microsoft-windows"),
    ("Icon.Platform.MacOS", "apple"),
    ("Icon.Platform.Android", "android"),
    ("Icon.Platform.iOS", "apple-ios"),
    ("Icon.Platform.PlayStation", "sony-playstation"),
    ("Icon.Platform.Switch", "nintendo-switch"),
]

OUT_PATH = "src/Resources/Icons.xaml"
API = "https://api.iconify.design/mdi.json?icons="


def fetch_paths(names):
    """MDI アイコン名の集合をまとめて取得し、名前 -> path の d 属性 の辞書を返す。"""
    # Iconify API は User-Agent 無しのリクエストを 403 で弾くため明示的に付ける。
    req = urllib.request.Request(API + ",".join(sorted(names)),
                                 headers={"User-Agent": "SEEDEditor-icon-generator"})
    data = json.load(urllib.request.urlopen(req))
    missing = data.get("not_found") or []
    if missing:
        sys.exit(f"MDI に存在しないアイコン名: {missing}")
    result = {}
    for name, icon in data["icons"].items():
        ds = re.findall(r'\sd="([^"]+)"', icon["body"])
        if len(ds) != 1:
            sys.exit(f"{name}: path が 1 本ではない（手動対応が必要）: {icon['body']}")
        result[name] = ds[0]
    return result


def main():
    names = {mdi for _, mdi in CATALOG if mdi}
    paths = fetch_paths(names)

    out = []
    out.append('<!--')
    out.append('    ベクターアイコン定義（自動生成ファイル / 手で編集しない）。')
    out.append('')
    out.append('    生成元 : Material Design Icons (prefix "mdi", Apache License 2.0)')
    out.append('    生成器 : editor/gen_icons.py の CATALOG に 1 行足して再実行する')
    out.append('    正典書 : docs/editor_icons.md（キー一覧・用途対応表・追加手順）')
    out.append('')
    out.append('    すべて 24x24 の viewBox 前提。先頭の "F1" は FillRule=Nonzero の指定で、')
    out.append('    SVG の既定塗り規則に合わせるために必須（省略すると WPF 既定の EvenOdd に')
    out.append('    なり、穴あき・自己交差のあるアイコンが欠けて描画される）。')
    out.append('-->')
    out.append('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
    out.append('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
    out.append('                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"')
    out.append('                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"')
    out.append('                    mc:Ignorable="po">')
    out.append('')
    out.append('    <!-- アイコン既定色。Foreground を継承できない箇所（AvalonDock の IconSource など')
    out.append('         ImageSource を要求する API）で IconImages が使う唯一の色定義。 -->')
    out.append('    <SolidColorBrush x:Key="Icon.DefaultBrush" Color="#DCDCDC" po:Freeze="True"/>')

    for key, mdi in CATALOG:
        if mdi is None:
            out.append('')
            out.append(f'    <!-- {key} -->')
            continue
        out.append(f'    <Geometry x:Key="{key}" po:Freeze="True">F1 {paths[mdi]}</Geometry>')

    out.append('')
    out.append('</ResourceDictionary>')

    with open(OUT_PATH, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(out) + "\n")
    n = sum(1 for _, m in CATALOG if m)
    print(f"generated {OUT_PATH}: {n} icons")


if __name__ == "__main__":
    main()
