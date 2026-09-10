"""リザルトパネルの魚画像（ResultPanel/ResultBody/FishImage）の登場・退場クリップを生成する。

規則（ResultPanel.cs のクラスコメントと同じ）:
  登場 result_fish_in  … 画面いっぱい相当の倍率から easeOutCubic で表示サイズ（等倍）へ縮む
  退場 result_fish_out … 等倍から easeInCubic で 0 倍へ縮んで消える

「画面いっぱい相当の倍率」は<b>ここでしか持たない</b>（インスペクタには出さない）。
キャンバスの設計解像度と FishImage のスプライト実寸から逆算するので、
絵の大きさを変えたらこのスクリプトを流し直せば追従する:

    倍率 = max(キャンバス幅 / 絵の幅, キャンバス高 / 絵の高) * FILL_MARGIN

時刻はすべてフレーム格子（FPS）に乗せる。タンジェントは「値 / 秒」で、
easeOutCubic は始点の out_tan = 3Δ/dt・終点の in_tan = 0、
easeInCubic は始点の out_tan = 0・終点の in_tan = 3Δ/dt になる
（ランタイム側 engine/animation/sampler.rs の hermite と一致する形）。

出力は既存の .anim（エディタ保存形式）に合わせて CRLF・末尾改行なしで書く。
"""
import json

# ─── 入出力パス ────────────────────────────────────────────────
# 倍率の逆算はプレハブ（正典）の値で行い、シーン側の実体はズレの検出にだけ使う。
PREFAB = r"C:\Users\k023g\source\RustProjects\SEED\runtime\assets\mainGame\actors\UI\ResultPanel.actor"
SCENE = r"C:\Users\k023g\source\RustProjects\SEED\runtime\assets\mainGame\MainGame.scene"
OUT_DIR = r"C:\Users\k023g\source\RustProjects\SEED\runtime\assets\mainGame\animations"

# ─── 演出の尺（フレーム格子）──────────────────────────────────
FPS = 30
IN_END_F = 12          # 登場の尺（0.400 s）
OUT_END_F = 8          # 退場の尺（0.267 s ≒ 0.25 s）

# ─── 倍率 ──────────────────────────────────────────────────────
# 画面を覆い切ったうえで少し外へはみ出す余裕（1.0 = ちょうど画面幅/高さ）。
# 図鑑画像は周囲に余白を含むため、ちょうどだと「画面いっぱい」に見えない。
FILL_MARGIN = 1.4
END_SCALE = 1.0        # 表示サイズ（＝プレハブの実寸そのまま）
HIDDEN_SCALE = 0.0     # 消え切った倍率

# ─── 対象（アクタ名・クリップ名・出力ファイル名）─────────────
FISH_IMAGE_ACTOR = "FishImage"          # Animator を付けるアクタ（クリップは自分自身を動かす）
CANVAS_COMPONENT = "CanvasComponent"    # 設計解像度の出どころ
SPRITE_COMPONENT = "SpriteComponent"    # 絵の実寸の出どころ
IN_CLIP_NAME = "result_fish_in"
OUT_CLIP_NAME = "result_fish_out"
FILES = {
    IN_CLIP_NAME: "result_fish_in.anim",
    OUT_CLIP_NAME: "result_fish_out.anim",
}

# ─── .anim の書式 ─────────────────────────────────────────────
JSON_INDENT = 2
LINE_SEPARATOR = "\r\n"   # 既存の .anim（エディタ保存）に合わせる
EASE_TANGENT_SCALE = 3.0  # 3 次イージングのタンジェント係数（p(t)=t^3 系の傾き）
ROUND_DIGITS = 4


def find_actor(node, name):
    """アクタ木から名前でノードを探す（深さ優先・最初に見つかったもの）。"""
    if node.get("name") == name:
        return node
    for child in node.get("children", []):
        found = find_actor(child, name)
        if found:
            return found
    return None


def find_component_data(actor, type_name):
    """アクタのコンポーネント一覧から指定種別の data を取り出す（無ければ None）。"""
    for slot in actor.get("components", []):
        component = slot.get("component", {})
        if component.get("type") == type_name:
            return component.get("data", {})
    return None


def find_actor_in_file(path, name):
    """.actor / .scene のどちらからでもアクタを探す（.scene は actors 配列を持つ）。"""
    root = json.load(open(path, encoding="utf-8"))
    roots = root.get("actors", [root]) if isinstance(root, dict) else root
    for r in roots:
        found = find_actor(r, name)
        if found:
            return found
    return None


def f2t(frame):
    """フレーム番号を秒へ（フレーム格子に乗せた時刻）。"""
    return round(frame / FPS, 6)


def key(frame, value, interp, in_tan=None, out_tan=None):
    """キーフレーム 1 個ぶんの辞書を作る（タンジェントは省略可）。"""
    k = {"time": f2t(frame), "value": value, "interp": interp}
    if in_tan is not None:
        k["in_tan"] = in_tan
    if out_tan is not None:
        k["out_tan"] = out_tan
    return k


