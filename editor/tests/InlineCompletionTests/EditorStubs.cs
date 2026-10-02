// ============================================================
//  EditorStubs.cs — エディタ本体のシングルトンの代役（このテストの中だけ）
//
//  ScriptApiReference・InlineCompletionSystemPrompt はエディタの EditorLog・EditorPreferences・
//  EditorPaths を使う。本物は WPF のエディタ本体（WinExe）にあり、テストへは持ち込めないので、
//  同じ名前空間・同じ名前で、使われる口だけを持つ代役を置く。
//  （本物の口が変わってこの代役と合わなくなると、このテストがビルドできなくなる＝食い違いの検知器）
// ============================================================

using System.Collections.Generic;
using System.IO;
using InlineCompletionTests;

namespace SEEDEditor
{
    /// <summary>EditorLog の代役（書いた行を覚えるだけ）。</summary>
    public static class EditorLog
    {
        /// <summary>書かれた行（古い順）。</summary>
        public static List<string> Lines { get; } = new();

        /// <summary>1 行書く。</summary>
        /// <param name="message">内容。</param>
        public static void Write(string message) => Lines.Add(message);
    }

    /// <summary>EditorPreferences の代役（予算の項目だけ）。</summary>
    public sealed class EditorPreferences
    {
        /// <summary>今の環境設定。</summary>
        public static EditorPreferences Instance { get; } = new();

        /// <summary>注入する API リファレンスの最大文字数（null = JSON の既定）。</summary>
        public int? InlineCompletionReferenceChars { get; set; }
    }
}

namespace SEEDEditor.Settings
{
    /// <summary>EditorPaths の代役（構成フォルダはリポジトリの editor/config）。</summary>
    public static class EditorPaths
    {
        /// <summary>構成フォルダ（同梱の inline_completion_reference.json がある所）。</summary>
        public static string? ConfigDir =>
            Path.GetDirectoryName(RepoFiles.Find(RepoFiles.ReferenceSettingsRelative));
    }
}
