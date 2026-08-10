using System;

namespace SEED;

/// <summary>
/// スクリプトから任意区間の CPU 時間を計測する静的 API。
///
/// 計測結果はエンジンのフレームプロファイラへ「そのとき実行中のセクションの子」として
/// 積まれ、エディタの「プロファイラ」パネルの階層ツリーに現れる
/// （例: スクリプト &gt; Update &gt; ここで付けた名前）。
///
/// 【いつ計測されるか】エディタの「プロファイラ」パネルが表示されている間だけ。
/// パネルが閉じているときは <see cref="Begin"/> / <see cref="End"/> ともに
/// ほぼゼロコストで false を返す（文字列変換すら行わない）。
///
/// 【使い方】<c>using</c> で自動終了させるのが安全:
/// <code>
/// using (SEED.Profiler.Scope("敵の索敵"))
/// {
///     SearchEnemies();
/// }
/// </code>
/// 明示的に呼ぶ場合は Begin と End を必ず対にすること:
/// <code>
/// SEED.Profiler.Begin("敵の索敵");
/// SearchEnemies();
/// SEED.Profiler.End();
/// </code>
///
/// 【名前は固定文字列にすること】名前の種類には上限（256）がある。
/// <c>Begin($"敵{i}")</c> のようにループ変数を埋め込むと上限に達し、
/// それ以降の名前が計測されなくなる。
/// </summary>
public static class Profiler
{
    // ── コマンド種別（Rust 側 host_api.rs の PROFILER_KIND_* と一致させる）──

    /// <summary>計測開始。</summary>
    private const int KindBegin = 0;

    /// <summary>計測終了。</summary>
    private const int KindEnd = 1;

    /// <summary>
    /// 計測区間を開始する。計測された（＝パネルが開いている）なら true。
    /// 必ず対応する <see cref="End"/> を呼ぶこと。
    /// </summary>
    /// <param name="name">パネルに表示されるセクション名（固定文字列推奨）</param>
    public static bool Begin(string name) => ScriptHost.ProfilerScope(KindBegin, name);

    /// <summary>
    /// 直近の <see cref="Begin"/> に対応する計測区間を終了する。
    /// 対応する Begin が無ければ何もせず false を返す（エンジン側の計測は壊れない）。
    /// </summary>
    public static bool End() => ScriptHost.ProfilerScope(KindEnd, null);

    /// <summary>
    /// <c>using</c> 構文で自動終了する計測スコープを作る。
    /// 例外で抜けても確実に End されるため、Begin/End の手書きより安全。
    /// </summary>
    /// <param name="name">パネルに表示されるセクション名（固定文字列推奨）</param>
    public static ProfilerScopeHandle Scope(string name) => new ProfilerScopeHandle(name);
}

/// <summary>
/// <see cref="Profiler.Scope"/> が返す使い捨てスコープ。
/// Dispose（using ブロックの終端）で <see cref="Profiler.End"/> を呼ぶ。
/// 構造体なのでヒープ確保は発生しない。
/// </summary>
public readonly struct ProfilerScopeHandle : IDisposable
{
    /// <summary>Begin が実際に計測されたか。false のとき Dispose は何もしない。</summary>
    private readonly bool _began;

    /// <summary>計測を開始する。</summary>
    internal ProfilerScopeHandle(string name) => _began = Profiler.Begin(name);

    /// <summary>計測を終了する。</summary>
    public void Dispose()
    {
        if (_began) Profiler.End();
    }
}
