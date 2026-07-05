namespace SEED;

/// <summary>
/// フレーム時間へのアクセス。値は毎フレーム、ライフサイクル呼び出しの直前に
/// エンジン（ScriptBridge）が更新する。スクリプトからは読み取り専用。
///
/// <c>ctx.DeltaTime</c> と同じ値を、ctx を引き回さずどこからでも
/// <c>Time.DeltaTime</c> で参照できるようにするためのもの。
/// </summary>
public static class Time
{
    /// <summary>前フレームからの経過秒。</summary>
    public static float DeltaTime { get; private set; }

    /// <summary>ゲーム内の累計時間（秒）。</summary>
    public static float ElapsedTime { get; private set; }

    /// <summary>
    /// エンジン内部用: 現在フレームの時間を反映する。
    /// ScriptBridge が各ライフサイクル呼び出しの直前に呼ぶ。ユーザーは使わない。
    /// </summary>
    internal static void Sync(float deltaTime, float elapsedTime)
    {
        DeltaTime   = deltaTime;
        ElapsedTime = elapsedTime;
    }
}
