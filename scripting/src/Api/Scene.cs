namespace SEED;

/// <summary>
/// シーン管理の静的 API。シーン遷移（タイトル → ゲーム → リザルト等）に使う。
///
/// シーンはエディタの「プロジェクト設定 → シーンマネージャ」で登録した**名前**で
/// 参照する（例: "game"）。assets:// の仮想パス直接指定も可能。
///
/// 推奨フロー: 遷移前の暇なタイミング（フェードアウト中など）で <see cref="Load"/> を
/// 呼んで事前読み込みしておき、<see cref="Transition"/> で即座に切り替える。
/// Load せずに Transition だけ呼んでも内部で自動的に読み込まれる（その分遷移が重い）。
/// </summary>
public static class Scene
{
    // ── コマンド種別（Rust 側 host_api.rs の SCENE_CMD_* と一致させる）──
    private const int CmdPreload = 0;
    private const int CmdTransition = 1;

    /// <summary>
    /// シーンを事前読み込みする（遷移はしない）。受理されたら true。
    /// 読み込みはフレーム末尾に行われ、次の <see cref="Transition"/>（同じシーン）で
    /// ロードなしの即時切り替えになる。保持できる事前読み込みは 1 つ。
    /// </summary>
    /// <param name="sceneName">シーンマネージャ登録名（または assets:// パス）</param>
    public static bool Load(string sceneName) => ScriptHost.SceneCommand(CmdPreload, sceneName);

    /// <summary>
    /// シーン全体を切り替える（シーン遷移）。受理されたら true。
    /// 事前読み込み済みなら即座に、未読み込みなら内部で自動的に読み込んでから切り替わる。
    /// 実際の切り替えはフレーム末尾に行われ、現在のシーンの全アクター・スクリプトは破棄される。
    ///
    /// 注意: Transition を呼んだフレームで発行した Instantiate / Destroy は破棄される。
    /// </summary>
    /// <param name="sceneName">シーンマネージャ登録名（または assets:// パス）</param>
    public static bool Transition(string sceneName) => ScriptHost.SceneCommand(CmdTransition, sceneName);
}
