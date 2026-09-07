"""シーンに保存された HIT アイテムの位置を「出現時の静止位置」として 4 本の .anim を生成する。

規則（HitBanner.cs のクラスコメントと同じ）:
  帯 … 法線方向（上帯は左上の外、下帯は右下の外）から easeOutCubic で入場、静止、easeInCubic で同じ方向へ退場
  文字 … 帯の方向に沿って両脇から easeOutCubic で入場（少し遅れて）、静止、同じ向きへ通り抜けて退場
時刻はすべてフレーム格子（FPS）に乗せる。
"""
import json, math, sys

SCENE = r"C:SERSK023GSOURCERUSTPROJECTSSEEDUNTIMESSETSMAINGAMEMAINGAME.SCENE"
OUT_DIR = r"C:\Users\k023g\source\RustProjects\SEED\runtime\assets\mainGame\animations"

FPS = 30
BAND_IN_END_F = 8      # 帯の入場が終わるフレーム（≒0.27 s）
TEXT_DELAY_F = 3       # 文字が動き出すフレーム（≒0.10 s）
TEXT_IN_END_F = 14     # 文字の入場が終わるフレーム（≒0.47 s）
HOLD_END_F = 44        # 静止が終わるフレーム（≒1.47 s）
END_F = 53             # 尺（≒1.77 s）

BAND_TRAVEL_PX = 600     # 帯が法線方向に出入りする距離（実 px）
TEXT_IN_PX = 1000        # 文字の入場距離（帯方向）
TEXT_OUT_PX = 2600       # 文字の退場距離（帯方向・1920 幅でも画面外へ抜ける）

ITEMS = {
    # name: (kind, 入場方向の符号)  帯: 法線 n の符号 / 文字: 帯方向 d の符号
    "HitBandBlackTop":    ("band", -1),   # rest - n*D から入る（左上の外）
    "HitBandBlackBottom": ("band", +1),   # rest + n*D から入る（右下の外）
    "HitTextLevel":       ("text", -1),   # rest - d*D から入り、+d へ抜ける
    "HitTextHit":         ("text", +1),   # rest + d*D から入り、-d へ抜ける
}
FILES = {
    "HitBandBlackTop": "hit_banner_band_top.anim",
    "HitBandBlackBottom": "hit_banner_band_bottom.anim",
    "HitTextLevel": "hit_banner_text_level.anim",
    "HitTextHit": "hit_banner_text_hit.anim",
}


def find(a, n):
    if a["name"] == n:
        return a
    for c in a.get("children", []):
        r = find(c, n)
        if r:
            return r
    return None


def f2t(f):
    return round(f / FPS, 6)


def key(f, value, interp, in_tan=None, out_tan=None):
    k = {"time": f2t(f), "value": value, "interp": interp}
    if in_tan is not None:
        k["in_tan"] = in_tan
    if out_tan is not None:
        k["out_tan"] = out_tan
    return k


def r4(v):
    return [round(x, 4) for x in v]


def main():
    scene = json.load(open(SCENE, encoding="utf-8"))
    ui = None
    for root in scene["actors"]:
        ui = find(root, "FishingUI")
        if ui:
            break
    assert ui, "FishingUI が無い"

    for name, (kind, sign) in ITEMS.items():
        a = find(ui, name)
        assert a, f"{name} が無い"
        ct = a["canvas_transform"]
        rest = list(ct["position"])
        rot = ct["rotation"]
        th = math.radians(rot)
        d = (math.cos(th), math.sin(th))          # 帯の方向（キャンバス px・y 下向き）
        n = (-math.sin(th), math.cos(th))         # 法線
        comps = {c["name"]: c["component"]["data"] for c in a["components"]}

        if kind == "band":
            start = [rest[0] + sign * n[0] * BAND_TRAVEL_PX, rest[1] + sign * n[1] * BAND_TRAVEL_PX]
            end = start
            in_f0, in_f1 = 0, BAND_IN_END_F
            rgb = comps["Sprite"]["color"][:3]
            color_comp = "sprite"
        else:
            start = [rest[0] + sign * d[0] * TEXT_IN_PX, rest[1] + sign * d[1] * TEXT_IN_PX]
            end = [rest[0] - sign * d[0] * TEXT_OUT_PX, rest[1] - sign * d[1] * TEXT_OUT_PX]
            in_f0, in_f1 = TEXT_DELAY_F, TEXT_IN_END_F
            rgb = comps["Text"]["color"][:3]
            color_comp = "text"

        dt_in = (in_f1 - in_f0) / FPS
        dt_out = (END_F - HOLD_END_F) / FPS
        # easeOutCubic: out_tan = 3Δ/dt, in_tan = 0 / easeInCubic: out_tan = 0, in_tan = 3Δ/dt
        tan_in = [3 * (rest[i] - start[i]) / dt_in for i in range(2)]
        tan_out = [3 * (end[i] - rest[i]) / dt_out for i in range(2)]

        pos_keys = []
        if kind == "text":
            pos_keys.append(key(0, r4(start), "step"))
        pos_keys += [
            key(in_f0, r4(start), "bezier", out_tan=r4(tan_in)),
            key(in_f1, r4(rest), "linear", in_tan=[0.0, 0.0]),
            key(HOLD_END_F, r4(rest), "bezier", out_tan=[0.0, 0.0]),
            key(END_F, r4(end), "linear", in_tan=r4(tan_out)),
        ]
        first_visible_f = 0 if kind == "band" else TEXT_DELAY_F
        clip = {
            "name": "Hit",
            "duration": f2t(END_F),
            "fps": FPS,
            "loop_mode": "once",
            "tracks": [
                {
                    "target": {"actor_path": "", "component": "canvas_transform", "property": "position"},
                    "value_type": "vec2",
                    "keys": pos_keys,
                },
                {
                    "target": {"actor_path": "", "component": "canvas_transform", "property": "rotation"},
                    "value_type": "float",
                    "keys": [key(0, rot, "step")],
                },
                {
                    "target": {"actor_path": "", "component": color_comp, "property": "color"},
                    "value_type": "color",
                    "keys": [
                        key(first_visible_f, [rgb[0], rgb[1], rgb[2], 1.0], "step"),
                        key(END_F, [rgb[0], rgb[1], rgb[2], 0.0], "step"),
                    ],
                },
            ],
        }
        path = f"{OUT_DIR}\\{FILES[name]}"
        with open(path, "w", encoding="utf-8", newline="\n") as fp:
            json.dump(clip, fp, ensure_ascii=False, indent=2)
            fp.write("\n")
        print(f"{name}: rest={r4(rest)} start={r4(start)} end={r4(end)} -> {FILES[name]}")


if __name__ == "__main__":
    main()
