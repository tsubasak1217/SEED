// ============================================================
//  TerrainSaveAsWindow.xaml.cs — 地形の「名前を付けて保存」ダイアログ
//
//  【責務】
//    地形一式（.tvox / .tscatter / .tcover）を置く「地形フォルダ」の保存先を
//    ユーザーに決めさせ、アセットルート相対のパス（例 `levels/forest/ground`）
//    を <see cref="ResultDir"/> として返す。
//
//  【なぜフォルダ選択＋名前入力なのか】
//    地形は単一ファイルではなく **フォルダ 1 つ** で表される資産であり、
//    SaveFileDialog では表現できない。親フォルダを選び、その下に作る／使う
//    フォルダ名を打つ、という 2 段構成が地形の実体に一致する。
//
//  【アセットルート外を拒否する理由】
//    パッケージング（assets.pak）はアセットルート配下だけを取り込む。
//    ルート外へ保存するとエディタでは動くのにビルドした実行ファイルで
//    地形が消える、という最も分かりにくい壊れ方をする。よってここで弾く。
//    最終的な判定はアセットルートの所在を知るランタイム側でも行う（二重の防御）。
// ============================================================

using System;
using System.IO;
using System.Windows;

namespace SEEDEditor.Terrain;

/// <summary>地形の保存先フォルダを選ぶモーダルダイアログ。</summary>
public partial class TerrainSaveAsWindow : Window
{
    // ── 定数 ──────────────────────────────────────────────────

    /// <summary>アセットルート相対パスの区切り文字（ランタイムの `assets://` 規約に合わせる）。</summary>
    private const char AssetPathSeparator = '/';

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>アセットルートの絶対パス（この配下しか選べない）。</summary>
    private readonly string _assetsRoot;

    /// <summary>
    /// 決定された地形フォルダ参照（アセットルート相対・スラッシュ区切り）。
    /// ダイアログが <c>true</c> で閉じたときだけ有効。
    /// </summary>
    public string ResultDir { get; private set; } = string.Empty;

    // ── 構築 ──────────────────────────────────────────────────

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="currentDir">
    /// 現在の地形フォルダ参照（アセットルート相対）。初期値として親フォルダ／名前欄へ分解して入れる。
    /// 空なら空欄で開く。
    /// </param>
    public TerrainSaveAsWindow(string assetsRoot, string currentDir)
    {
        InitializeComponent();
        _assetsRoot = assetsRoot;

        // 現在の参照を「親フォルダ / 名前」へ分解して初期表示にする。
        var (parent, name) = SplitDirRef(currentDir);
        TxtParentFolder.Text = parent;
        TxtFolderName.Text   = name;

        // どちらの欄を変えても保存先プレビューと OK ボタンの可否を更新する。
        TxtParentFolder.TextChanged += (_, _) => UpdatePreview();
        TxtFolderName.TextChanged   += (_, _) => UpdatePreview();
        UpdatePreview();

        // 名前欄からの入力が主なので、そこにフォーカスを置く。
        Loaded += (_, _) => { TxtFolderName.Focus(); TxtFolderName.SelectAll(); };
    }

    // ── 純粋ヘルパー（UI 非依存・テストしやすい形） ─────────────

    /// <summary>
    /// 地形フォルダ参照（`a/b/c`）を「親フォルダ（`a/b`）」と「名前（`c`）」へ分解する。
    /// 区切りが無ければ親は空文字。
    /// </summary>
    internal static (string Parent, string Name) SplitDirRef(string dirRef)
    {
        var normalized = NormalizeSeparators(dirRef).Trim(AssetPathSeparator);
        if (normalized.Length == 0) return (string.Empty, string.Empty);
        int idx = normalized.LastIndexOf(AssetPathSeparator);
        return idx < 0
            ? (string.Empty, normalized)
            : (normalized[..idx], normalized[(idx + 1)..]);
    }

    /// <summary>パス区切りをアセットパス規約（'/'）へ統一する。</summary>
    internal static string NormalizeSeparators(string path)
        => (path ?? string.Empty).Replace('\\', AssetPathSeparator);

