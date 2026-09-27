// ============================================================
//  GravityFilter.java — 加速度から重力を見積もって引く一次の低域通過（TYPE_LINEAR_ACCELERATION の無い端末の代わり。W1-8）
//
//  developer.android.com「Motion sensors」の例（重力 = α·重力 + (1−α)·加速度、重力を除いた加速度 = 加速度 − 重力。
//  α = t / (t + dT)、t は低域通過の時定数・dT は標本の間隔）と同じ式。例は α を 0.8 に固定しているが、頻度はスクリプトが決め、
//  実際の間隔も揺れるので、標本の時刻（SensorEvent.timestamp）の差から毎回 α を計算する。
//
//  【時定数 TIME_CONSTANT_SECONDS = 0.25 秒】の理由: 振る動き（2〜5 Hz）を重力の見積もりに取り込まないため、遮断の周波数
//  1/(2π·t) ≈ 0.64 Hz を振りより十分低くする（2 Hz の振りで見積もりに漏れるのは約 3 割）。一方で向きを変えた後の見積もりの
//  追従（3t ≈ 0.75 秒）は遅くなり、その間は重力の差が加速度として見える（振りとして数えられうる。docs/android.md §25.16）。
//  【見積もりの始め方】最初の標本（と時刻が戻ったとき）はその値を重力の見積もりにして 0 を出す（0 から始めると登録の直後に
//  約 9.8 m/s² の偽の振りが出る）。間が大きく空いたときは式が α ≈ 0 になり、自然に今の値へ寄る。
//  1 つの登録（FeedListener＝センサーのスレッド）だけが使うので同期しない。登録し直すたびに新しいものを作る。
// ============================================================

package com.seedengine.runtime.platform.sensor;

/**
 * 重力の見積もりと引き算（センサーのスレッド専用）。
 */
final class GravityFilter {

    /** 低域通過の時定数（秒）。理由はファイルの先頭。 */
    static final float TIME_CONSTANT_SECONDS = 0.25f;

    /** 1 秒のナノ秒（SensorEvent.timestamp の差を秒にする）。 */
    private static final double NANOS_PER_SECOND = 1_000_000_000.0;

    /** 成分の数（x, y, z）。 */
    private static final int AXES = 3;

    /** 重力の見積もり（m/s²。端末の座標系）。 */
    private final float[] gravity = new float[AXES];

    /** 見積もりを始めたか（最初の標本で true）。 */
    private boolean primed;

    /** 前の標本の時刻（ナノ秒。SensorEvent.timestamp）。 */
    private long lastTimestampNanos;

    /**
     * 加速度の標本から重力を引く。
     *
     * @param acceleration   加速度（SensorEvent.values。先頭の 3 つ〈x, y, z〉を使う。重力を含む）
     * @param timestampNanos 標本の時刻（SensorEvent.timestamp。ナノ秒）
     * @param out            重力を除いた加速度の書き先（長さ 3 以上）
     */
    void removeGravity(float[] acceleration, long timestampNanos, float[] out) {
        long elapsedNanos = timestampNanos - lastTimestampNanos;
        if (!primed || elapsedNanos < 0) {
            // 最初の標本（と時刻が戻ったとき）: この値を重力の見積もりにして、重力を除いた加速度は 0 とする
            System.arraycopy(acceleration, 0, gravity, 0, AXES);
            primed = true;
            lastTimestampNanos = timestampNanos;
            for (int axis = 0; axis < AXES; axis++) {
                out[axis] = 0f;
            }
            return;
        }
        // α = t / (t + dT)。同じ時刻の標本（dT = 0）は α = 1 で見積もりを動かさない
        float elapsedSeconds = (float) (elapsedNanos / NANOS_PER_SECOND);
        float alpha = TIME_CONSTANT_SECONDS / (TIME_CONSTANT_SECONDS + elapsedSeconds);
        for (int axis = 0; axis < AXES; axis++) {
            gravity[axis] = alpha * gravity[axis] + (1f - alpha) * acceleration[axis];
            out[axis] = acceleration[axis] - gravity[axis];
        }
        lastTimestampNanos = timestampNanos;
    }
}
