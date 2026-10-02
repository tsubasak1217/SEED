// ============================================================
//  PackagingWindow.DebugBuildMark.cs — パッケージ化ウィンドウの「ビルド種別」と「開発用のビルド」の欄
//
//  【欄】（設定ペインの「ビルド設定」の中）
//    Windows / macOS / iOS … 「ビルド種別」（Release / Debug）と、その下の「開発用のビルド」のチェック。
//        チェックの既定はビルド種別に合わせる（Debug なら入れる・Release なら入れない＝2026-10-01 からの挙動）。
//        既定と違う値を選んだときだけ packaging_settings.json の <platform>.debug_build_mark に上書きを保存し、
//        ビルド種別を選び直すと上書きを捨てて自動に戻す。決め方の正典は DebugBuildMarkPolicy。
//        下に「いまの決まり方」（自動か手で指定したか）を出し、Release で印を入れるときは注意を出す。
//    Android … ビルドの種類（開発用 / 配布用）で決まるので、チェックは押せない（見せるだけ）。
//
//  【反映】
//  値はその場で _data（PackagingData）へ書き、ビルドの開始時に packaging_settings.json へ保存される（他の欄と同じ）。
//  ビルドは MarksDebugBuild（PackagingWindow.xaml.cs）で同じ規則を引き、ログに「開発用のビルドの印: 入れる／入れない」を出す。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.Packaging;

public partial class PackagingWindow
{
    // ── 文言 ──────────────────────────────────────────────

    /// <summary>ビルド種別の選択肢: Release（cargo の --release）。</summary>
    private const string BuildTypeReleaseLabel = "Release";

    /// <summary>ビルド種別の選択肢: Debug（cargo の dev）。</summary>
    private const string BuildTypeDebugLabel = "Debug";

    /// <summary>ビルド種別の行のラベル。</summary>
    private const string BuildTypeRowLabel = "ビルド種別";

    /// <summary>開発用のビルドの印の行のラベル。</summary>
    private const string DebugMarkRowLabel = "開発用のビルド";

    // ── 見た目 ────────────────────────────────────────────

    /// <summary>チェックの文字色（「.NET ランタイムを同梱」などのチェックの行と同じ）。</summary>
    private static readonly SolidColorBrush DebugMarkCheckBrush = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>チェックの文字の大きさ（入力欄・コンボ〈SettingTextBox / SettingCombo〉と同じ）。</summary>
    private const double DebugMarkCheckFontSize = 11;

    // ── デスクトップ（Windows / macOS / iOS）────────────────────

    /// <summary>
    /// 「ビルド種別」の行と「開発用のビルド」の欄を足す（3 つのデスクトップのプラットフォームで共通）。
    /// ビルド種別を選び直すと、開発用のビルドの印の上書きを捨てて自動（ビルド種別に合わせる）に戻す。
    /// </summary>
    /// <param name="getBuildType">今のビルド種別を読む。</param>
    /// <param name="setBuildType">ビルド種別を書く。</param>
    /// <param name="getMark">印の上書きを読む（null ＝ 自動）。</param>
    /// <param name="setMark">印の上書きを書く。</param>
    private void AddBuildTypeAndDebugMarkRows(
        Func<BuildType> getBuildType, Action<BuildType> setBuildType,
        Func<bool?> getMark, Action<bool?> setMark)
    {
        // ビルド種別の行（選び直したら印を自動に戻して欄を更新する。更新の口は下で作るので後から入れる）
        Action? refreshMark = null;
        SettingsPane.Children.Add(BuildComboRow(BuildTypeRowLabel,
            [BuildTypeReleaseLabel, BuildTypeDebugLabel],
            getBuildType() == BuildType.Debug ? BuildTypeDebugLabel : BuildTypeReleaseLabel,
            v =>
            {
                var buildType = v == BuildTypeDebugLabel ? BuildType.Debug : BuildType.Release;
                if (buildType == getBuildType()) return;
                setBuildType(buildType);
                setMark(null);
                refreshMark?.Invoke();
            }));

        refreshMark = AddDesktopDebugMarkRows(getBuildType, getMark, setMark);
    }