    /// <summary>
    /// 親フォルダと名前から地形フォルダ参照を組み立てる。
    /// 空要素・`.`・重複スラッシュは取り除く。組み立てられなければ空文字。
    /// </summary>
    internal static string CombineDirRef(string parent, string name)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var raw in NormalizeSeparators(parent).Split(AssetPathSeparator))
        {
            var p = raw.Trim();
            if (p.Length == 0 || p == ".") continue;
            parts.Add(p);
        }
        foreach (var raw in NormalizeSeparators(name).Split(AssetPathSeparator))
        {
            var p = raw.Trim();
            if (p.Length == 0 || p == ".") continue;
            parts.Add(p);
        }
        return string.Join(AssetPathSeparator, parts);
    }

    /// <summary>
    /// 地形フォルダ参照を検証する。問題なければ null、あれば表示用の理由文字列を返す。
    ///
    /// ランタイム側（`terrain::dir_ref::normalize`）と同じ拒否条件を UX のために先取りする。
    /// 正の判定はあくまでランタイム側にある（アセットルートの所在を知っているのはあちら）。
    /// </summary>
    internal static string? ValidateDirRef(string dirRef)
    {
        if (string.IsNullOrWhiteSpace(dirRef))
            return "フォルダ名を入力してください。";

        foreach (var part in dirRef.Split(AssetPathSeparator))
        {
            if (part == "..")
                return "アセットルート外（.. を含むパス）は保存先にできません。";
            // Windows で使えない文字が混ざっていると保存時に初めて失敗する。ここで止める。
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return $"フォルダ名に使えない文字が含まれています: {part}";
        }
        if (Path.IsPathRooted(dirRef))
            return "アセットルート外（絶対パス）は保存先にできません。";

        return null;
    }

    /// <summary>
    /// アセットルート配下の絶対パスをアセットルート相対へ落とす。
    /// ルート外・ルートそのものは null を返す（呼び出し側がエラー表示する）。
    /// </summary>
    internal static string? ToRelativeUnderRoot(string assetsRoot, string absolutePath)
    {
        var root = NormalizeSeparators(assetsRoot).TrimEnd(AssetPathSeparator);
        var abs  = NormalizeSeparators(absolutePath).TrimEnd(AssetPathSeparator);
        if (root.Length == 0 || abs.Length == 0) return null;

        // Windows のパス比較は大文字小文字を区別しない。
        if (!abs.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (abs.Length == root.Length) return string.Empty; // ルートそのもの＝相対パスは空
        if (abs[root.Length] != AssetPathSeparator) return null; // 兄弟ディレクトリの前方一致を弾く

        return abs[(root.Length + 1)..];
    }

    // ── UI 更新 ───────────────────────────────────────────────

    /// <summary>保存先プレビューとエラー表示、OK ボタンの可否を現在の入力から作り直す。</summary>
    private void UpdatePreview()
    {
        var dirRef = CombineDirRef(TxtParentFolder.Text, TxtFolderName.Text);
        var error  = ValidateDirRef(dirRef);

        if (error != null)
        {
            TxtPreview.Text = string.Empty;
            TxtError.Text   = error;
            BtnOk.IsEnabled = false;
            return;
        }

        // 実際に書かれる場所を必ず見せる（仮想パスと実パスの両方）。
        var abs = Path.Combine(_assetsRoot, dirRef.Replace(AssetPathSeparator, Path.DirectorySeparatorChar));
        TxtPreview.Text = $"assets://{dirRef}\n{abs}";
        // 既存フォルダを選んだ場合の上書き注意（削除はしないが混在はしうる）。
        TxtError.Text = Directory.Exists(abs)
            ? "※ 既存のフォルダです。同名のチャンクファイルは上書きされます。"
            : string.Empty;
        BtnOk.IsEnabled = true;
    }

    // ── イベント ──────────────────────────────────────────────

    /// <summary>「参照」ボタン: 親フォルダをフォルダ選択ダイアログで選ぶ（アセットルート内のみ）。</summary>
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "地形フォルダを置く親フォルダを選択（アセットフォルダ内）",
            InitialDirectory = _assetsRoot,
            Multiselect      = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        var rel = ToRelativeUnderRoot(_assetsRoot, dlg.FolderName);
        if (rel == null)
        {
            MessageBox.Show(this,
                "アセットフォルダの外は選べません（パッケージングに含まれないため）。",
                "保存先が不正", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TxtParentFolder.Text = rel; // 空文字＝アセットルート直下
    }

    /// <summary>「保存」ボタン: 参照を確定してダイアログを閉じる。</summary>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        var dirRef = CombineDirRef(TxtParentFolder.Text, TxtFolderName.Text);
        var error  = ValidateDirRef(dirRef);
        if (error != null)
        {
            TxtError.Text = error;
            return;
        }
        ResultDir    = dirRef;
        DialogResult = true;
    }
}
