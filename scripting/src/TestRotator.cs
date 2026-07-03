using System;

namespace SEED.Scripting;

/// <summary>
/// スクリプティングシステムの動作確認用サンプル。
/// interval 秒ごとに経過時間をコンソールに出力する。
/// [SerializeField] の動作確認も兼ねる（インスペクタから interval を変更できる）。
/// </summary>
public class TestRotator : ScriptComponent
{
    /// <summary>ログ出力間隔（秒）。エディタのインスペクタから編集できる。</summary>
    [SerializeField(Label = "出力間隔(秒)", Tooltip = "ログを出力する間隔")]
    private float interval = 1.0f;

    private float _elapsed;

    public override void Update(ref NativeFrameContext ctx)
    {
        _elapsed += ctx.DeltaTime;
        if (_elapsed >= interval)
        {
            Console.WriteLine($"[TestRotator] anim_time={ctx.AnimTime:F2}s");
            _elapsed = 0f;
        }
    }
}
