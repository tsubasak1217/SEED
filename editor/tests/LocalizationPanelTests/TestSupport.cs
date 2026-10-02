using SEEDEditor.Localization.Model;

namespace LocalizationPanelTests;

// ============================================================
//  TestSupport.cs — テストの下回り（リポジトリの場所・一時フォルダ・見本の表のモデル）
// ============================================================

/// <summary>リポジトリの中の場所。</summary>
internal static class RepoPaths
{
    /// <summary>リポジトリの根の目印（この 2 つのフォルダがある所）。</summary>
    private static readonly string[] Markers = { "templates", "scripting" };

    /// <summary>リポジトリの根（実行ファイルの場所から上へ探す）。</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Markers.All(m => Directory.Exists(Path.Combine(dir.FullName, m)))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("リポジトリの根（templates と scripting のあるフォルダ）が見つかりません");
        }
    }

    /// <summary>テンプレートのライブラリ（templates/）。</summary>
    public static string Templates => Path.Combine(Root, "templates");

    /// <summary>見本の表（templates/locale）。</summary>
    public static string TemplateLocale => Path.Combine(Templates, "locale");

    /// <summary>エディタの構成フォルダ（editor/config）。</summary>
    public static string EditorConfig => Path.Combine(Root, "editor", "config");
}

/// <summary>使い終わったら消す一時フォルダ。</summary>
internal sealed class TempFolder : IDisposable
{
    /// <summary>一時フォルダの絶対パス。</summary>
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seed_l10n_panel_" + Guid.NewGuid().ToString("N"));

    /// <summary>一時フォルダを作る。</summary>
    public TempFolder() => Directory.CreateDirectory(Path);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* 後片付けの失敗はテストの結果に関係しない */ }
    }
}

/// <summary>テストで使う表のモデルの作り方。</summary>
internal static class Models
{
    /// <summary>見本の表（templates/locale）をそのまま読んだモデル。</summary>
    public static LocaleTableModel Templates() => FromFolder(RepoPaths.TemplateLocale);

    /// <summary>フォルダの index.json と言語の表を読んだモデル。</summary>
    public static LocaleTableModel FromFolder(string folder) =>
        LocaleTableModel.Load(Read(System.IO.Path.Combine(folder, "index.json")), code => Read(System.IO.Path.Combine(folder, code + ".json")));

    /// <summary>文字列の表（ファイル名 → 中身）から作ったモデル。</summary>
    public static LocaleTableModel FromTexts(IReadOnlyDictionary<string, string> files) =>
        LocaleTableModel.Load(
            files.TryGetValue("index.json", out var index) ? LocaleFileContent.Read(index) : LocaleFileContent.Missing,
            code => files.TryGetValue(code + ".json", out var text) ? LocaleFileContent.Read(text) : LocaleFileContent.Missing);

    /// <summary>ファイルを読む（無ければ Missing）。</summary>
    private static LocaleFileContent Read(string path) =>
        File.Exists(path) ? LocaleFileContent.Read(File.ReadAllText(path)) : LocaleFileContent.Missing;

    /// <summary>
    /// 保存で書くファイルの中身を引く（無ければ null）。改行は "\n" にそろえて返す
    /// （見本の表は checkout の設定で CRLF になることがある。改行を保つことは DocumentTests で別に確かめる）。
    /// </summary>
    public static string? Pending(LocaleTableModel model, string fileName) =>
        model.PendingWrites().FirstOrDefault(f => f.FileName == fileName)?.Text.Replace("\r\n", "\n");

    /// <summary>行を引く。</summary>
    public static LocaleRow Row(LocaleTableModel model, string key) =>
        model.BuildRows().FirstOrDefault(r => r.Key == key) ?? throw new InvalidOperationException($"行 {key} がありません");

    /// <summary>行の言語の升目を引く。</summary>
    public static LocaleCell Cell(LocaleTableModel model, string key, string code) =>
        Row(model, key).Cells.First(c => c.Code == code);
}
