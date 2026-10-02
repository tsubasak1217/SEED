// ============================================================
//  LocaleSafeParse.cs — 実行中と同じ読み方（LocaleTable.Parse・LocaleIndex.Parse）を、例外を出さずに呼ぶ
//
//  【なぜ要るか】（2026-10-03 に確かめた）
//  LocaleTable.Parse・LocaleIndex.Parse は JsonException だけを捕まえる。ところが System.Text.Json の
//  JsonDocument.Parse は、対になっていないサロゲートのエスケープ（"\ud800"）を含む JSON で ArgumentException を投げる
//  （"Cannot transcode invalid UTF-16 string to UTF-8 JSON text."）。エディタの UI スレッドで外へ出すとエディタごと落ちるので、
//  ここで受け止めて「読めない（空）」として扱う。実行中（L10n）の側の扱いは backlog（scripting を直す）。
// ============================================================

using System;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>実行中と同じ読み方を、例外を出さずに呼ぶ。</summary>
internal static class LocaleSafeParse
{
    /// <summary>空の JSON（読めなかったときの代わり）。</summary>
    private const string EmptyJson = "{}";

    /// <summary>言語の表を読む（例外になる JSON は空の表）。</summary>
    /// <param name="text">表の JSON。</param>
    /// <param name="origin">どこから読んだか。</param>
    /// <param name="error">例外になった理由（読めたら空）。</param>
    /// <returns>表。</returns>
    public static LocaleTable Table(string text, string origin, out string error)
    {
        try
        {
            error = string.Empty;
            return LocaleTable.Parse(text, origin);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = ex.Message;
            return LocaleTable.Parse(EmptyJson, origin);
        }
    }

    /// <summary>言語の一覧を読む（例外になる JSON は言語 0 件）。</summary>
    /// <param name="text">index.json の中身。</param>
    /// <param name="origin">どこから読んだか。</param>
    /// <returns>言語の一覧。</returns>
    public static LocaleIndex Index(string text, string origin)
    {
        try
        {
            return LocaleIndex.Parse(text, origin);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return LocaleIndex.Parse(EmptyJson, origin);
        }
    }
}
