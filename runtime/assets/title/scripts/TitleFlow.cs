// ============================================================================
//  TitleFlow.cs
//  タイトルシーンの進行（決定入力 → 進行状況に応じた分岐遷移）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// タイトルシーンの進行スクリプト。
///
/// 【責務】
///  - 決定入力を待ち、セーブデータの進行フラグに応じて遷移先を決める。
///    - チュートリアル完了済み  … 本編（mainGame）へ
///    - 未完了                  … プロローグ（prologue）へ
///
/// フェード演出とシーン切替の手順そのものは SceneFlow が持つ。
/// 本クラスは「どこへ行くか」を決めるだけに徹する（単一責任）。
///
/// 【シーン側の設定】
///  - 同じアクターに SceneFlow と本スクリプトを付ける。
///  - 参照フィールド sceneFlow に、そのアクター自身を指定する。
/// </summary>
public class TitleFlow : SEEDScript
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>チュートリアル完了済みのときの遷移先シーン名（プロジェクト設定の登録名）。</summary>
    private const string SceneNameMainGame = "mainGame";

    /// <summary>チュートリアル未完了のときの遷移先シーン名（プロジェクト設定の登録名）。</summary>
    private const string SceneNamePrologue = "prologue";

    /// <summary>進行フラグが読めなかった場合の既定値（未クリア扱い）。</summary>
    private const bool DefaultTutorialDone = false;

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>
    /// 遷移演出を担う SceneFlow（同じアクターに付いているものを指定する）。
    /// スクリプト間参照はインスペクタの参照フィールド経由が正式な作法。
    /// （GetComponent はネイティブコンポーネント専用でスクリプトは取得できない）
    /// </summary>
    [SerializeField(Label = "シーン遷移", Tooltip = "同じアクターに付けた SceneFlow を指定する")]
    public SceneFlow? sceneFlow;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 毎フレーム、決定入力を監視して遷移を発行する。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 参照未設定・解決失敗時は必ず null になるため毎回チェックする
        if (sceneFlow is null) return;

        // 遷移演出中の入力は無視する（二重遷移の防止）
        if (sceneFlow.IsTransitioning) return;

        // 決定入力が無ければ何もしない
        if (!SceneFlow.IsConfirmPressed()) return;

        // 【デバッグ】SceneFlow 側のフラグが立っていれば、セーブデータの進行状況を
        // 一切見ずに常にプロローグへ通す（プロローグ→チュートリアルの動作確認用）。
        if (sceneFlow.debugForceFromPrologue)
        {
            sceneFlow.GoTo(SceneNamePrologue);
            return;
        }

        // 通常時: 進行状況で分岐する（チュートリアル済みなら本編へ直行）
        bool tutorialDone = SEED.SaveData.GetBool(GameProgressKeys.TutorialDone, DefaultTutorialDone);
        sceneFlow.GoTo(tutorialDone ? SceneNameMainGame : SceneNamePrologue);
    }
}
