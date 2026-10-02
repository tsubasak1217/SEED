// ============================================================
//  RenderProfileFlagValues.cs — 描画の構成の旗の値の組（ランタイムの RenderProfileFlags の写し）
//
//  【役割】
//  構成の定義（render_profiles.json の flags）・プロジェクトの上書き（project_settings.json の render 節）を重ねた
//  「旗の値の組」を、変更不可の値として持つ。画面の「構成のまま（有効）」の表示と、実効の旗の要約に使う。
//  既定（何も書かれていない旗）は「用意する」＝ true、memory_hint は performance（flags.rs の RenderProfileFlags::FULL）。
//
//  【実効の判断】
//  3D のシーンを描かない（scene_3d = false）と、3D だけの資源の旗（RenderProfileToggleFlag.RequiresScene3D）は
//  個々の値によらず止まる（flags.rs の allocates_* と同じ規則）。<see cref="IsEffectivelyEnabled"/> がそれを返す。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.ProjectSettings;

/// <summary>描画の構成の旗の値の組（変更不可）。</summary>
public sealed class RenderProfileFlagValues
{
    /// <summary>書かれた真偽の旗（無いキーは既定＝ true）。</summary>
    private readonly IReadOnlyDictionary<string, bool> _toggles;

    /// <summary>旗の値の組を作る。</summary>
    /// <param name="toggles">真偽の旗（無いキーは既定＝ true）。</param>
    /// <param name="memoryHint">GPU メモリの確保の方針（正規化した値）。</param>
    private RenderProfileFlagValues(IReadOnlyDictionary<string, bool> toggles, string memoryHint)
    {
        _toggles   = toggles;
        MemoryHint = memoryHint;
    }

    /// <summary>すべて用意する（full。従来の描画と同じ）。</summary>
    public static RenderProfileFlagValues Full { get; } =
        new(new Dictionary<string, bool>(StringComparer.Ordinal), RenderProfileFlagCatalog.DefaultMemoryHint);

    /// <summary>GPU メモリの確保の方針（<see cref="RenderProfileFlagCatalog.MemoryHintPerformance"/> など）。</summary>
    public string MemoryHint { get; }

    /// <summary>真偽の旗の値（書かれていなければ既定＝ true）。</summary>
    /// <param name="key">旗のキー。</param>
    /// <returns>値。</returns>
    public bool Get(string key) => !_toggles.TryGetValue(key, out var value) || value;

    /// <summary>
    /// 実効で有効か（3D だけの資源の旗は scene_3d も有効なときだけ有効。flags.rs の allocates_* と同じ規則）。
    /// </summary>
    /// <param name="key">旗のキー。</param>
    /// <returns>実効で有効なら true。</returns>
    public bool IsEffectivelyEnabled(string key)
    {
        var flag = RenderProfileFlagCatalog.FindToggle(key);
        var own  = Get(key);
        return flag is { RequiresScene3D: true } ? own && Get(RenderProfileFlagCatalog.Scene3DKey) : own;
    }

    /// <summary>full と同じか（何も止めない＝従来どおり）。</summary>
    public bool IsFull =>
        RenderProfileFlagCatalog.ToggleFlags.All(flag => Get(flag.Key))
        && string.Equals(MemoryHint, RenderProfileFlagCatalog.DefaultMemoryHint, StringComparison.Ordinal);

    /// <summary>真偽の旗を 1 つ差し替えた組を返す。</summary>
    /// <param name="key">旗のキー（表にあるもの）。</param>
    /// <param name="value">値。</param>
    /// <returns>新しい組。</returns>
    public RenderProfileFlagValues WithToggle(string key, bool value)
    {
        var toggles = new Dictionary<string, bool>(_toggles, StringComparer.Ordinal) { [key] = value };
        return new RenderProfileFlagValues(toggles, MemoryHint);
    }

    /// <summary>memory_hint を差し替えた組を返す。</summary>
    /// <param name="memoryHint">正規化した値。</param>
    /// <returns>新しい組。</returns>
    public RenderProfileFlagValues WithMemoryHint(string memoryHint) => new(_toggles, memoryHint);

    /// <summary>
    /// 値の組を人が読む 1 行にする（画面の「実効」の要約。例「3D のシーン 無効・後処理 有効・…・GPU メモリの確保 memory_usage」）。
    /// 3D だけの資源の旗は実効の値（scene_3d が無効なら無効）を出す。
    /// </summary>
    /// <returns>要約。</returns>
    public string Describe()
    {
        var parts = RenderProfileFlagCatalog.ToggleFlags
            .Select(flag => $"{flag.Label} {(IsEffectivelyEnabled(flag.Key) ? EnabledText : DisabledText)}")
            .Append($"{RenderProfileFlagCatalog.MemoryHintLabel} {MemoryHint}");
        return string.Join(SummarySeparator, parts);
    }

    /// <summary>有効の表示。</summary>
    public const string EnabledText = "有効";

    /// <summary>無効の表示。</summary>
    public const string DisabledText = "無効";

    /// <summary>要約の区切り。</summary>
    private const string SummarySeparator = "・";
}
