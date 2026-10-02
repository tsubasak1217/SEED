namespace SEED.Localization;

// ============================================================
//  ILocalizedTarget.cs — 「キーを受けて自分の文字を更新する」当てる先
//
//  SEED.UI の部品（Button・Toggle・Checkbox・SegmentedControl…）に手を入れずに多言語の文字を当てるため、部品の外に
//  この形の小さな当てる先（Targets/ の TextLabelTarget・ButtonLabelTarget・ChildLabelTarget・SelectionLabelTarget）を置き、
//  LocalizedLabel が 1 か所の表（LocalizedTargetTable）で選ぶ。当てる先は公開の API だけを使う（部品の振る舞いを変えない）。
//  スクリプトからも直接使える（例 new TextLabelTarget(text).Apply(new LocalizedRequest("menu.start"))）。
// ============================================================

/// <summary>キーを受けて自分の文字を更新する当てる先。</summary>
public interface ILocalizedTarget
{
    /// <summary>当てる先の種類の名前（表の名前。ログ・診断用）。</summary>
    string KindName { get; }

    /// <summary>当てる先がまだ生きているか（アクタ・部品が破棄されたら false）。</summary>
    bool IsAlive { get; }

    /// <summary>今の言語の文字を当てる。</summary>
    /// <param name="request">引き方（キー・差し込み・数）。</param>
    /// <returns>当てられたら true（当てる文字の欄が無ければ false）。</returns>
    bool Apply(LocalizedRequest request);
}
