// ============================================================
//  Program.cs — ヒエラルキーの行の目アイコンが「今その行にいるアクタ」の表示を切り替えるかの確認（オフスクリーン）
//
//  【確かめる不具合（2026-10-03 の 2 回目のレビュー #12）】
//  行の目アイコンのクリックは、行を作ったときの node.Id・node.SelfVisible をラムダで握っていた。
//  差分更新は安定キーが同じ行を使い回し（TreeViewItem.Tag だけ新しいノードへ差し替える）、HeaderDiffers は Id を比べないので
//  見出し（目アイコンを含む）を作り直さない。R{A,B,C} で A を消すと B の行は使い回され、目は古い 1 番を握ったまま。
//  B の目を押すと SET_VISIBLE:1,… が送られ、今 1 番にいる **C** が隠れ、普通の編集として積まれて保存される。
//
//  【やること】
//  本物の HierarchyPanel にヒエラルキーの JSON を 2 回流し（行の使い回しを起こし）、行の目アイコンへ
//  PreviewMouseLeftButtonDown を流して、ランタイムへ送られた SET_VISIBLE の番号と値を確かめる。
//  ランタイムは起動しない（存在しない exe の RuntimeManager。送信はパイプが無いのでログへ書くだけ。その行を拾う）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SEEDEditor.Panels;
using SEEDEditor.Runtime;
using SEEDEditor.Runtime.BuildConfig;

namespace SEEDEditor.Tests.HierarchyPanelProbe;

/// <summary>ヒエラルキーの行の目アイコンの検証の入口。</summary>
public static class Program
{
    /// <summary>存在しないランタイムの exe（RuntimeManager を作るためだけ。起動しない）。</summary>
    private const string NoRuntimeExePath = @"C:\seed_hierarchy_probe_no_runtime\seed_runtime.exe";

    /// <summary>RuntimeManager.SendToRuntime がログへ書く行の印（この後ろが送ったコマンド）。</summary>
    private const string SentLogMarker = "[Editor→Runtime] ";

    /// <summary>拾う送信の接頭辞（目アイコンが送る表示切替）。</summary>
    private const string SetVisiblePrefix = "SET_VISIBLE:";

    /// <summary>パネルの JSON の受け口（ランタイムの HierarchyUpdated の購読先。非公開なので反射で呼ぶ）。</summary>
    private const string HierarchyUpdatedHandlerName = "OnHierarchyUpdated";

    /// <summary>本体のログの型（internal。送信の行を拾うため反射で購読する）。</summary>
    private const string EditorLogTypeName = "SEEDEditor.EditorLog";

    /// <summary>ログの行が足されたときのイベントの名前。</summary>
    private const string LogWrittenEventName = "LogWritten";

    /// <summary>パネルのツリーの x:Name。</summary>
    private const string ActorTreeName = "ActorTree";

    /// <summary>Dispatcher のキューを空にするために回す回数。</summary>
    private const int DispatcherPumpCount = 8;

    /// <summary>表明の失敗数。</summary>
    private static int _failures;

    /// <summary>送られた SET_VISIBLE（ログから拾う。古い順）。</summary>
    private static readonly List<string> SentSetVisible = new();

