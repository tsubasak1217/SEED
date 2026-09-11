// ============================================================
//  RecentProjectItem.cs — スタート画面「最近のプロジェクト」1 行ぶんの表示データ
//
//  【役割】
//  RecentProjectEntry（永続化された生データ）を、そのまま XAML へ束縛できる形に整える。
//  実在判定・日時の整形・行の色といった「見せ方」の判断をここに集め、
//  XAML 側にコンバータやコードビハインドの分岐を散らさない。
// ============================================================

using System;
using System.IO;
using System.Windows.Media;
using SEEDEditor.Project;

namespace SEEDEditor.Startup;

/// <summary>
/// スタート画面の一覧に出す 1 プロジェクトぶんの表示データ（不変）。
/// </summary>
public sealed class RecentProjectItem
{
    // ── 表示色（ダークテーマ）────────────────────────────────

    /// <summary>実在するプロジェクトの名前の色。</summary>
    private static readonly SolidColorBrush BrushName = new(Color.FromRgb(0xDC, 0xDC, 0xDC));

    /// <summary>実在するプロジェクトの副情報（パス・日時）の色。</summary>
    private static readonly SolidColorBrush BrushSub = new(Color.FromRgb(0x8A, 0x8A, 0x8A));

    /// <summary>見つからないプロジェクトの名前の色（薄く表示する）。</summary>
    private static readonly SolidColorBrush BrushNameMissing = new(Color.FromRgb(0x77, 0x77, 0x77));

    /// <summary>見つからないプロジェクトの副情報の色。</summary>
    private static readonly SolidColorBrush BrushSubMissing = new(Color.FromRgb(0x5E, 0x5E, 0x5E));

    /// <summary>見つからない行に添える注記。</summary>
    private const string MISSING_NOTE = "(見つかりません)";

    /// <summary>最終使用日時の表示書式（ローカル時刻）。</summary>
    private const string LAST_OPENED_FORMAT = "yyyy/MM/dd HH:mm";

    /// <summary>最終使用日時が記録されていない／壊れているときの表示。</summary>
    private const string LAST_OPENED_UNKNOWN = "最終使用: 不明";

    // ── プロパティ（XAML の束縛先）───────────────────────────

    /// <summary>.seedproj の絶対パス。</summary>
    public string ProjectFilePath { get; }

    /// <summary>一覧に出す表示名。</summary>
    public string DisplayName { get; }

    /// <summary>最終使用日時の表示文字列。</summary>
    public string LastOpenedText { get; }

    /// <summary>.seedproj が実在するか。</summary>
    public bool Exists { get; }

    /// <summary>実在しない行に出す注記（実在するなら空文字）。</summary>
    public string MissingNote => Exists ? string.Empty : MISSING_NOTE;

    /// <summary>名前の文字色（実在しないものは薄く）。</summary>
    public Brush NameBrush => Exists ? BrushName : BrushNameMissing;

    /// <summary>副情報の文字色（実在しないものは薄く）。</summary>
    public Brush SubBrush => Exists ? BrushSub : BrushSubMissing;

    /// <summary>
    /// 永続化された記録から表示データを組み立てる。
    /// </summary>
    /// <param name="entry">recent_projects.json の 1 件。</param>
    public RecentProjectItem(RecentProjectEntry entry)
    {
        ProjectFilePath = entry.Path;
        Exists          = SafeFileExists(entry.Path);

        // 記録された名前が空なら、ファイル名（拡張子なし）で代用する。
        DisplayName = string.IsNullOrWhiteSpace(entry.Name)
            ? Path.GetFileNameWithoutExtension(entry.Path)
            : entry.Name;

        var at = entry.LastOpenedAt;
        LastOpenedText = at is null
            ? LAST_OPENED_UNKNOWN
            : $"最終使用: {at.Value.ToLocalTime().ToString(LAST_OPENED_FORMAT, System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>例外を投げずにファイルの存在を確認する（不正パス・未接続ドライブ対策）。</summary>
    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }
}
