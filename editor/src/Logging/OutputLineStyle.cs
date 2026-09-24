// ============================================================
//  OutputLineStyle.cs — Output パネルの 1 行の「色の種類」と「出どころ（エンジン / ゲーム）」
//
//  【色の種類（規約の正典: docs/editor_ui_style.md 7 章）】
//    Default … 通常の行（灰）
//    Runtime … 実行先からの通知（水色。[Runtime→Editor]・Android の実行の開始／停止／アプリの終了）
//    Build   … ビルドの進み具合（黄。[cargo]・Android の工程の見出し・子プロセスの出力）
//    Warning … 警告（黄。いまは Build と同じ色。意味を分けておき、色を変えるときは OutputPanel の表だけを直す）
//    Error   … エラー・失敗（赤）
//  色そのもの（ブラシ）は Panels/OutputPanel.xaml.cs が種類から引く。ここは WPF に依存しない。
//
//  【出どころ】Output パネルの表示フィルタ（すべて / エンジン / ゲーム）に使う。
//  ゲーム = ユーザースクリプトの Debug.Log（PC は [Script] 付きの行、Android は logcat のタグ DOTNET）。
//
//  【2 通りの決め方】
//    - EditorLog.Write(本文) … 見た目の指定なし。OutputLineClassifier が本文の印（[cargo] 等）から決める（従来どおり）
//    - EditorLog.Write(本文, 見た目) … 書き手が決める（Android の実行の行。AndroidRun/AndroidRunOutputFormatter.cs）
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

namespace SEEDEditor.Logging;

/// <summary>Output パネルの行の色の種類。</summary>
public enum OutputTone
{
    /// <summary>通常の行。</summary>
    Default,

    /// <summary>実行先（ランタイム・Android の端末）からの通知。</summary>
    Runtime,

    /// <summary>ビルドの進み具合（工程の見出し・コンパイラ等の子プロセスの出力）。</summary>
    Build,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>エラー・失敗。</summary>
    Error,
}

/// <summary>Output パネルの行の出どころ（表示フィルタ用）。</summary>
public enum OutputSource
{
    /// <summary>エンジン・エディタ・ビルド・ランタイムの通知。</summary>
    Engine,

    /// <summary>ユーザースクリプトのログ（Debug.Log）。</summary>
    Game,
}

/// <summary>Output パネルの 1 行の見た目（色の種類と出どころ）。</summary>
/// <param name="Tone">色の種類。</param>
/// <param name="Source">出どころ。</param>
public readonly record struct OutputLineStyle(OutputTone Tone, OutputSource Source)
{
    /// <summary>エンジン側の行の見た目を作る（出どころを省略したときの既定）。</summary>
    /// <param name="tone">色の種類。</param>
    /// <returns>見た目。</returns>
    public static OutputLineStyle Engine(OutputTone tone) => new(tone, OutputSource.Engine);
}

/// <summary>EditorLog が Output パネルへ知らせる 1 行。</summary>
/// <param name="Line">表示する本文（時刻付き）。</param>
/// <param name="Style">書き手が決めた見た目（null なら本文から OutputLineClassifier が決める）。</param>
public sealed record EditorLogEntry(string Line, OutputLineStyle? Style);
