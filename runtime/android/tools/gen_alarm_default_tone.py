# ============================================================
#  gen_alarm_default_tone.py — 目覚ましの既定の音（res/raw/seed_alarm_default.wav）を作る（W1-4a）
#
#  :seed_platform の RingService は、予約の音源（sound_path）が空・読めないときにこの音で鳴らす（無音にしない。
#  docs/app_platform_roadmap.md §2.2「鳴動の方針」）。APK の中の res/raw なので、再起動の後・ロック解除の前（Direct Boot）でも読める。
#
#  【音】「ピピピピッ・（休み）」の 2 回で 1 ループ（MediaPlayer がループ再生する）。
#    16 kHz・モノラル・16 bit の PCM WAV（res/raw の .wav は APK の中で圧縮されないので openRawResourceFd で直接渡せる）。
#    ビープは 1.6 kHz の正弦波に 3 倍音（4.8 kHz）を足した少し硬い音（スマートフォンの小さなスピーカーでも聞き取りやすい帯域）。
#    ビープの頭と尻に短い余弦のフェードを付け、プチッという音を出さない。ピークは -3 dBFS（通常のアラームの大きさ。
#    実際の大きさは端末のアラームの音量と、予約の force_volume・fade_in_seconds で決まる）。
#    ループの先頭がビープで始まり、末尾が休みで終わるので、つなぎ目で音が途切れて聞こえない。
#  【大きさ】2.22 秒（(0.11 × 4 + 0.09 × 3 + 0.40) × 2）× 16000 Hz × 2 バイト ≒ 71 KB（100 KB 未満。超えたら止める）。
#
#  使い方（リポジトリの直下から。出力先を省くと runtime/android/app/src/main/res/raw/seed_alarm_default.wav）:
#    python runtime/android/tools/gen_alarm_default_tone.py [出力先]
#  同じ入力からは同じバイト列になる（乱数を使わない）。音を変えたら生成し直して、WAV もリポジトリに入れる。
# ============================================================

import math
import os
import struct
import sys
import wave

# ── 形式 ──
SAMPLE_RATE_HZ = 16_000
CHANNELS = 1
SAMPLE_WIDTH_BYTES = 2
FULL_SCALE = 32_767

# ── 音の中身 ──
FUNDAMENTAL_HZ = 1_600.0          # ビープの基本の高さ
HARMONIC_MULTIPLE = 3             # 足す倍音（奇数倍音で少し硬い音にする）
HARMONIC_LEVEL = 0.25             # 倍音の大きさ（基本に対する比）
PEAK_DBFS = -3.0                  # ピークの大きさ
FADE_SECONDS = 0.005              # ビープの頭と尻のフェード

# ── 並び（1 ループ = 「ピピピピッ・休み」× 2）──
BEEP_SECONDS = 0.11               # 1 つのビープの長さ
GAP_SECONDS = 0.09                # ビープの間の休み
BEEPS_PER_BURST = 4               # 1 まとまりのビープの数
REST_SECONDS = 0.40               # まとまりの後の休み
BURSTS_PER_LOOP = 2               # 1 ループのまとまりの数

# ── 出力の上限（res/raw に置く大きさの約束）──
MAX_FILE_BYTES = 100 * 1024

DEFAULT_OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                              "..", "app", "src", "main", "res", "raw", "seed_alarm_default.wav")


def seconds_to_samples(seconds: float) -> int:
    """秒を標本の数にする（四捨五入）。"""
    return int(round(seconds * SAMPLE_RATE_HZ))


def beep_samples(length: int, amplitude: float) -> list[float]:
    """ビープ 1 つ（基本＋倍音、頭と尻に余弦のフェード）。"""
    fade = seconds_to_samples(FADE_SECONDS)
    samples = []
    for i in range(length):
        t = i / SAMPLE_RATE_HZ
        value = math.sin(2.0 * math.pi * FUNDAMENTAL_HZ * t)
        value += HARMONIC_LEVEL * math.sin(2.0 * math.pi * FUNDAMENTAL_HZ * HARMONIC_MULTIPLE * t)
        # 余弦のフェード（0 → 1 → 0）
        edge = min(i, length - 1 - i)
        gain = 1.0 if edge >= fade else 0.5 - 0.5 * math.cos(math.pi * edge / fade)
        samples.append(amplitude * gain * value)
    return samples


def build_loop() -> list[int]:
    """1 ループぶんの 16 bit の標本を作る（ピークが PEAK_DBFS になるよう大きさをそろえる）。"""
    # 大きさ 1 で作ったビープの実際のピーク（基本と倍音は同時には最大にならない）で割って、ピークを PEAK_DBFS にそろえる
    unit_beep = beep_samples(seconds_to_samples(BEEP_SECONDS), 1.0)
    raw_peak = max(abs(value) for value in unit_beep)
    amplitude = FULL_SCALE * (10.0 ** (PEAK_DBFS / 20.0)) / raw_peak
    beep = beep_samples(seconds_to_samples(BEEP_SECONDS), amplitude)
    gap = [0.0] * seconds_to_samples(GAP_SECONDS)
    rest = [0.0] * seconds_to_samples(REST_SECONDS)

    samples: list[float] = []
    for _burst in range(BURSTS_PER_LOOP):
        for index in range(BEEPS_PER_BURST):
            samples.extend(beep)
            if index < BEEPS_PER_BURST - 1:
                samples.extend(gap)
        samples.extend(rest)
    return [max(-FULL_SCALE, min(FULL_SCALE, int(round(value)))) for value in samples]


def main() -> None:
    output = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUTPUT)
    frames = build_loop()
    os.makedirs(os.path.dirname(output), exist_ok=True)
    with wave.open(output, "wb") as out:
        out.setnchannels(CHANNELS)
        out.setsampwidth(SAMPLE_WIDTH_BYTES)
        out.setframerate(SAMPLE_RATE_HZ)
        out.writeframes(b"".join(struct.pack("<h", value) for value in frames))
    size = os.path.getsize(output)
    peak = max(abs(value) for value in frames)
    print(f"wrote {output}: {len(frames)} frames ({len(frames) / SAMPLE_RATE_HZ:.2f} s), "
          f"{size} bytes, peak {peak} ({20.0 * math.log10(peak / FULL_SCALE):.2f} dBFS)")
    if size >= MAX_FILE_BYTES:
        raise SystemExit(f"大きすぎます（{size} バイト。上限 {MAX_FILE_BYTES} バイト）")


if __name__ == "__main__":
    main()