    /// <summary>
    /// デスクトップの「開発用のビルド」のチェックと、その下の説明・注意を足す。
    /// </summary>
    /// <param name="getBuildType">今のビルド種別を読む。</param>
    /// <param name="getMark">印の上書きを読む（null ＝ 自動）。</param>
    /// <param name="setMark">印の上書きを書く。</param>
    /// <returns>表示を今の設定に合わせ直す口（ビルド種別を選び直したときに呼ぶ）。</returns>
    private Action AddDesktopDebugMarkRows(Func<BuildType> getBuildType, Func<bool?> getMark, Action<bool?> setMark)
    {
        var checkBox = NewDebugMarkCheckBox();
        SettingsPane.Children.Add(BuildLabeledRow(DebugMarkRowLabel, checkBox, DebugBuildMarkPolicy.CheckBoxText));

        // 説明と注意の置き場（設定が変わるたびに中身を作り直す）
        var detail = new StackPanel();
        SettingsPane.Children.Add(detail);

        // 画面からチェックを合わせ直している間は、変更の知らせを利用者の操作として扱わない
        var updating = false;

        void Refresh()
        {
            var buildType = getBuildType();
            var markOverride = getMark();
            updating = true;
            checkBox.IsChecked = DebugBuildMarkPolicy.Resolve(buildType, markOverride);
            updating = false;

            detail.Children.Clear();
            detail.Children.Add(BuildInfoBlock(DebugBuildMarkPolicy.DescribeDesktop(buildType, markOverride)));
            if (DebugBuildMarkPolicy.NeedsReleaseWarning(buildType, markOverride))
            {
                detail.Children.Add(BuildNoteBlock(DebugBuildMarkPolicy.ReleaseWarningText, PlatformAvailability.RequiresSetup));
            }
        }

        void OnChecked(bool chosen)
        {
            if (updating) return;
            // ビルド種別の既定と同じなら上書きを消す（packaging_settings.json に欄を書かない）
            setMark(DebugBuildMarkPolicy.OverrideFor(getBuildType(), chosen));
            Refresh();
        }

        checkBox.Checked   += (_, _) => OnChecked(true);
        checkBox.Unchecked += (_, _) => OnChecked(false);
        Refresh();
        return Refresh;
    }

    // ── Android ───────────────────────────────────────────

    /// <summary>
    /// Android の「開発用のビルド」の欄（ビルドの種類で決まるので押せない。決まり方を見せるだけ）。
    /// ビルドの種類を選び直すとペインごと作り直される（AddAndroidVariantRows）ので、ここは作るときの値だけ見ればよい。
    /// </summary>
    private void AddAndroidDebugMarkRows()
    {
        var variant = _data.Android.Variant;
        var checkBox = NewDebugMarkCheckBox();
        checkBox.IsChecked = DebugBuildMarkPolicy.ForAndroid(variant);
        checkBox.IsEnabled = false;
        SettingsPane.Children.Add(BuildLabeledRow(DebugMarkRowLabel, checkBox, DebugBuildMarkPolicy.CheckBoxText));
        SettingsPane.Children.Add(BuildInfoBlock(DebugBuildMarkPolicy.DescribeAndroid(variant)));
    }

    // ── 共通 ──────────────────────────────────────────────

    /// <summary>「開発用のビルド」のチェックを作る（文言は折り返す）。</summary>
    /// <returns>チェック。</returns>
    private static CheckBox NewDebugMarkCheckBox() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Foreground        = DebugMarkCheckBrush,
        FontSize          = DebugMarkCheckFontSize,
        ToolTip           = DebugBuildMarkPolicy.CheckBoxText,
        Content           = new TextBlock
        {
            Text         = DebugBuildMarkPolicy.CheckBoxText,
            TextWrapping = TextWrapping.Wrap,
        },
    };
}
