using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「子を切り抜く（CanvasClipComponent）」実装（W2-1a）。
///
/// ■ 何をするコンポーネントか
///   2D キャンバスのノードに付けると、そのノードのレイアウトの矩形（CanvasComponent があれば
///   キャンバス領域、無ければ最初の有効な Sprite の矩形）で子孫を切り抜く。はみ出した部分は
///   描かれず、ポインタイベントも当たらない。ノード自身の Sprite（背景の板）は切らない。
///
/// ■ 欄
///   有効フラグと、形（矩形・角丸・楕円・スプライトの形に合わせる。W2-4）・角丸の半径。有効フラグの変更時は
///   <c>SET_CANVAS_CLIP_FIELD:{actor},{slot},enabled,{true|false}</c> を送る（Undo はランタイム側の共通機構）。
///   スロットの見出しの有効・無効とは別の旗で、両方が有効のときだけ切り抜く
///   （この旗はスクリプトの <c>CanvasClip.Enabled</c> から切り替えられる）。
/// </summary>
public partial class InspectorPanel
{
    /// <summary>CanvasClipComponent の ACTOR_COMPONENTS "type" 文字列。</summary>
    private const string CanvasClipComponentType = "CanvasClipComponent";

    /// <summary>有効フラグの既定値（Rust 側 CanvasClipComponentData の既定と一致させること）。</summary>
    private const bool CanvasClipEnabledDefault = true;

    /// <summary>説明文の文字色（他のコンポーネントの補足文と同じ灰色）。</summary>
    private static readonly Color CanvasClipHintColor = Color.FromRgb(0x66, 0x66, 0x66);

    /// <summary>説明文の文字の大きさ（他のコンポーネントの補足文と同じ）。</summary>
    private const double CanvasClipHintFontSize = 10;

    /// <summary>
    /// CanvasClipComponent のインスペクター UI を構築して返す。
    /// </summary>
    private UIElement BuildCanvasClipSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_CANVAS_CLIP_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        var section = BuildSection("切り抜き");
        var body = (StackPanel)section.Child;

        // 有効フラグ（スクリプトの CanvasClip.Enabled と同じ旗）
        body.Children.Add(BuildCheckRow("子を切り抜く", info.CanvasClipEnabled,
            v => SendField("enabled", v ? "true" : "false")));

        // 切り抜きの形（W2-4。角丸は RoundedRect のときだけ出す＝関係のない欄は隠す）
        AddCanvasClipShapeRows(body, info, SendField);

        body.Children.Add(new TextBlock
        {
            Text = "このノードのキャンバス領域（CanvasComponent が無ければ最初の Sprite の矩形）から"
                 + "はみ出した子孫を描かず、押せなくします。このノード自身の Sprite は切りません。"
                 + "入れ子にすると外側の枠との重なりで切ります。回転したノードは外接矩形で切ります。",
            Foreground = new SolidColorBrush(CanvasClipHintColor),
            FontSize = CanvasClipHintFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 2),
        });

        sp.Children.Add(section);
        return sp;
    }
}
