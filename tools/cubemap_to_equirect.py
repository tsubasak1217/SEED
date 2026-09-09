#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
cubemap_to_equirect.py

【モジュール概要】
SEED エンジンで使っているキューブマップ・アトラス画像（1 枚の PNG に 6 面を
タイル状に並べたもの）を、SEED のスカイボックスが対応する
「正距円筒（equirectangular）1 枚絵の .hdr（Radiance RGBE, リニア色空間）」
に変換するツール。

対象アトラスのデフォルトレイアウト（skybox.png 用）:
  - 側面 4 枚: 画像の中段の横帯（y: F..2F）を x 方向に 4 分割し、
    左から右へ水平方向に連続して 360° を一周する。
  - 上面 (+Y): 右列・上段 (col=3, row=0)。ほぼ単色。
  - 下面 (-Y): 右列・下段 (col=3, row=2)。ほぼ単色。
  ここで F は 1 面の一辺のピクセル数（正方形）。

【変換アルゴリズムの要点】
出力 equirect 画像の各ピクセル (u, v) について、
  1. 経度 phi・緯度 theta を求める。
  2. 球面上の方向ベクトル (x, y, z) を作る。
  3. 各面法線とのなす内積が最大（＝絶対値最大の軸）の面を選ぶ標準的な
     キューブマップの面選択規則で、6 面のどれに属するかを判定する。
  4. 選ばれた面のローカル座標系（面法線 N・右方向 R・下方向 D）へ透視除算
     (perspective divide) して面内 UV（-1..1）を求める。
  5. 面内 UV をアトラス内の対応領域のピクセル座標に変換し、
     cv2.remap でバイリニアサンプリングする。
このアルゴリズムは numpy 配列演算で完全にベクトル化しており、
Python の二重ループは一切使用しない。

【側面 4 枚の割り当てについて】
アトラスの側面帯は「左から右へ連続して 1 周する」という制約だけを満たせば
よく、実際にどの立方体面（+X/-Z/-X/+Z）が左から何番目かという世界座標との
対応は本質的に自由である。本スクリプトでは、経度方向に 45° の位相オフセット
を掛けたうえで標準のキューブマップ面選択式を使うことで、
「アトラスの列境界（F の整数倍のx座標）」と「面の境界（隣接面との切り替え
位置）」がちょうど一致するようにしている（面の中心はその面の列の中央に来る）。
これにより、列境界をまたいでも方向ベクトルが連続に変化し、結果として
出力 equirect 画像も継ぎ目なく連続になる。上面・下面は単色でほぼ回転不変の
ため、回転の向き（ローカル UV の張り方）は問わない。

【使い方】
  python tools/cubemap_to_equirect.py input.png output.hdr \
      [--width 4096] [--face-size 1280] [--scale 1.0] \
      [--preview preview.png] [--layout '{"+X":[0,1], ...}']

  自己検証（既知パターンで正しさを assert する）:
  python tools/cubemap_to_equirect.py --selftest
