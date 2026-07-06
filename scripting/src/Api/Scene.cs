namespace SEED;

/// <summary>
/// シーン管理の静的 API。シーン遷移（タイトル → ゲーム → リザルト等）に使う。
/// </summary>
public static class Scene
{
    /// <summary>
    /// シーン全体を指定の .scene ファイルへ切り替える（assets:// 仮想パス）。
    /// 受理されたら true。実際の切り替えはフレーム末尾に行われ、
    /// 現在のシーンの全アクター・スクリプトは破棄される。
    ///
    /// 注意: 同フレーム中に発行した Instantiate / Destroy は破棄される。
    /// シーン遷移を呼んだら、そのフレームではそれ以上シーン操作をしないこと。
    /// </summary>
    public static bool Load(string scenePath) => ScriptHost.TryLoadScene(scenePath);
}