    /// <summary>各場面を順に確かめる。</summary>
    /// <returns>終了コード（表明が全部通ったら 0）。</returns>
    [STAThread]
    public static int Main()
    {
        // App を生成すると App.xaml のリソース（アイコン・共通書式）が載る。Run() は呼ばない。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        // EditorLog は本体の internal なので、公開の LogWritten イベントへ反射で購読する
        var logType = typeof(SEEDEditor.App).Assembly.GetType(EditorLogTypeName)
                      ?? throw new InvalidOperationException($"{EditorLogTypeName} が見つからない（名前が変わった）");
        var logWritten = logType.GetEvent(LogWrittenEventName, BindingFlags.Public | BindingFlags.Static)
                         ?? throw new InvalidOperationException($"{EditorLogTypeName}.{LogWrittenEventName} が見つからない");
        logWritten.AddEventHandler(null, new Action<SEEDEditor.Logging.EditorLogEntry>(entry =>
        {
            int at = entry.Line.IndexOf(SentLogMarker, StringComparison.Ordinal);
            if (at < 0) return;
            var command = entry.Line[(at + SentLogMarker.Length)..];
            if (command.StartsWith(SetVisiblePrefix, StringComparison.Ordinal)) SentSetVisible.Add(command);
        }));

        var runtime = new RuntimeManager(NoRuntimeExePath, new RuntimeBuildConfig());

        Console.WriteLine("=== ヒエラルキーの行の目アイコン（2 回目のレビュー #12）===");

        // ── 1: 手前のアクタを消す（R{A,B,C} → R{B,C}）。B の行は使い回され、B は 0 番になる ─────────
        RunScenario(runtime,
            "01: 手前を消した後の B の目は、今の B（0 番）を切り替える",
            before: """[{"id":0,"name":"A","parent":null},{"id":1,"name":"B","parent":null},{"id":2,"name":"C","parent":null}]""",
            after:  """[{"id":0,"name":"B","parent":null},{"id":1,"name":"C","parent":null}]""",
            rowName: "B",
            expected: "SET_VISIBLE:0,0");

        // ── 2: 手前へアクタが増える（貼り付け・Undo）。B の行は使い回され、B は 2 番になる ─────────
        RunScenario(runtime,
            "02: 手前へ増えた後の B の目は、今の B（2 番）を切り替える",
            before: """[{"id":0,"name":"A","parent":null},{"id":1,"name":"B","parent":null}]""",
            after:  """[{"id":0,"name":"A","parent":null},{"id":1,"name":"N","parent":null},{"id":2,"name":"B","parent":null}]""",
            rowName: "B",
            expected: "SET_VISIBLE:2,0");

        // ── 3: 自分が非表示の行（送る値は自分のフラグの反転 = 1）。番号のずれと同時 ─────────────
        RunScenario(runtime,
            "03: 非表示の B の目は、今の B（0 番）を表示に戻す（自分のフラグの反転）",
            before: """[{"id":0,"name":"A","parent":null},{"id":1,"name":"B","parent":null,"visible":false,"self_visible":false}]""",
            after:  """[{"id":0,"name":"B","parent":null,"visible":false,"self_visible":false}]""",
            rowName: "B",
            expected: "SET_VISIBLE:0,1");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "すべての表明が通りました" : $"表明の失敗 {_failures} 件");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 1 つの場面: 新しいパネルへ before → after の順に木を流し、after の rowName の行の目を押して、送られた SET_VISIBLE を確かめる。
    /// </summary>
    /// <param name="runtime">送信先（起動していない RuntimeManager）。</param>
    /// <param name="title">場面の名前（表示用）。</param>
    /// <param name="before">1 回目の木（行を作る）。</param>
    /// <param name="after">2 回目の木（差分更新で行を使い回す）。</param>
    /// <param name="rowName">目を押す行の名前。</param>
    /// <param name="expected">送られるべきコマンド。</param>
    private static void RunScenario(RuntimeManager runtime, string title, string before, string after, string rowName, string expected)
    {
        var panel = new HierarchyPanel();
        panel.SetRuntime(runtime);
        var tree = (TreeView)panel.FindName(ActorTreeName);

        PushHierarchy(panel, before);
        var rowBefore = FindRow(tree, rowName);

        PushHierarchy(panel, after);
        var rowAfter = FindRow(tree, rowName);

        // 前提: 差分更新が行を使い回したこと（作り直されたなら、この場面は不具合の条件を作れていない）
        Expect(rowBefore is not null && ReferenceEquals(rowBefore, rowAfter), $"{title}: 前提（{rowName} の行が使い回される）");
        if (rowAfter is null) return;

        int sentBefore = SentSetVisible.Count;
        ClickEye(rowAfter);
        var sent = SentSetVisible.Skip(sentBefore).ToList();
        Expect(sent.Count == 1 && sent[0] == expected,
            $"{title}（期待 {expected} / 実際 {(sent.Count == 0 ? "送信なし" : string.Join(" ", sent))}）");
    }

    /// <summary>ランタイムからヒエラルキーが届いたのと同じ受け口へ JSON を渡し、反映まで Dispatcher を回す。</summary>
    /// <param name="panel">対象のパネル。</param>
    /// <param name="json">ヒエラルキーの JSON（ランタイムの HIERARCHY と同じ形）。</param>
    private static void PushHierarchy(HierarchyPanel panel, string json)
    {
        var handler = typeof(HierarchyPanel).GetMethod(HierarchyUpdatedHandlerName, BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new InvalidOperationException($"HierarchyPanel.{HierarchyUpdatedHandlerName} が見つからない（名前が変わった）");
        handler.Invoke(panel, [json]);
        Pump();
    }

    /// <summary>ツリーの行（TreeViewItem）を Tag の名前で探す（ルート階層だけ。場面はルートだけで組む）。</summary>
    /// <param name="tree">パネルのツリー。</param>
    /// <param name="name">行のアクタの名前。</param>
    /// <returns>見つかった行（無ければ null）。</returns>
    private static TreeViewItem? FindRow(TreeView tree, string name)
        => tree.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is ActorNode n && n.Name == name);

    /// <summary>行の見出しの先頭の目アイコンへ左クリック（PreviewMouseLeftButtonDown）を流す。</summary>
    /// <param name="row">押す行。</param>
    private static void ClickEye(TreeViewItem row)
    {
        if (row.Header is not TextBlock header ||
            header.Inlines.OfType<InlineUIContainer>().FirstOrDefault()?.Child is not UIElement eye)
        {
            Expect(false, "行の見出しの先頭に目アイコンが無い（見出しの組み立てが変わった）");
            return;
        }
        eye.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = eye,
        });
        Pump();
    }

    /// <summary>Dispatcher のキューを空にする（BeginInvoke で積まれた反映を走らせる）。</summary>
    private static void Pump()
    {
        for (var i = 0; i < DispatcherPumpCount; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>表明（失敗なら数えて表示する）。</summary>
    /// <param name="condition">成り立つべき条件。</param>
    /// <param name="message">表明の説明。</param>
    private static void Expect(bool condition, string message)
    {
        Console.WriteLine($"  [{(condition ? " OK " : "FAIL")}] {message}");
        if (!condition) _failures++;
    }
}