"""

import argparse
import json
import sys

import cv2
import numpy as np

# ============================================================
# 定数定義（マジックナンバー排除）
# ============================================================

# アトラスのグリッド構成（列数・行数）。デフォルトレイアウトは
# 「横 4 列 × 縦 3 行」のグリッドの中に 6 面を配置する構成。
DEFAULT_GRID_COLS = 4
DEFAULT_GRID_ROWS = 3

# デフォルトの面レイアウト: 面名 -> (col, row)。
# 側面帯 (row=1) を左から順に +X, -Z, -X, +Z とし、
# 右列の上段/下段に +Y（上面）/-Y（下面）を置く。
# この割り当ての導出根拠は本ファイル冒頭のコメント参照。
DEFAULT_FACE_LAYOUT = {
    "+X": (0, 1),
    "-Z": (1, 1),
    "-X": (2, 1),
    "+Z": (3, 1),
    "+Y": (3, 0),
    "-Y": (3, 2),
}

# 側面 4 枚の経度方向の位相オフセット。
# アトラスの列境界と立方体面の境界（面の中心から ±45°）を一致させるために、
# 方向ベクトルを作る際の経度 phi に加算する。
FACE_PHASE_OFFSET_RAD = np.pi / 4.0  # 45°

# 出力 equirect のデフォルト解像度（高さは幅の半分＝正距円筒の標準比率 2:1）。
DEFAULT_OUTPUT_WIDTH = 4096
EQUIRECT_ASPECT_RATIO = 2.0  # width / height

# sRGB <-> linear 変換のしきい値・係数（IEC 61966-2-1 標準の sRGB EOTF）。
SRGB_LINEAR_THRESHOLD = 0.0031308  # リニア側の分岐点
SRGB_ENCODED_THRESHOLD = 0.04045   # sRGB エンコード側の分岐点
SRGB_LINEAR_SLOPE = 12.92
SRGB_GAMMA = 2.4
SRGB_OFFSET = 0.055
SRGB_OFFSET_SCALE = 1.055

# 8bit 画素値の最大値（0-255 <-> 0-1 の正規化に使用）。
UINT8_MAX = 255.0

# プレビュー PNG のデフォルト出力幅。
DEFAULT_PREVIEW_WIDTH = 1024

# 面内ローカル座標の透視除算で 0 除算を避けるための下限値。
# （選択されなかった面の式も配列全体に対して計算するため、
#  未使用の分岐でゼロ割り警告が出ないようにするための安全策）
MIN_AXIS_DIVISOR = 1e-9


# ============================================================
# 色空間変換
# ============================================================

def srgb_to_linear(c: np.ndarray) -> np.ndarray:
    """sRGB(0..1) の画素値配列を、標準の sRGB EOTF でリニア(0..1)に変換する。

    Args:
        c: 0..1 に正規化された sRGB 値の配列（任意形状）。
    Returns:
        同じ形状のリニア値配列。
    """
    low = c / SRGB_LINEAR_SLOPE
    high = ((c + SRGB_OFFSET) / SRGB_OFFSET_SCALE) ** SRGB_GAMMA
    return np.where(c <= SRGB_ENCODED_THRESHOLD, low, high)


def linear_to_srgb(c: np.ndarray) -> np.ndarray:
    """リニア(0..1，範囲外はクリップ)の画素値配列を sRGB(0..1) にエンコードする。

    Args:
        c: リニア値配列。
    Returns:
        同じ形状の sRGB エンコード済み配列（0..1 にクリップ済み）。
    """
    c = np.clip(c, 0.0, 1.0)
    low = c * SRGB_LINEAR_SLOPE
    high = SRGB_OFFSET_SCALE * np.power(c, 1.0 / SRGB_GAMMA) - SRGB_OFFSET
    return np.where(c <= SRGB_LINEAR_THRESHOLD, low, high)


# ============================================================
# キューブマップ <-> 正距円筒 変換の中核
# ============================================================

def _build_direction_vectors(width: int, height: int):
    """出力 equirect 画像の各ピクセルに対応する球面方向ベクトルを作る。

    - u (0..width-1) -> 経度 phi (0..2π)。u=0 が「側面帯の x=0」に対応する
      ように、位相オフセットを別途 phi に加算して x,z を求める
      （オフセットの理由は本ファイル冒頭コメント参照）。
    - v (0..height-1) -> 緯度 theta (0..π)。v=0 が天頂（+Y, 北極）。

    Returns:
        (x, y, z): それぞれ shape=(height, width) の float64 配列。
    """
    # ピクセル中心をサンプルするため +0.5 する。
    u = (np.arange(width, dtype=np.float64) + 0.5) / width
    v = (np.arange(height, dtype=np.float64) + 0.5) / height

    phi = u * 2.0 * np.pi          # 経度 0..2π
    theta = v[:, None] * np.pi     # 緯度 0..π （列ベクトル化のため縦に）

    phi_dir = phi[None, :] + FACE_PHASE_OFFSET_RAD  # 面境界とアトラス列境界を合わせる位相オフセット

    sin_theta = np.sin(theta)
    cos_theta = np.cos(theta)

    x = sin_theta * np.sin(phi_dir)
    z = sin_theta * np.cos(phi_dir)
    y = np.broadcast_to(cos_theta, (height, width))
    x = np.broadcast_to(x, (height, width))
    z = np.broadcast_to(z, (height, width))
    return x, y, z


def _face_uv(dir_component_r: np.ndarray, dir_component_d: np.ndarray,
             forward: np.ndarray) -> tuple:
    """1 つの面のローカル基底 (forward, right=R, down=D) に対して、
    透視除算した面内 UV（-1..1）を求める。

    Args:
        dir_component_r: 方向ベクトルと R（右方向ベクトル）の内積。
        dir_component_d: 方向ベクトルと D（下方向ベクトル）の内積。
        forward: 方向ベクトルと N（面法線）の内積（＝透視除算の分母。正の値）。
    Returns:
        (u_ndc, v_ndc): 面内座標（理想的には -1..1 の範囲）。
    """
    safe_forward = np.where(np.abs(forward) < MIN_AXIS_DIVISOR,
                             MIN_AXIS_DIVISOR, forward)
    u_ndc = dir_component_r / safe_forward
    v_ndc = dir_component_d / safe_forward
    return u_ndc, v_ndc


def build_equirect_sample_maps(width: int, height: int, face_size: int,
                                layout: dict):
    """equirect 出力画像の各ピクセルが、アトラス画像のどのピクセルを
    サンプルすべきかを表す remap 用マップ (mapx, mapy) を構築する。

    Args:
        width, height: 出力 equirect のサイズ。
        face_size: アトラス内 1 面の一辺のピクセル数 F。
        layout: 面名 -> (col, row) の辞書（アトラス内のタイル位置）。
    Returns:
        (mapx, mapy): shape=(height, width) の float32 配列。cv2.remap にそのまま渡せる。
    """
    x, y, z = _build_direction_vectors(width, height)

    abs_x, abs_y, abs_z = np.abs(x), np.abs(y), np.abs(z)

    # 排他的な面選択マスク（標準的なキューブマップの軸選択規則）。
    is_x_major = (abs_x >= abs_y) & (abs_x >= abs_z)
    is_y_major = (~is_x_major) & (abs_y >= abs_z)
    is_z_major = (~is_x_major) & (~is_y_major)

    mask_px = is_x_major & (x > 0)
    mask_nx = is_x_major & (x <= 0)
    mask_py = is_y_major & (y > 0)
    mask_ny = is_y_major & (y <= 0)
    mask_pz = is_z_major & (z > 0)
    mask_nz = is_z_major & (z <= 0)

    # 各面のローカル基底 (N=forward, R=right, D=down) に基づく UV 計算。
    # D は側面 4 枚すべてで (0,-1,0) に統一し、縦方向の向きをアトラス上で
    # 一貫させる（上下端がすべての側面で北=天頂 / 南=天底に対応するように）。
    # +Y / -Y 面は単色でほぼ回転不変のため向きは任意（ここでは x, z をそのまま使用）。
    u_ndc = np.zeros((height, width), dtype=np.float64)
    v_ndc = np.zeros((height, width), dtype=np.float64)

    # +X: N=(1,0,0), R=(0,0,-1), D=(0,-1,0)
    u, v = _face_uv(-z, -y, x)
    u_ndc = np.where(mask_px, u, u_ndc)
    v_ndc = np.where(mask_px, v, v_ndc)

    # -X: N=(-1,0,0), R=(0,0,1), D=(0,-1,0)
    u, v = _face_uv(z, -y, -x)
    u_ndc = np.where(mask_nx, u, u_ndc)
    v_ndc = np.where(mask_nx, v, v_ndc)

    # +Z: N=(0,0,1), R=(1,0,0), D=(0,-1,0)
    u, v = _face_uv(x, -y, z)
    u_ndc = np.where(mask_pz, u, u_ndc)
    v_ndc = np.where(mask_pz, v, v_ndc)

    # -Z: N=(0,0,-1), R=(-1,0,0), D=(0,-1,0)
    u, v = _face_uv(-x, -y, -z)
    u_ndc = np.where(mask_nz, u, u_ndc)
    v_ndc = np.where(mask_nz, v, v_ndc)

    # +Y: N=(0,1,0), R=(1,0,0), D=(0,0,1)  （回転は任意）
    u, v = _face_uv(x, z, y)
    u_ndc = np.where(mask_py, u, u_ndc)
    v_ndc = np.where(mask_py, v, v_ndc)

    # -Y: N=(0,-1,0), R=(1,0,0), D=(0,0,-1)  （回転は任意）
    u, v = _face_uv(x, -z, -y)
    u_ndc = np.where(mask_ny, u, u_ndc)
    v_ndc = np.where(mask_ny, v, v_ndc)

    # -1..1 -> 0..1 -> 面内ピクセル座標(0..F)
    face_px_u = (u_ndc + 1.0) * 0.5 * face_size
    face_px_v = (v_ndc + 1.0) * 0.5 * face_size

    # 面ごとにアトラス上のオフセットを加算して絶対ピクセル座標にする。
    offset_x = np.zeros((height, width), dtype=np.float64)
    offset_y = np.zeros((height, width), dtype=np.float64)
    face_masks = {
        "+X": mask_px, "-X": mask_nx,
        "+Y": mask_py, "-Y": mask_ny,
        "+Z": mask_pz, "-Z": mask_nz,
    }
    for face_name, mask in face_masks.items():
        col, row = layout[face_name]
        offset_x = np.where(mask, col * face_size, offset_x)
        offset_y = np.where(mask, row * face_size, offset_y)

    map_x = (face_px_u + offset_x).astype(np.float32)
    map_y = (face_px_v + offset_y).astype(np.float32)

    # 面境界の丸め誤差でアトラス範囲をわずかにはみ出すことがあるためクリップする。
    total_w = DEFAULT_GRID_COLS * face_size
    total_h = DEFAULT_GRID_ROWS * face_size
    np.clip(map_x, 0, total_w - 1, out=map_x)
    np.clip(map_y, 0, total_h - 1, out=map_y)

    return map_x, map_y


def convert_cubemap_to_equirect(atlas_linear_bgr: np.ndarray, out_width: int,
                                 out_height: int, face_size: int,
                                 layout: dict) -> np.ndarray:
    """アトラス画像（リニア BGR, float32）を equirect（リニア BGR, float32）に変換する。"""
    map_x, map_y = build_equirect_sample_maps(out_width, out_height, face_size, layout)
    result = cv2.remap(
        atlas_linear_bgr, map_x, map_y,
        interpolation=cv2.INTER_LINEAR,
        borderMode=cv2.BORDER_REPLICATE,
    )
    return result


# ============================================================
# 自己検証（既知パターンでの assert）
# ============================================================

def run_selftest():
    """既知の単色パターンで面配置・連続性が正しいことを検証する。"""
    face_size = 64
    grid_w = DEFAULT_GRID_COLS * face_size
    grid_h = DEFAULT_GRID_ROWS * face_size

    # BGR で色を定義（cv2 は BGR なので分かりやすいように名前を付ける）。
    color_bgr = {
        "+X": (0, 0, 255),    # 赤
        "-Z": (0, 255, 0),    # 緑
        "-X": (255, 0, 0),    # 青
        "+Z": (0, 255, 255),  # 黄
        "+Y": (255, 255, 0),  # シアン
        "-Y": (255, 0, 255),  # マゼンタ
    }

    atlas = np.full((grid_h, grid_w, 3), 255, dtype=np.uint8)  # 未使用領域は白のまま
    for face_name, (col, row) in DEFAULT_FACE_LAYOUT.items():
        y0, y1 = row * face_size, (row + 1) * face_size
        x0, x1 = col * face_size, (col + 1) * face_size
        atlas[y0:y1, x0:x1] = color_bgr[face_name]

    atlas_linear = srgb_to_linear(atlas.astype(np.float64) / UINT8_MAX)
    out_w, out_h = 256, 128
    equirect = convert_cubemap_to_equirect(
        atlas_linear.astype(np.float32), out_w, out_h, face_size, DEFAULT_FACE_LAYOUT
    )

    def sample(px, py):
        return equirect[py, px]

    def assert_close(actual, expected, label, tol=0.05):
        # linear 空間で比較（0..1）。sRGB 255 => linear 1.0 相当なので tol は緩めに。
        expected_lin = srgb_to_linear(np.array(expected, dtype=np.float64) / UINT8_MAX)
        diff = np.abs(actual.astype(np.float64) - expected_lin).max()
        assert diff < tol, f"selftest失敗: {label} 期待={expected_lin} 実測={actual} diff={diff}"

    # 天頂(北極, v=0付近)は +Y 色、天底(v=H-1付近)は -Y 色。
    top_row = out_h // 16
    bottom_row = out_h - 1 - out_h // 16
    for test_u in [0, out_w // 4, out_w // 2, (3 * out_w) // 4, out_w - 1]:
        assert_close(sample(test_u, top_row), color_bgr["+Y"], f"天頂 u={test_u}")
        assert_close(sample(test_u, bottom_row), color_bgr["-Y"], f"天底 u={test_u}")

    # 赤道付近(v=H/2)の各面中心での色を検証。
    # 面中心の経度 phi(度) = 45,135,225,315 (本ファイル冒頭コメントの導出参照)。
    equator_row = out_h // 2
    face_center_deg_to_name = {
        45: "+X", 135: "-Z", 225: "-X", 315: "+Z",
    }
    for deg, face_name in face_center_deg_to_name.items():
        u_px = int(round((deg / 360.0) * out_w))
        u_px = min(u_px, out_w - 1)
        assert_close(sample(u_px, equator_row), color_bgr[face_name],
                     f"赤道 面中心={face_name} (phi={deg}deg)")

    print("selftest OK: 天頂=+Y, 天底=-Y, 赤道90度区間ごとに +X/-Z/-X/+Z を確認しました。")


# ============================================================
# メイン処理
# ============================================================

def load_atlas_linear_bgr(png_path: str) -> np.ndarray:
    """PNG（sRGB, RGBA/ RGB）を読み込み、alpha を無視した BGR リニア float32 に変換する。"""
    img = cv2.imread(png_path, cv2.IMREAD_UNCHANGED)
    if img is None:
        raise FileNotFoundError(f"入力画像を読み込めません: {png_path}")
    if img.ndim == 2:
        img = cv2.cvtColor(img, cv2.COLOR_GRAY2BGR)
    bgr = img[:, :, :3]  # alpha は無視
    srgb01 = bgr.astype(np.float64) / UINT8_MAX
    linear = srgb_to_linear(srgb01)
    return linear.astype(np.float32)


def save_preview_png(equirect_linear_bgr: np.ndarray, preview_path: str,
                      preview_width: int):
    """equirect のリニア画像を sRGB 8bit PNG に変換して保存する（プレビュー用に縮小）。"""
    h, w = equirect_linear_bgr.shape[:2]
    preview_height = max(1, round(preview_width * h / w))
    resized = cv2.resize(equirect_linear_bgr, (preview_width, preview_height),
                          interpolation=cv2.INTER_AREA)
    srgb01 = linear_to_srgb(resized)
    srgb8 = np.round(srgb01 * UINT8_MAX).astype(np.uint8)
    cv2.imwrite(preview_path, srgb8)


def parse_layout_arg(layout_json: str) -> dict:
    """--layout の JSON 文字列を {面名: (col,row)} 辞書に変換する。"""
    raw = json.loads(layout_json)
    layout = {name: (int(pos[0]), int(pos[1])) for name, pos in raw.items()}
    required = set(DEFAULT_FACE_LAYOUT.keys())
    if set(layout.keys()) != required:
        raise ValueError(f"--layout には6面すべて({sorted(required)})の指定が必要です。")
    return layout


def main():
    parser = argparse.ArgumentParser(
        description="SEED 用: キューブマップアトラスPNG を equirectangular .hdr に変換する。"
    )
    parser.add_argument("input_png", nargs="?", help="入力アトラス PNG のパス")
    parser.add_argument("output_hdr", nargs="?", help="出力 equirectangular .hdr のパス")
    parser.add_argument("--width", type=int, default=DEFAULT_OUTPUT_WIDTH,
                         help=f"出力幅（既定 {DEFAULT_OUTPUT_WIDTH}）。高さは幅/2。")
    parser.add_argument("--face-size", type=int, default=None,
                         help="アトラス1面の一辺ピクセル数。省略時は入力画像幅/4。")
    parser.add_argument("--scale", type=float, default=1.0,
                         help="出力リニア値に乗算する露出スケール（既定 1.0）。")
    parser.add_argument("--preview", type=str, default=None,
                         help="sRGB 8bit プレビュー PNG の出力先パス（省略可）。")
    parser.add_argument("--preview-width", type=int, default=DEFAULT_PREVIEW_WIDTH,
                         help=f"プレビュー PNG の幅（既定 {DEFAULT_PREVIEW_WIDTH}）。")
    parser.add_argument("--layout", type=str, default=None,
                         help='面レイアウトを差し替えるJSON。例: \'{"+X":[0,1], ...}\'')
    parser.add_argument("--selftest", action="store_true",
                         help="既知パターンでの自己検証を実行して終了する。")
    args = parser.parse_args()

    if args.selftest:
        run_selftest()
        return

    if not args.input_png or not args.output_hdr:
        parser.error("input_png と output_hdr を指定してください（--selftest 以外の場合）。")

    layout = DEFAULT_FACE_LAYOUT
    if args.layout:
        layout = parse_layout_arg(args.layout)

    atlas_linear = load_atlas_linear_bgr(args.input_png)
    atlas_h, atlas_w = atlas_linear.shape[:2]
    face_size = args.face_size if args.face_size else atlas_w // DEFAULT_GRID_COLS

    out_w = args.width
    out_h = round(out_w / EQUIRECT_ASPECT_RATIO)

    equirect = convert_cubemap_to_equirect(atlas_linear, out_w, out_h, face_size, layout)
    equirect_scaled = equirect * np.float32(args.scale)

    ok = cv2.imwrite(args.output_hdr, equirect_scaled)
    if not ok:
        print(f"エラー: .hdr の書き出しに失敗しました: {args.output_hdr}", file=sys.stderr)
        sys.exit(1)
    print(f"書き出し完了: {args.output_hdr} ({out_w}x{out_h}, face_size={face_size}, scale={args.scale})")

    if args.preview:
        save_preview_png(equirect_scaled, args.preview, args.preview_width)
        print(f"プレビュー書き出し完了: {args.preview}")


if __name__ == "__main__":
    main()
