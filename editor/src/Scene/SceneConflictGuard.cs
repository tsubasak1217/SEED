// ============================================================
//  SceneConflictGuard.cs — 競合中のシーンファイルを「読まない・上書きしない」ための判定
//
//  【なぜ必要か】
//  バージョン管理（Lore）は競合したテキストファイルへ diff3 形式の印
//  （<<<<<<< / ======= / >>>>>>>）を書き込む。印が入った .scene は JSON として読めない。
//  シーンを開いたままマージや「最新を取得」で競合が起きると、
//    1. 自動再読込が印つきのファイルを読もうとして失敗し、エラーダイアログが出る
//       （ランタイムは元のシーンを保持するので表示はマージ前のまま）
//    2. その状態で Ctrl+S すると、メモリ上の古いシーンで印つきのファイルを上書きする。
//       競合の中身（相手の変更）が黙って消え、しかも Lore 上は「未解決」のまま残る
//  どちらも「競合を解決するまで触らない」が正しいので、ここで 1 か所に判定を置く。
//
//  【判定の方法】
//  ファイルの中に競合ブロックの始まりの印（行頭の <<<<<<< ）があるかを見る。
//  バージョン管理の状態（サーバ・プロバイダ）には依存しない。
//  JSON のシーンに行頭 <<<<<<< が正当に現れることは無いので、誤検出しない。
//
//  【依存】
//  WPF に依存しない。印の判定は VersionControl/Merge の純粋ロジックを使い回す
//  （印の定義を二重に持たない）。
// ============================================================

using System;
using System.IO;
using SEEDEditor.VersionControl.Merge;

namespace SEEDEditor.Scene;

/// <summary>
/// シーンファイルが競合中（印つき）かどうかの判定。
/// </summary>
public static class SceneConflictGuard
{
    /// <summary>自動再読込を見送るときのステータス文言。</summary>
    public const string MESSAGE_RELOAD_SKIPPED =
        "シーンが競合中のため再読込しません（バージョン管理パネルで競合を解決すると読み直します）";

    /// <summary>保存を拒否するときの文言。</summary>
    public const string MESSAGE_SAVE_DENIED =
        "このシーンは競合中です（ファイルに競合の印が入っています）。\n"
      + "いま保存すると、競合している相手側の変更を失います。\n"
      + "先に「バージョン管理」パネルの競合の節で解決してください。";

    /// <summary>保存を拒否したときのトースト文言。</summary>
    public const string TOAST_SAVE_DENIED = "競合中のため保存できません";

    /// <summary>
    /// 指定のシーンファイルに競合の印が入っているか。
    /// 読めない・存在しない場合は false（＝判定できないときは従来の挙動を妨げない）。
    /// </summary>
    /// <param name="scenePath">シーンの絶対パス（null 可）。</param>
    public static bool IsConflicted(string? scenePath)
    {
        if (string.IsNullOrEmpty(scenePath)) return false;

        try
        {
            if (!File.Exists(scenePath)) return false;

            // 書き込み中でも読めるよう共有モードを広く取る（自動再読込のハッシュ計算と同じ）。
            using var stream = new FileStream(
                scenePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ConflictMarkerDocument.HasConflictMarkers(reader.ReadToEnd());
        }
        catch (Exception)
        {
            // 読めないなら判定できない。ここで例外を上げて保存や再読込を止める方が有害。
            return false;
        }
    }
}
