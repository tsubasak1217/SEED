# W1-0 スパイクの鳴動音を作る（一度だけ実行）。
# USAGE_ALARM はアラーム音量で鳴り、端末の音量は変えない約束なので、音源そのものを極端に小さくする。
# 1 秒のループ: 150 ms の 880 Hz（-60 dBFS、前後 10 ms のフェード）＋ 850 ms の無音。
import math
import struct
import sys
import wave

SAMPLE_RATE = 22050
LOOP_SECONDS = 1.0
TONE_SECONDS = 0.15
FADE_SECONDS = 0.01
FREQUENCY_HZ = 880.0
LEVEL_DBFS = -60.0
FULL_SCALE = 32767


def main(path: str) -> None:
    amplitude = FULL_SCALE * (10.0 ** (LEVEL_DBFS / 20.0))
    total = int(SAMPLE_RATE * LOOP_SECONDS)
    tone = int(SAMPLE_RATE * TONE_SECONDS)
    fade = int(SAMPLE_RATE * FADE_SECONDS)
    frames = bytearray()
    for i in range(total):
        value = 0.0
        if i < tone:
            gain = min(1.0, i / fade, (tone - i) / fade)
            value = amplitude * gain * math.sin(2.0 * math.pi * FREQUENCY_HZ * i / SAMPLE_RATE)
        frames += struct.pack("<h", int(round(value)))
    with wave.open(path, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(SAMPLE_RATE)
        out.writeframes(bytes(frames))
    print(f"wrote {path}: peak={amplitude:.1f} ({LEVEL_DBFS} dBFS), {total} frames")


if __name__ == "__main__":
    main(sys.argv[1])
