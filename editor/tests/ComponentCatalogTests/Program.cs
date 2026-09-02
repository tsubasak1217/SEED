using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ComponentCatalogTests;

/// <summary>
/// コンポーネント追加ダイアログの「既定名」まわりの単体テスト。
///
/// <para>直した不具合:</para>
/// <para>
/// 「コンポーネント追加」で選んだ種別と違う、別のコンポーネントの名前が
/// 既定名として入ることがあった。原因は一覧データ（表示名）と
/// 既定名テーブル（switch 文）という 2 つの情報源のずれ。
/// 一覧行を選ぶと名前欄へ<b>表示名</b>が書き込まれるのに、
/// 「ユーザーが編集していないか」の判定には<b>switch の既定名</b>を使っていたため、
/// 表示名と既定名が異なる種別（例: 表示 "Water Volume" / 既定名 "Water"）を
/// いったん選ぶと、次に別の種別を選んでも判定が成立せず、
/// 前の種別の名前のまま ADD_COMPONENT が送られていた。
/// </para>
/// <para>
/// 本テストは ComponentCatalog を唯一の情報源とした後の不変条件を、
/// 全種別の総当たりで固定する。
/// </para>
/// </summary>
public static class Program
{
    public static int Main()
    {
        var h = new TestHarness();
        var all = ComponentCatalog.AllEntries.ToList();

        // ── カタログ自体の健全性 ─────────────────────────────────

        h.Add("カタログが空でない", () =>
        {
            Check.True(all.Count > 0, "コンポーネントが 1 件も登録されていない");
        });

        h.Add("型 ID が重複しない", () =>
        {
            var dups = all.GroupBy(e => e.TypeId)
                          .Where(g => g.Count() > 1)
                          .Select(g => g.Key)
                          .ToList();
            Check.True(dups.Count == 0, $"型 ID が重複している: {string.Join(", ", dups)}");
        });

        h.Add("全エントリが空でない既定名を持つ", () =>
        {
            foreach (var e in all)
            {
                Check.True(!string.IsNullOrWhiteSpace(e.DefaultName),
                    $"{e.TypeId} の既定名が空");
            }
        });

        h.Add("既定名に空白を含まない（スロット名は識別子的に保つ）", () =>
        {
            foreach (var e in all)
            {
                Check.True(!e.DefaultName.Contains(' '),
                    $"{e.TypeId} の既定名 '{e.DefaultName}' に空白が含まれる");
            }
        });

        h.Add("DefaultNameOf がカタログの値と一致する", () =>
        {
            foreach (var e in all)
            {
                Check.Equal(e.DefaultName, ComponentCatalog.DefaultNameOf(e.TypeId),
                    $"{e.TypeId} の既定名");
            }
        });

        h.Add("未知の型 ID は型 ID をそのまま返す", () =>
        {
            Check.Equal("UnknownComponent",
                ComponentCatalog.DefaultNameOf("UnknownComponent"), "未知型の既定名");
        });

        h.Add("プラグインは接頭辞を外した名前が既定名になる", () =>
        {
            Check.Equal("MyPlugin", ComponentCatalog.DefaultNameOf("Plugin:MyPlugin"),
                "プラグインの既定名");

            var entries = ComponentCatalog.PluginEntries(new[] { "MyPlugin" });
            Check.Equal(1, entries.Count, "プラグインエントリ数");
            Check.Equal("Plugin:MyPlugin", entries[0].TypeId, "プラグインの型 ID");
            Check.Equal("MyPlugin", entries[0].DefaultName, "プラグインの既定名");
        });

        // ── 自動リネーム（本不具合の回帰テスト）────────────────────

        h.Add("初回選択では新しい種別の既定名が入る", () =>
        {
            foreach (var e in all)
            {
                Check.Equal(e.DefaultName,
                    ComponentCatalog.NextDefaultName("", null, e.TypeId),
                    $"{e.TypeId} を最初に選んだときの名前");
            }
        });

        h.Add("【回帰】どの種別から選び直しても、選んだ種別の既定名になる", () =>
        {
            // 全順序対の総当たり。旧実装は「表示名 != 既定名」の種別
            // （Water Volume / Audio Source / Collider 2D / ジョイントアタッチ 等）を
            // 経由すると、ここで前の種別の名前を返して落ちる。
            foreach (var prev in all)
            {
                // 前の種別を選んだ直後の名前欄の状態（＝前の種別の既定名）。
                var textAfterPrev = ComponentCatalog.NextDefaultName("", null, prev.TypeId);
                Check.Equal(prev.DefaultName, textAfterPrev, $"{prev.TypeId} 選択直後の名前");

                foreach (var next in all)
                {
                    var actual = ComponentCatalog.NextDefaultName(
                        textAfterPrev, prev.TypeId, next.TypeId);
                    Check.Equal(next.DefaultName, actual,
                        $"{prev.TypeId} → {next.TypeId} と選び直したときの名前");
                }
            }
        });

        h.Add("ユーザーが手で入力した名前は種別を変えても保たれる", () =>
        {
            const string typed = "MyCustomName";
            foreach (var prev in all.Take(5))
            {
                foreach (var next in all.Take(5))
                {
                    Check.Equal(typed,
                        ComponentCatalog.NextDefaultName(typed, prev.TypeId, next.TypeId),
                        $"{prev.TypeId} → {next.TypeId} で手入力名が失われた");
                }
            }
        });

        h.Add("名前欄を空にすると新しい種別の既定名が戻る", () =>
        {
            var e = all[0];
            Check.Equal(e.DefaultName,
                ComponentCatalog.NextDefaultName("", "SomeOtherComponent", e.TypeId),
                "空欄からの既定名復帰");
        });

        h.Add("プラグインとの相互の選び直しでも名前が正しく追従する", () =>
        {
            var plugin = ComponentCatalog.PluginEntries(new[] { "MyPlugin" })[0];
            var model  = all.First(e => e.TypeId == "ModelComponent");

            // 通常種別 → プラグイン
            var afterModel = ComponentCatalog.NextDefaultName("", null, model.TypeId);
            Check.Equal(plugin.DefaultName,
                ComponentCatalog.NextDefaultName(afterModel, model.TypeId, plugin.TypeId),
                "Model → プラグイン");

            // プラグイン → 通常種別
            var afterPlugin = ComponentCatalog.NextDefaultName("", null, plugin.TypeId);
            Check.Equal(model.DefaultName,
                ComponentCatalog.NextDefaultName(afterPlugin, plugin.TypeId, model.TypeId),
                "プラグイン → Model");
        });

        Console.WriteLine("ComponentCatalog テスト");
        Console.WriteLine();
        return h.Run();
    }
}
