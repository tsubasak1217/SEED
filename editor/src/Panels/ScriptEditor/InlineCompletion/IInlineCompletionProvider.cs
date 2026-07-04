using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// インライン補完の候補文字列を生成するバックエンドの共通インターフェース。
/// ローカル LLM（FIM）版・Groq（クラウド）版など複数実装を切り替えられるようにする。
/// </summary>
public interface IInlineCompletionProvider
{
    /// <summary>現在リクエストを送れる状態か（サーバー起動済み・APIキー設定済み等）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// カーソル前後のテキストから補完候補を取得する。
    /// 利用不可・エラー・空応答のときは null を返す。
    /// </summary>
    /// <param name="prefix">カーソルより前の全テキスト</param>
    /// <param name="suffix">カーソルより後の全テキスト</param>
    /// <param name="ct">キャンセルトークン（新しい入力が来たら破棄する）</param>
    Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken ct);
}
