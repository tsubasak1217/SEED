// ============================================================================
//  TutorialFlow.cs
//  チュートリアルシーンの進行（決定入力 → 完了フラグ保存 → 本編へ）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// チュートリアルシーンの進行スクリプト。
///
/// 【責務】
///  - 決定入力を待ち、チュートリアル完了フラグを保存してから本編へ遷移する。
///    フラグはタイトルの分岐（TitleFlow）が読むため、遷移前に必ずディスクへ
///    書き出す（SetBool はメモリ上の更新に過ぎず、Save() で永続化される）。
///
/// フェード演出とシーン切替の手順は SceneFlow が持つ（単一責任）。
///
/// 【シーン側の設定】
///  - 同じアクターに SceneFlow と本スクリプトを付ける。
///  - 参照フィールド sceneFlow に、そのアクター自身を指定する。
/// </summary>
public class TutorialFlow : SEEDScript
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>チュートリアルの次に進むシーン名（プロジェクト設定の登録名）。</summary>
    private const string SceneNameMainGame = "mainGame";

    /// <summary>チュートリアル完了として保存する値。</summary>
    private const bool TutorialDoneValue = true;

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>遷移演出を担う SceneFlow（同じアクターに付いているものを指定する）。</summary>
    [SerializeField(Label = "シーン遷移", Tooltip = "同じアクターに付けた SceneFlow を指定する")]
    public SceneFlow? sceneFlow;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 毎フレーム、決定入力を監視して「完了フラグ保存 → 本編へ遷移」を行う。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 参照未設定・解決失敗時は必ず null になるため毎回チェックする
        if (sceneFlow is null) return;

        // 遷移演出中の入力は無視する（二重遷移・フラグの二重保存の防止）
        if (sceneFlow.IsTransitioning) return;

        // 決定入力が無ければ何もしない
        if (!SceneFlow.IsConfirmPressed()) return;

        // 進行フラグを永続化してから遷移する（次回起動時もタイトルから本編へ直行できる）
        SEED.SaveData.SetBool(GameProgressKeys.TutorialDone, TutorialDoneValue);
        SEED.SaveData.Save();

        sceneFlow.GoTo(SceneNameMainGame);
    }
}
