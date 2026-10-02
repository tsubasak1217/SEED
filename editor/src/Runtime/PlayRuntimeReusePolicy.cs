// ============================================================
//  PlayRuntimeReusePolicy.cs — 常駐の Play プロセスを使い回してよいか（純粋な判断）
//
//  【背景】RuntimeManager はウィンドウ Play（別プロセス）を Stop しても Kill せず、隠して保持する（常駐 Play）。
//  次の Play では LOAD_SCENE でシーンだけ差し替えて使い回す（コールドスタートの数十秒を避ける）。
//  端末の模擬（docs/editor_device_presets.md）は窓の大きさ・表示倍率・安全領域を「プロセスの起動時の環境変数」で
//  与えるため、別の条件で起動した常駐プロセスを使い回すと、模擬の条件が黙って違ったまま動いてしまう。
//
//  【決まり】
//    1. 使い回すのは、生きていて・読み直すシーンが決まっていて（従来の条件）・起動の条件の Key
//       （RuntimeLaunchOverrides.Key。上書きなしの従来の起動は null）が同じときだけ。
//       違えば常駐のプロセスを閉じて新しく起動する
//    2. 埋め込みの Play（別プロセスを起動しない）に戻るときは、上書き付きで起動した常駐のプロセスを閉じる
//       （埋め込みの Play では使い回せず、端末の大きさの窓と GPU の資源を握ったまま隠れ続けるだけなので）。
//       上書きなしの常駐プロセスは従来どおり残す（従来の別ウィンドウ Play の振る舞いを変えない）
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Runtime;

/// <summary>常駐の Play プロセスを使い回してよいかの判断。</summary>
public static class PlayRuntimeReusePolicy
{
    /// <summary>
    /// 常駐の Play プロセスを次の Play に使い回してよいか。
    /// </summary>
    /// <param name="persistentAlive">常駐のプロセスが生きているか。</param>
    /// <param name="playScenePath">次の Play で読むシーン（null・空なら開始シーン＝LOAD_SCENE を送れないので使い回さない）。</param>
    /// <param name="persistentLaunchKey">常駐のプロセスを起動したときの条件の Key（上書きなしなら null）。</param>
    /// <param name="requestedLaunchKey">次の Play の条件の Key（上書きなしなら null）。</param>
    /// <returns>使い回してよければ true。</returns>
    public static bool CanReuse(bool persistentAlive, string? playScenePath, string? persistentLaunchKey, string? requestedLaunchKey) =>
        persistentAlive
        && !string.IsNullOrEmpty(playScenePath)
        && string.Equals(persistentLaunchKey, requestedLaunchKey, StringComparison.Ordinal);

    /// <summary>
    /// 埋め込みの Play を始める前に、常駐の Play プロセスを閉じるべきか（上書き付きで起動したものだけ閉じる）。
    /// </summary>
    /// <param name="hasPersistent">常駐のプロセスを保持しているか。</param>
    /// <param name="persistentLaunchKey">そのプロセスを起動したときの条件の Key（上書きなしなら null）。</param>
    /// <returns>閉じるべきなら true。</returns>
    public static bool MustReleaseBeforeEmbeddedPlay(bool hasPersistent, string? persistentLaunchKey) =>
        hasPersistent && persistentLaunchKey is not null;
}
