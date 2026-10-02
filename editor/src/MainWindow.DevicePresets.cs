// ============================================================
//  MainWindow.DevicePresets.cs — 実行先「PC（端末の模擬: …）」の結線
//                                （端末プリセットの読み込み・起動に足す環境変数と引数・画面に収まるかの警告）
//
//  【役割】WPF の結線だけ。判断はすべて DevicePresets/ の WPF 非依存のクラス（単体テスト editor/tests/AndroidRunUiTests）:
//    - 端末の一覧（editor/config/device_presets.json）… DevicePresetCatalog（エディタの起動中に 1 回だけ読む）
//    - 実行先の行                                    … DevicePresetRunTargets（並べるのは RunTargetCatalogBuilder。
//                                                       一覧の材料に渡すのは MainWindow.AndroidRun.cs の BuildRunTargetCatalog。
//                                                       渡す一覧は LoadedDevicePresets）
//    - 起動に足す環境変数と起動引数                  … DevicePresetLaunchEnvironment（当てるのは RuntimeManager.LaunchAsync）
//    - 窓が画面に収まるか                            … DevicePresetScreenFit（画面の大きさはここで SystemParameters と DPI から読む）
//
//  【起動の流れ】実行ボタン → OnPlayBarPlayClick → PlayBarPolicy は PC と同じ TogglePc → OnPlayPause（MainWindow.xaml.cs）。
//  OnPlayPause は実行先が端末の模擬なら「ウィンドウを出してプレイ」の設定に関わらず別ウィンドウの Play にし（UsesEmbeddedPlay）、
//  PrepareDevicePresetLaunch の上書きを RuntimeManager.PlayLaunchOverrides に渡す。設定の値（EditorPreferences.WindowPlay）は変えない。
//  停止・一時停止・状態遷移は従来の別ウィンドウ Play と同じ（常駐の Play の使い回しは起動の条件が同じときだけ。
//  埋め込みの Play に戻るときは模擬で起動した常駐のプロセスを閉じる。RuntimeManager.PlayAsync・PlayRuntimeReusePolicy）。
//
//  【関連】docs/editor_device_presets.md
// ============================================================

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SEEDEditor.DevicePresets;
using SEEDEditor.ProjectSettings;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 表示文言（マジックストリングの一元化）────────────────────

    /// <summary>ログの頭。</summary>
    private const string DevicePresetLogPrefix = "[端末の模擬]";

    /// <summary>読み込みの完了のログの書式（{0}=頭、{1}=読んだファイル、{2}=件数）。</summary>
    private const string DevicePresetCatalogLoadedLogFormat = "{0} 端末プリセット読み込み完了 — source={1}  件数={2}";

    /// <summary>組み込みの 1 件へ落ちたときの source の表記。</summary>
    private const string DevicePresetBuiltInSourceLabel = "(組み込みの 1 件)";

    /// <summary>
    /// render_quality がランタイムの描画の品質のプリセットに無いときのログの書式
    /// （{0}=頭、{1}=端末の名前、{2}=名前、{3}=ランタイムのプリセットの一覧）。
    /// </summary>
    private const string DevicePresetUnknownQualityLogFormat =
        "{0} 端末「{1}」の render_quality '{2}' は描画の品質のプリセット（{3}）にありません（ランタイムは使わずに PC の既定で描く見込み）";

    /// <summary>プリセットの名前の一覧の区切り。</summary>
    private const string DevicePresetNameListSeparator = " / ";

    /// <summary>起動のログの書式（{0}=頭、{1}=端末の要約、{2}=環境変数と引数）。</summary>
    private const string DevicePresetLaunchLogFormat = "{0} {1} で別ウィンドウの Play を起動します: {2}";

    /// <summary>画面に収まらない警告のログの書式（{0}=頭、{1}=警告）。確かめ方を添える。</summary>
    private const string DevicePresetScreenFitLogFormat =
        "{0} {1}。実際の窓の大きさは起動ログ（[SEED INIT] 窓の大きさ…・描画面の寸法）で確かめてください（小さい端末のプリセットもあります）。";

    // ── 端末の一覧 ───────────────────────────────────────────

    /// <summary>端末プリセットの一覧（初めて使うときに 1 回だけ読む。JSON が無い・壊れていても組み込みの 1 件で必ず使える）。</summary>
    private static readonly Lazy<DevicePresetCatalog> _devicePresetCatalog = new(LoadDevicePresetCatalog);

    /// <summary>端末プリセットの一覧（実行先セレクタの「PC（端末の模擬: …）」の行の元）。</summary>
    private static DevicePresetCatalog LoadedDevicePresets => _devicePresetCatalog.Value;

    /// <summary>
    /// 端末プリセットを editor/config から読み、問題があればログへ出す（読み込み自体は失敗しない）。
    /// あわせて render_quality の名前がランタイムの描画の品質のプリセット（埋め込みの render_presets.json）にあるかを確かめる。
    /// </summary>
    /// <returns>一覧。</returns>
    private static DevicePresetCatalog LoadDevicePresetCatalog()
    {
        var catalog = DevicePresetCatalog.LoadFromDir(SEEDEditor.Settings.EditorPaths.ConfigDir);
        foreach (var warning in catalog.Warnings) EditorLog.Write($"{DevicePresetLogPrefix} {warning}");

        // 品質のプリセットの一覧を読めないとき（空）は確かめない（誤った警告を出さない）
        var qualities = RenderQualityPresetCatalog.Presets;
        if (qualities.Count > 0)
        {
            var known = string.Join(DevicePresetNameListSeparator, qualities.Select(quality => quality.Name));
            foreach (var preset in catalog.Presets)
            {
                if (preset.RenderQuality is { } name && RenderQualityPresetCatalog.Find(name) is null)
                {
                    EditorLog.Write(string.Format(DevicePresetUnknownQualityLogFormat, DevicePresetLogPrefix, preset.Name, name, known));
                }
            }
        }

        EditorLog.Write(string.Format(DevicePresetCatalogLoadedLogFormat,
            DevicePresetLogPrefix, catalog.SourcePath ?? DevicePresetBuiltInSourceLabel, catalog.Presets.Count));
        return catalog;
    }

    // ── 選んでいる実行先 ─────────────────────────────────────

    /// <summary>選んでいる実行先の端末プリセット（PC（端末の模擬）の行のときだけ。ほかの行では null）。</summary>
    private DevicePreset? SelectedDevicePreset =>
        _runTargets.Selected is { IsPcSimulated: true, DevicePreset: { } preset } ? preset : null;

    /// <summary>
    /// PC の Play を埋め込み（シーンパネルのランタイムをその場で Play 化）で行うか。
    /// 「ウィンドウを出してプレイ」がオフ（<see cref="_embeddedPlay"/>）でも、実行先が端末の模擬なら別ウィンドウの Play にする
    /// （模擬の条件はプロセスの起動時の環境変数で与えるため）。PC の実行中は実行先を変えられないので、
    /// Play の開始から Edit へ戻るまで同じ値になる（フォーカスの出し入れもこの値で決める）。
    /// </summary>
    private bool UsesEmbeddedPlay => _embeddedPlay && SelectedDevicePreset is null;

    // ── 起動 ─────────────────────────────────────────────────

    /// <summary>
    /// 実行先が PC（端末の模擬）なら、別ウィンドウの Play の起動に足す環境変数と起動引数を組み立てて Output に出し、
    /// 窓が画面に収まらなければトーストで警告する（起動は止めない）。ほかの実行先では何もしないで null。
    /// OnPlayPause が Play を始める直前に呼ぶ（UI スレッド）。
    /// </summary>
    /// <returns>起動に足すもの（端末の模擬でなければ null）。</returns>
    private RuntimeLaunchOverrides? PrepareDevicePresetLaunch()
    {
        var preset = SelectedDevicePreset;
        if (preset is null) return null;

        var overrides = DevicePresetLaunchEnvironment.Build(preset);
        EditorLog.Write(string.Format(DevicePresetLaunchLogFormat,
            DevicePresetLogPrefix, DevicePresetFormat.Summary(preset), overrides.Describe()));

        var (availableWidth, availableHeight) = MeasureAvailableClientAreaPx();
        if (DevicePresetScreenFit.Warn(preset, availableWidth, availableHeight) is { } warning)
        {
            ShowToast(warning);
            EditorLog.Write(string.Format(DevicePresetScreenFitLogFormat, DevicePresetLogPrefix, warning));
        }
        return overrides;
    }

    /// <summary>
    /// 窓の中身（描画面）に使える画面の大きさ（物理ピクセル）。主画面の作業領域（タスクバーを除く）から、
    /// 普通のウィンドウの枠とタイトルバー（SystemParameters.WindowNonClientFrameThickness）を除き、
    /// このウィンドウの DPI の倍率で画素へ直す（SystemParameters の値は DIP）。
    /// </summary>
    /// <returns>幅と高さ（物理ピクセル。0 未満にはしない）。</returns>
    private (int Width, int Height) MeasureAvailableClientAreaPx()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        var frame = SystemParameters.WindowNonClientFrameThickness;
        var width = (area.Width - frame.Left - frame.Right) * dpi.DpiScaleX;
        var height = (area.Height - frame.Top - frame.Bottom) * dpi.DpiScaleY;
        return ((int)Math.Max(0, Math.Floor(width)), (int)Math.Max(0, Math.Floor(height)));
    }
}
