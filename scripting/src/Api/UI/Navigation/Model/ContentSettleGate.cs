namespace SEED.UI;

// ============================================================
//  ContentSettleGate.cs — 入ってくる中身が落ち着いたか（遷移の時計の直し。2026-09-30。純粋な計算。
//                         docs/ui_navigation.md §2「出入りの時計」）
//
//  画面のプレハブはできあがった（CanvasTransform が付いた）後にスクリプトが始まり、画面のスクリプト（UiScreen）が OnScreenEnter で
//  値を受けて、その後の Update で中身（文字・ホイールの行など）を作る。この間のフレームは重いので、出入りの動きはここが済むまで待つ:
//    落ち着いた = 「Enter を届けて（届ける相手がいないと分かって）から、SettleFrames 回のフレームを描き終えた」
//                 または「できあがってから MaxWaitFrames 回のフレームを待っても Enter を届けられなかった」（画面のスクリプトの無いプレハブ）
//  使い方（毎フレームの順が大事）: 中身ができあがった後の毎フレーム、Enter を届けるより前に Frame() を 1 回呼ぶ →
//  Enter を届けたら MarkEntered() → IsSettled を見る。Enter を届けたフレームは数えないので、そのフレームの描画
//  （Enter の中の変更と、画面のスクリプトの最初の Update）が済んだ次のフレームから落ち着く。
//  使う所: ScreenStack（入ってくる画面）・TopSheet（覆いの中身）。
// ============================================================

/// <summary>入ってくる中身（画面・覆いの中身）が落ち着いたか。</summary>
public struct ContentSettleGate
{
    /// <summary>Enter を届けた後、落ち着いたとみなすまでに描き終えるフレームの数（Enter を届けたフレーム自身は数えない）。</summary>
    public const int SettleFrames = 1;

    /// <summary>
    /// できあがってから Enter を待つフレームの上限（画面のスクリプト〈UiScreen〉の付いていないプレハブを待ち続けないため）。
    /// PC ではできあがってから 1〜2 フレームで Enter が届いた（2026-09-30 の Wake or Pay の nav,probe）ので、余裕を持たせた値。
    /// </summary>
    public const int MaxWaitFrames = 10;

    /// <summary>できあがってから数えたフレーム（<see cref="MaxWaitFrames"/> で止める）。</summary>
    private int _builtFrames;
    /// <summary>Enter を届けてから数えたフレーム（<see cref="SettleFrames"/> で止める）。</summary>
    private int _enteredFrames;
    /// <summary>Enter を届けたか（届ける相手がいないと分かったときも true）。</summary>
    private bool _entered;

    /// <summary>落ち着いたか（出入りの時計を進めてよいか）。</summary>
    public readonly bool IsSettled => (_entered && _enteredFrames >= SettleFrames) || _builtFrames >= MaxWaitFrames;

    /// <summary>Enter を届けたか（届ける相手がいないと分かったときも true）。</summary>
    public readonly bool Entered => _entered;

    /// <summary>中身ができあがった後の 1 フレーム（毎フレーム、Enter を届けるより前に 1 回呼ぶ）。</summary>
    public void Frame()
    {
        // 上限で止める（長く生きる画面で数が溢れない。比べるのは上限までなので、それより先の値は要らない）
        if (_builtFrames < MaxWaitFrames) _builtFrames++;
        // Enter を届けた後のフレーム（届けたフレームは MarkEntered が 0 にした後なので、次のフレームのこの呼び出しから 1）
        if (_entered && _enteredFrames < SettleFrames) _enteredFrames++;
    }

    /// <summary>Enter を届けた（または届ける相手がいない＝中身を作れなかった）。このフレームは数えない。2 回目からは何もしない。</summary>
    public void MarkEntered()
    {
        if (_entered) return;
        _entered = true;
        _enteredFrames = 0;
    }
}
