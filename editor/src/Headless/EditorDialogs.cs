using System.Windows;

namespace SEEDEditor.Headless;

/// <summary>
/// モーダルダイアログ（<see cref="MessageBox"/>）の単一窓口。
///
/// <para>
/// ヘッドレス起動時にモーダルを出すと、UI スレッドがユーザー操作を待って永久に止まり、
/// AI からの HTTP ブリッジ呼び出しが全部タイムアウトする（誰も OK を押せない）。
/// そこでヘッドレス時は「表示せずログへ流し、既定の応答を返す」に置き換える。
/// </para>
///
/// <para>
/// ヘッドレス時の既定応答は <b>常に「破壊的でない側」</b>を返す:
/// </para>
/// <list type="bullet">
///   <item>OK のみ … <see cref="MessageBoxResult.OK"/>（通知なので押したのと同じ）</item>
///   <item>YesNo / YesNoCancel / OKCancel … <see cref="MessageBoxResult.No"/> または
///         <see cref="MessageBoxResult.Cancel"/>（「保存しますか？」等に勝手に Yes と答えない）</item>
/// </list>
///
/// <para>
/// 呼び出し側は戻り値の扱いを変えなくてよい。通常起動では従来どおり
/// <see cref="MessageBox.Show(string,string,MessageBoxButton,MessageBoxImage)"/> をそのまま呼ぶ。
/// </para>
/// </summary>
internal static class EditorDialogs
{
    /// <summary>ヘッドレス時にログへ付ける接頭辞（grep しやすくするため）。</summary>
    private const string LOG_PREFIX = "[ヘッドレス:ダイアログ抑止]";

    /// <summary>
    /// メッセージボックスを表示する（ヘッドレス時はログ出力のみ）。
    /// </summary>
    /// <param name="message">本文。</param>
    /// <param name="caption">タイトル。</param>
    /// <param name="button">ボタン構成。既定は OK のみ。</param>
    /// <param name="image">アイコン。既定はなし。</param>
    /// <returns>ユーザーの選択。ヘッドレス時は「破壊的でない側」の既定値。</returns>
    public static MessageBoxResult Show(
        string message,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        if (!EditorStartupOptions.IsHeadless)
        {
            return MessageBox.Show(message, caption, button, image);
        }

        // 改行を潰して 1 行のログにする（ログは行単位で読まれるため）。
        var flat = message.Replace("\r\n", " / ").Replace("\n", " / ");
        var result = HeadlessDefault(button);
        EditorLog.Write($"{LOG_PREFIX} {caption}: {flat}  → 既定応答={result}");
        return result;
    }

    /// <summary>
    /// ボタン構成ごとの、ヘッドレス時の既定応答を返す。
    /// 「勝手に破壊的な操作を承諾しない」を原則にする。
    /// </summary>
    private static MessageBoxResult HeadlessDefault(MessageBoxButton button) => button switch
    {
        MessageBoxButton.OK          => MessageBoxResult.OK,
        MessageBoxButton.OKCancel    => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo       => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _                            => MessageBoxResult.None,
    };
}
