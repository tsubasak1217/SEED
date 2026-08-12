"""
アイコンキー参照の整合チェック（開発時のみ使用）。

Icons.xaml に定義された x:Key と、エディタのソース中で文字列として参照されている
"Icon.*" を突き合わせ、未定義キーの参照（＝実行時にアイコンが描画されない）を洗い出す。
キーの綴り間違いは C# / XAML のビルドでは検出できないため、このスクリプトが検査手段。

使い方:  python editor/check_icons.py     （終了コード 1 = 未定義参照あり）
"""

import io
import os
import re
import sys

ICONS_XAML = "resources/icons/Icons.xaml"
SOURCE_ROOT = "src"
SOURCE_EXTS = (".cs", ".xaml")

# アイコンキーではない（ブラシなど）ため未使用判定から除外するキー。
NON_ICON_KEYS = {"Icon.DefaultBrush"}


def main():
    defined = set(re.findall(
        r'x:Key="(Icon\.[\w.]+)"',
        io.open(ICONS_XAML, encoding="utf-8").read()))

    used: dict[str, set[str]] = {}
    for root, _dirs, files in os.walk(SOURCE_ROOT):
        for name in files:
            if not name.endswith(SOURCE_EXTS):
                continue
            path = os.path.join(root, name)
            if path.replace("\\", "/").endswith(ICONS_XAML):
                continue
            text = io.open(path, encoding="utf-8", errors="replace").read()
            for match in re.finditer(r'"(Icon\.[\w.]+)"', text):
                used.setdefault(match.group(1), set()).add(path)

    missing = {k: sorted(v) for k, v in used.items() if k not in defined}
    unused = sorted(defined - set(used) - NON_ICON_KEYS)

    print(f"定義済みキー: {len(defined)}")
    print(f"参照キー    : {len(used)}")
    print(f"未使用キー  : {len(unused)} {unused}")
    if missing:
        print("未定義参照（実行時にアイコンが出ない）:")
        for key, paths in sorted(missing.items()):
            print(f"  {key}: {paths}")
        sys.exit(1)
    print("未定義参照: なし")


if __name__ == "__main__":
    main()
