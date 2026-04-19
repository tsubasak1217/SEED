using System;

namespace SEED.Scripting;

/// <summary>
/// スクリプティングシステムの動作確認用サンプル。
/// Update ごとに経過時間をコンソールに出力する。
/// </summary>
public class TestRotator : ScriptComponent
{
    private float _elapsed;

    public override void Update(ref NativeFrameContext ctx)
    {
        _elapsed += ctx.DeltaTime;
        if (_elapsed >= 1.0f)
        {
            Console.WriteLine($"[TestRotator] anim_time={ctx.AnimTime:F2}s");
            _elapsed = 0f;
        }
    }
}
