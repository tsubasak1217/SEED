// ============================================================
//  AndroidOverlayPlanner.cs — 差し替えで端末へ送るアセットの選び方（差分の選び方。純粋な処理。docs/android.md §23）
//
//  【規則】候補（変わったファイルから参照をたどったもの）ごとに、
//    手元の中身（pak に入れるときと同じ形。シーン等は中の絶対パスを assets:// へ書き換えたもの）の SHA-256 と、
//    端末の中身の SHA-256（上書き層へ送った記録 → 無ければ APK の pak のエントリ → どちらにも無ければ「無い」）を比べ、
//      端末に無い   → 送る（New）
//      中身が違う   → 送る（Changed）
//      中身が同じ   → 送らない（Unchanged。保存し直しただけ・参照先の変わっていないアセット）
//      手元で読めない → 送らない（理由を返す）
//  手元の中身の読み方・端末の中身の引き方は引数の関数で受ける（単体テストは偽物を渡す）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.HotReload;

/// <summary>送る理由。</summary>
public enum AndroidOverlayChange
{
    /// <summary>端末に無い（pak にも上書き層にも無い）。</summary>
    New,

    /// <summary>端末の中身と違う。</summary>
    Changed,
}

/// <summary>手元のアセット 1 件の中身（pak に入れるときと同じ形）。</summary>
/// <param name="Digest">中身の SHA-256（16 進・大文字）。</param>
/// <param name="Size">中身の大きさ（バイト）。</param>
/// <param name="Content">中身（書き換えたもの。書き換えないアセットは null＝<paramref name="SourcePath"/> をそのまま送る）。</param>
/// <param name="SourcePath">中身のファイル（<paramref name="Content"/> が null のとき）。</param>
public sealed record AndroidLocalAssetContent(string Digest, long Size, byte[]? Content, string? SourcePath);

/// <summary>送るアセット 1 件。</summary>
/// <param name="Relative">アセットルートからの相対パス（区切り /）。</param>
/// <param name="Local">手元の中身。</param>
/// <param name="Change">送る理由。</param>
public sealed record AndroidOverlayItem(string Relative, AndroidLocalAssetContent Local, AndroidOverlayChange Change);

/// <summary>読めなかった候補 1 件。</summary>
/// <param name="Relative">アセットルートからの相対パス。</param>
/// <param name="Reason">理由。</param>
public sealed record AndroidOverlaySkip(string Relative, string Reason);

/// <summary>差分の選び方の結果。</summary>
/// <param name="ToPush">送るもの（候補の順）。</param>
/// <param name="Unchanged">端末と同じなので送らないもの。</param>
/// <param name="Unreadable">手元で読めなかったもの。</param>
public sealed record AndroidOverlayPlan(
    IReadOnlyList<AndroidOverlayItem> ToPush,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<AndroidOverlaySkip> Unreadable)
{
    /// <summary>送るものの合計の大きさ（バイト）。</summary>
    public long PushBytes => ToPush.Sum(item => item.Local.Size);
}

/// <summary>差分の選び方。</summary>
public static class AndroidOverlayPlanner
{
    /// <summary>
    /// 候補から、端末へ送るアセットを選ぶ（純粋な処理。関数は候補ごとに 1 回ずつ呼ぶ）。
    /// </summary>
    /// <param name="candidates">候補（アセットルートからの相対パス。大文字小文字だけ違う重複は最初のものを使う）。</param>
    /// <param name="readLocal">手元の中身を読む（読めなければ例外）。</param>
    /// <param name="deviceDigest">端末の中身の SHA-256（無ければ null）。</param>
    /// <returns>結果。</returns>
    public static AndroidOverlayPlan Plan(
        IEnumerable<string> candidates,
        Func<string, AndroidLocalAssetContent> readLocal,
        Func<string, string?> deviceDigest)
    {
        var toPush = new List<AndroidOverlayItem>();
        var unchanged = new List<string>();
        var unreadable = new List<AndroidOverlaySkip>();
        foreach (var relative in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AndroidLocalAssetContent local;
            try
            {
                local = readLocal(relative);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new AndroidOverlaySkip(relative, ex.Message));
                continue;
            }
            var device = deviceDigest(relative);
            if (device is null)
            {
                toPush.Add(new AndroidOverlayItem(relative, local, AndroidOverlayChange.New));
            }
            else if (!string.Equals(device, local.Digest, StringComparison.OrdinalIgnoreCase))
            {
                toPush.Add(new AndroidOverlayItem(relative, local, AndroidOverlayChange.Changed));
            }
            else
            {
                unchanged.Add(relative);
            }
        }
        return new AndroidOverlayPlan(toPush, unchanged, unreadable);
    }
}
