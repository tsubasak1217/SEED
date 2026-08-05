using System;
using System.Windows;
using System.Windows.Input;

namespace SEEDEditor.Panels;

/// <summary>
/// 「アクタ／コンポーネントを参照するインスペクタフィールド」から
/// Hierarchy パネルの該当アクタへジャンプするための共通フック。
///
/// ファイル参照フィールド（<see cref="FileRefBuilder"/>）の
/// RevealInProjectRequested と同じ考え方の静的フックで、フィールド生成側が
/// パネル参照を持たなくてもジャンプ要求を出せるようにする。
/// 結線は MainWindow の初期化時に 1 度だけ行う。
///
/// ジャンプ先では「スクロール＋一定時間の黄色ハイライト」だけを行い、
/// 選択状態は変更しない（選択変更イベントを発火させない）。
/// </summary>
internal static class ActorRefJump
{
    /// <summary>ダブルクリックと判定するクリック回数（WPF の ClickCount 比較用）。</summary>
    private const int DoubleClickCount = 2;

    /// <summary>
    /// アクタ名でのジャンプ要求フック。MainWindow が
    /// HierarchyPanel.RevealActorByName へ接続する。未接続なら何も起きない。
    /// </summary>
    public static Action<string>? RevealActorByNameRequested;

    /// <summary>
    /// DFS ID でのジャンプ要求フック。MainWindow が
    /// HierarchyPanel.RevealActor へ接続する。未接続なら何も起きない。
    /// アクタ参照を ID で保持するフィールドを将来追加したときの入口。
    /// </summary>
    public static Action<int>? RevealActorByDfsIdRequested;

    /// <summary>
    /// 「その名前のアクタがシーンに存在するか」を問い合わせるフック。
    /// MainWindow が HierarchyPanel.ActorExistsByName へ接続する。
    ///
    /// 参照フィールドは値をアクタ名の文字列で持つため、参照先が削除されると
    /// 表示だけが残る。参照ピッカー（ReferencePicker）はこのフックで存在を確認し、
    /// 見つからないときに警告表示へ切り替える。
    /// 未接続（null）のときは「判定不能」として警告を出さない。
    /// </summary>
    public static Func<string, bool>? ActorExistsByName;

    /// <summary>
    /// 指定要素のダブルクリックで「アクタ名によるジャンプ」を行うよう配線する。
    /// アクタ参照を表示している行（ラベル・ドロップゾーン等）に付ける。
    /// </summary>
    /// <param name="element">ダブルクリックを受け取る要素（Background が必要）。</param>
    /// <param name="actorNameProvider">
    /// 現在の参照先アクタ名を返す関数。参照未設定なら null/空を返すこと
    /// （その場合ダブルクリックは何もしない）。値は毎回評価されるため、
    /// 参照が張り替わっても最新の値でジャンプできる。
    /// </param>
    public static void AttachDoubleClickReveal(FrameworkElement element, Func<string?> actorNameProvider)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != DoubleClickCount) return;
            var name = actorNameProvider();
            if (string.IsNullOrEmpty(name)) return;
            RevealActorByNameRequested?.Invoke(name);
            e.Handled = true;
        };
    }
}
