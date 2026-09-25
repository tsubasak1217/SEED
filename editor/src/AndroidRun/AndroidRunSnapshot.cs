// ============================================================
//  AndroidRunSnapshot.cs — Android の実行の「いま」の写し（UI が読む値。不変）
//
//  AndroidRunController がロックの中で状態機械から作り、UI スレッド（プレイバー・進捗の表示）はこれだけを読む。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

namespace SEEDEditor.AndroidRun;

/// <summary>Android の実行のいまの写し。</summary>
public sealed record AndroidRunSnapshot
{
    /// <summary>動いていないときの写し。</summary>
    public static readonly AndroidRunSnapshot Idle = new();

    /// <summary>状態。</summary>
    public AndroidRunPhase Phase { get; init; } = AndroidRunPhase.Idle;

    /// <summary>実行先の表示名（セレクタの文言。例「Pixel_6a（実機）」）。</summary>
    public string? TargetText { get; init; }

    /// <summary>端末のシリアル。</summary>
    public string? Serial { get; init; }

    /// <summary>アプリ ID（準備が終わると分かる）。</summary>
    public string? ApplicationId { get; init; }

    /// <summary>いまの工程の表示名（工程が始まると分かる）。</summary>
    public string? StepTitle { get; init; }

    /// <summary>いまの工程が何番目か（1 始まり。準備中は 0）。</summary>
    public int StepIndex { get; init; }

    /// <summary>計画に載った工程の数（準備が終わると分かる。それまでは 0）。</summary>
    public int StepCount { get; init; }

    /// <summary>全体の進み具合（0〜1。工程を終えるたびに進む）。</summary>
    public double Fraction { get; init; }

    /// <summary>
    /// 準備の途中の詳細（例「エミュレータの起動を待っています（45 秒）」。準備の工程の進み具合のイベントの説明。段階C-3）。
    /// 工程が始まる前だけ進捗の表示に使う。
    /// </summary>
    public string? PrepareDetail { get; init; }

    /// <summary>止める理由（止めていなければ None）。</summary>
    public AndroidRunStopReason StopReason { get; init; }

    /// <summary>動いているか（Idle 以外）。</summary>
    public bool IsActive => Phase != AndroidRunPhase.Idle;
}
