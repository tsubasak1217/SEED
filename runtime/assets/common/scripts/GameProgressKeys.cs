// ============================================================================
//  GameProgressKeys.cs
//  ゲーム進行フラグのセーブキーを一元管理する定数クラス。
// ============================================================================

/// <summary>
/// SaveData に保存する「ゲーム進行フラグ」のキー名をまとめた静的クラス。
///
/// 【なぜ定数クラスに切り出すか】
/// セーブキーは「書く側（チュートリアル終了時）」と「読む側（タイトルの分岐）」の
/// 2 か所以上から参照される文字列であり、直書きするとタイプミスが
/// コンパイルエラーにならず「フラグが立たない／読めない」という
/// 発見しづらい不具合になる。定数へ集約して 1 か所でのみ定義する。
///
/// 【使い方】
/// <code>
/// SEED.SaveData.SetBool(GameProgressKeys.TutorialDone, true);
/// bool done = SEED.SaveData.GetBool(GameProgressKeys.TutorialDone, false);
/// </code>
/// </summary>
public static class GameProgressKeys
{
    /// <summary>チュートリアルを完了済みか（bool）。true ならタイトルから本編へ直行する。</summary>
    public const string TutorialDone = "tutorial_done";

    /// <summary>
    /// 怪獣（Lv10 の kaiju）を初めて釣り上げたときのストーリー会話を再生済みか（bool）。
    /// true なら 2 匹目以降の怪獣では会話を流さない。
    /// 書く側は <c>KaijuStoryTrigger</c>、読む側も同じスクリプトだけだが、
    /// 「進行フラグはここに集約する」という規約に従って定数化する。
    /// </summary>
    public const string KaijuStorySeen = "kaiju_story_seen";
}