def scale_pair(scale):
    """縦横同倍率の vec2 値（丸めてから出す）。"""
    return [round(scale, ROUND_DIGITS), round(scale, ROUND_DIGITS)]


def scale_tangent(delta, seconds):
    """3 次イージングのタンジェント（値/秒）を vec2 で返す。"""
    slope = round(EASE_TANGENT_SCALE * delta / seconds, ROUND_DIGITS)
    return [slope, slope]


def build_clip(name, end_frame, start_scale, end_scale, ease_out):
    """倍率だけを動かす 1 トラックのクリップを組み立てる。

    ease_out=True なら easeOutCubic（速く動き出して静かに収まる＝登場）、
    False なら easeInCubic（静かに動き出して一気に消える＝退場）。
    """
    seconds = f2t(end_frame)
    delta = end_scale - start_scale
    tangent = scale_tangent(delta, seconds)
    zero = [0.0, 0.0]
    return {
        "name": name,
        "duration": seconds,
        "fps": FPS,
        "loop_mode": "once",
        "tracks": [
            {
                "target": {
                    "actor_path": "",             # 空文字 ＝ Animator を持つアクタ自身
                    "component": "canvas_transform",
                    "property": "scale",
                },
                "value_type": "vec2",
                "keys": [
                    key(0, scale_pair(start_scale), "bezier",
                        out_tan=tangent if ease_out else zero),
                    key(end_frame, scale_pair(end_scale), "linear",
                        in_tan=zero if ease_out else tangent),
                ],
            },
        ],
        "events": [],
    }


def write_clip(clip, file_name):
    """.anim を既存ファイルと同じ書式（CRLF・末尾改行なし）で書き出す。"""
    path = f"{OUT_DIR}\\{file_name}"
    text = json.dumps(clip, ensure_ascii=False, indent=JSON_INDENT)
    with open(path, "w", encoding="utf-8", newline=LINE_SEPARATOR) as fp:
        fp.write(text)
    return path


def main():
    # ── 倍率の逆算に使う実寸をプレハブから読む ──
    prefab = json.load(open(PREFAB, encoding="utf-8"))
    canvas = find_component_data(prefab, CANVAS_COMPONENT)
    assert canvas, "ResultPanel.actor のルートに CanvasComponent が無い"

    fish = find_actor(prefab, FISH_IMAGE_ACTOR)
    assert fish, f"{FISH_IMAGE_ACTOR} が ResultPanel.actor に無い"
    sprite = find_component_data(fish, SPRITE_COMPONENT)
    assert sprite, f"{FISH_IMAGE_ACTOR} に SpriteComponent が無い"

    canvas_w, canvas_h = float(canvas["width"]), float(canvas["height"])
    fish_w, fish_h = float(sprite["width"]), float(sprite["height"])
    assert fish_w > 0 and fish_h > 0, "FishImage のスプライト実寸が 0"

    # ── シーンに置かれた実体とズレていないか見る（ズレていても生成は続ける）──
    scene_fish = find_actor_in_file(SCENE, FISH_IMAGE_ACTOR)
    scene_sprite = find_component_data(scene_fish, SPRITE_COMPONENT) if scene_fish else None
    if scene_sprite and (float(scene_sprite["width"]), float(scene_sprite["height"])) != (fish_w, fish_h):
        print(f"警告: シーンの {FISH_IMAGE_ACTOR} の実寸 "
              f"({scene_sprite['width']}x{scene_sprite['height']}) がプレハブ "
              f"({fish_w}x{fish_h}) と違う。倍率はプレハブ基準で作る")

    start_scale = max(canvas_w / fish_w, canvas_h / fish_h) * FILL_MARGIN

    clips = [
        (build_clip(IN_CLIP_NAME, IN_END_F, start_scale, END_SCALE, ease_out=True), FILES[IN_CLIP_NAME]),
        (build_clip(OUT_CLIP_NAME, OUT_END_F, END_SCALE, HIDDEN_SCALE, ease_out=False), FILES[OUT_CLIP_NAME]),
    ]
    for clip, file_name in clips:
        path = write_clip(clip, file_name)
        first = clip["tracks"][0]["keys"][0]["value"][0]
        last = clip["tracks"][0]["keys"][-1]["value"][0]
        print(f"{clip['name']}: scale {first} -> {last} / {clip['duration']}s -> {path}")

    print(f"（キャンバス {canvas_w:.0f}x{canvas_h:.0f} / 絵 {fish_w:.0f}x{fish_h:.0f} "
          f"/ 余裕 {FILL_MARGIN} → 開始倍率 {round(start_scale, ROUND_DIGITS)}）")


if __name__ == "__main__":
    main()
