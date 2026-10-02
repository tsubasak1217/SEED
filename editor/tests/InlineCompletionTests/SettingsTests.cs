using System;
using System.IO;
using System.Linq;
using ProjectSystemTests;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 選び方の設定（<see cref="ApiReferenceSettings"/>・editor/config/inline_completion_reference.json）のテスト。
/// </summary>
public static class SettingsTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("設定: 同梱の inline_completion_reference.json が組み込み既定と一致する", ShippedJsonMatchesBuiltIn);
        harness.Add("設定: JSON で値を差し替えられる（コメント・末尾カンマ可）", LoadsValuesFromJson);
        harness.Add("設定: 範囲外の値は丸めて警告する", ClampsOutOfRange);
        harness.Add("設定: 壊れた JSON・無いファイルは組み込み既定へ戻る", FallsBackToBuiltIn);
        harness.Add("設定: 環境設定の予算は範囲へ丸め、未指定なら JSON の既定", ResolvesBudget);
    }

    /// <summary>同梱の JSON と組み込み既定。</summary>
    private static void ShippedJsonMatchesBuiltIn()
    {
        var shipped = ApiReferenceSettings.Load(RepoFiles.Find(RepoFiles.ReferenceSettingsRelative));
        var builtIn = ApiReferenceSettings.BuiltIn();
        Check.True(shipped.Warnings.Count == 0, "同梱の JSON に警告が無い: " + string.Join(" / ", shipped.Warnings));
        Check.True(shipped.SourcePath is not null, "ファイルから読んだ");
        Check.Equal(builtIn.BudgetChars, shipped.BudgetChars, "budget_chars");
        Check.Equal(builtIn.MaxPartChars, shipped.MaxPartChars, "max_part_chars");
        Check.Equal(builtIn.CursorWindowLines, shipped.CursorWindowLines, "cursor_window_lines");
        Check.Equal(builtIn.MinPrefixLength, shipped.MinPrefixLength, "min_prefix_length");
        Check.Equal(builtIn.MinScore, shipped.MinScore, "min_score");
        Check.Equal(builtIn.RelativeMinScore, shipped.RelativeMinScore, "relative_min_score");
        Check.Equal(string.Join("|", builtIn.AlwaysInclude), string.Join("|", shipped.AlwaysInclude), "always_include");
        Check.True(builtIn.IgnoredWords.SetEquals(shipped.IgnoredWords), "ignored_words");
        Check.Equal(builtIn.Weights, shipped.Weights, "weights");
        Check.Equal(ApiReferenceSettings.DefaultBudgetChars, shipped.BudgetChars, "既定の予算は以前の固定値 12000 と同じ");
    }

    /// <summary>JSON の値。</summary>
    private static void LoadsValuesFromJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(ApiReferenceSettings.FileName);
        File.WriteAllText(path, """
            {
              // コメントも書ける
              "format_version": 1,
              "budget_chars": 50000,
              "max_part_chars": 4000,
              "cursor_window_lines": 5,
              "min_prefix_length": 4,
              "min_score": 2.5,
              "relative_min_score": 0.5,
              "always_include": ["1. スクリプトの基本形", "  "],
              "ignored_words": ["Foo"],
              "weights": { "typing": 9.0, "prefix_match": 0.25 },
            }
            """);
        var settings = ApiReferenceSettings.LoadFromDir(temp.Path);
        Check.True(settings.Warnings.Count == 0, "警告なし: " + string.Join(" / ", settings.Warnings));
        Check.Equal(50000, settings.BudgetChars, "budget_chars");
        Check.Equal(4000, settings.MaxPartChars, "max_part_chars");
        Check.Equal(5, settings.CursorWindowLines, "cursor_window_lines");
        Check.Equal(4, settings.MinPrefixLength, "min_prefix_length");
        Check.Equal(2.5, settings.MinScore, "min_score");
        Check.Equal(0.5, settings.RelativeMinScore, "relative_min_score");
        Check.Equal("1. スクリプトの基本形", string.Join("|", settings.AlwaysInclude), "空白だけの項目は捨てる");
        Check.True(settings.IgnoredWords.Contains("foo") && !settings.IgnoredWords.Contains("SEED"), "ignored_words は差し替え（大文字小文字は区別しない）");
        Check.Equal(9.0, settings.Weights.Typing, "書いた重み");
        Check.Equal(0.25, settings.Weights.PrefixMatch, "書いた重み");
        Check.Equal(ApiReferenceSettings.DefaultWeights.File, settings.Weights.File, "書かなかった重みは既定");

        File.WriteAllText(path, """{ "format_version": 1, "always_include": [] }""");
        Check.Equal(0, ApiReferenceSettings.Load(path).AlwaysInclude.Count, "空の always_include は「常に入れる節なし」として尊重する");
    }

    /// <summary>範囲外の値。</summary>
    private static void ClampsOutOfRange()
    {
        using var temp = new TempDir();
        var path = temp.Combine(ApiReferenceSettings.FileName);
        File.WriteAllText(path, """
            { "format_version": 99, "budget_chars": -5, "max_part_chars": 10, "cursor_window_lines": -1,
              "min_prefix_length": 0, "min_score": -1, "relative_min_score": 5, "weights": { "file": -2 } }
            """);
        var settings = ApiReferenceSettings.Load(path);
        Check.Equal(ApiReferenceSettings.MinBudgetChars, settings.BudgetChars, "負の予算は 0");
        Check.Equal(ApiReferenceSettings.MinPartChars, settings.MaxPartChars, "小さすぎる切れ端の上限は下限へ");
        Check.Equal(0, settings.CursorWindowLines, "負の行数は 0");
        Check.Equal(ApiReferenceSettings.MinPrefixLengthFloor, settings.MinPrefixLength, "接頭辞の最短は 1 以上");
        Check.Equal(ApiReferenceSettings.DefaultMinScore, settings.MinScore, "負の点の下限は既定");
        Check.Equal(ApiReferenceSettings.MaxRelativeMinScore, settings.RelativeMinScore, "1 を超える割合は 1 へ");
        Check.Equal(ApiReferenceSettings.DefaultWeights.File, settings.Weights.File, "負の重みは既定");
        Check.True(settings.Warnings.Count >= 8, $"項目ごとと書式バージョンの警告（{settings.Warnings.Count} 件）");
        Check.True(settings.Warnings.Any(w => w.Contains("format_version=99", StringComparison.Ordinal)), "未知の書式バージョンの警告");
    }

    /// <summary>壊れた・無いファイル。</summary>
    private static void FallsBackToBuiltIn()
    {
        using var temp = new TempDir();
        var broken = temp.Combine("broken.json");
        File.WriteAllText(broken, "{ これは JSON ではない");
        var fromBroken = ApiReferenceSettings.Load(broken);
        Check.True(fromBroken.SourcePath is null && fromBroken.Warnings.Count == 1, "壊れた JSON は組み込み既定・警告 1 件");
        Check.Equal(ApiReferenceSettings.DefaultBudgetChars, fromBroken.BudgetChars, "既定の予算");

        var missing = ApiReferenceSettings.LoadFromDir(temp.CreateSubDirectory("empty"));
        Check.True(missing.SourcePath is null && missing.Warnings.Count == 1, "無いファイルは組み込み既定・警告 1 件");
        var noDir = ApiReferenceSettings.LoadFromDir(null);
        Check.True(noDir.SourcePath is null && noDir.Warnings.Count == 1, "構成フォルダが無ければ組み込み既定");
    }

    /// <summary>予算の解決。</summary>
    private static void ResolvesBudget()
    {
        var settings = ApiReferenceSettings.Create(budgetChars: 8000);
        Check.Equal(8000, settings.ResolveBudget(null), "未指定なら JSON の既定");
        Check.Equal(60000, settings.ResolveBudget(60000), "指定が優先");
        Check.Equal(ApiReferenceSettings.MinBudgetChars, settings.ResolveBudget(-1), "負は 0 へ");
        Check.Equal(ApiReferenceSettings.MaxBudgetChars, settings.ResolveBudget(int.MaxValue), "大きすぎる値は上限へ");
    }
}
