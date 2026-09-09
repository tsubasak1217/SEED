namespace SEED;

/// <summary>
/// 実行環境の判定。「今どういう立場で動いているか」をスクリプトから知るための API。
///
/// 主な用途は <b>デバッグ機能を配布版で無効化する</b> こと。
/// デバッグ表示・当たり判定の可視化・チートコマンドなどを
/// <see cref="IsDebugAllowed"/> で囲っておけば、パッケージ（assets.pak 同梱）の
/// 実行ファイルではそれらが自動的に無効になる。
///
/// <para><b>値は実行中に変化しない</b><br/>
/// いずれのプロパティも起動時に確定し、ゲーム実行中に変わることはない。
/// そのため初回アクセス時に 1 度だけランタイムへ問い合わせ、以降は保持した値を返す。
/// 毎フレーム参照しても FFI 呼び出しは発生しないので、条件式に直接書いてよい。
/// </para>
///
/// <example>
/// <code>
/// // デバッグ表示は開発中だけ
/// if (SEED.Application.IsDebugAllowed)
/// {
///     SEED.Debug.Log($"hp = {hp}");
/// }
/// </code>
/// </example>
/// </summary>
public static class Application
{
    // ------------------------------------------------------------
    //  キャッシュ
    //
    //  static フィールドの初期化子や static コンストラクタを使うと、
    //  「型に最初に触れたタイミング」で値を読みに行くことになる。これは
    //  ホスト API（ScriptHost.RegisterHostApi）の登録より前に走る可能性があり、
    //  その場合は常に false が焼き付いてしまう。
    //  そのため null 許容 bool による遅延キャッシュとし、
    //  「最初に実際へアクセスされた時点」で 1 度だけ問い合わせる。
    // ------------------------------------------------------------

    /// <summary>パッケージ実行かどうかのキャッシュ（null = 未取得）。</summary>
    private static bool? _isPackaged;

    /// <summary>エディタからの Play かどうかのキャッシュ（null = 未取得）。</summary>
    private static bool? _isEditorPlay;

    /// <summary>
    /// パッケージ実行（assets.pak を同梱した配布版として動いている）なら true。
    ///
    /// エディタからの実行（Edit / Play どちらも）ではアセットを実ファイルから読むため false。
    /// </summary>
    public static bool IsPackaged
        => _isPackaged ??= ScriptHost.AppEnv(ScriptHost.AppEnvKindPackaged);

    /// <summary>
    /// エディタから Play したゲーム実行中なら true。
    ///
    /// エディタの編集中ビュー（Edit モード）や、配布された実行ファイルの
    /// 単体起動では false になる。
    /// </summary>
    public static bool IsEditorPlay
        => _isEditorPlay ??= ScriptHost.AppEnv(ScriptHost.AppEnvKindEditorPlay);

    /// <summary>
    /// デバッグ機能を有効にしてよい実行かどうか（現在の定義は <c>!IsPackaged</c>）。
    ///
    /// <para><b>デバッグ機能の共通ゲート</b><br/>
    /// 「開発中だけ動かしたい処理」は個別に <see cref="IsPackaged"/> や
    /// <see cref="IsEditorPlay"/> を見るのではなく、必ずこのプロパティで分岐すること。
    /// 判定方針を変えたくなったとき（例: 配布版でも隠しコマンドで有効化する、
    /// エディタ Play のときだけに絞る）に、書き換えるのはここ 1 箇所で済む。
    /// </para>
    /// </summary>
    public static bool IsDebugAllowed => !IsPackaged;
}
