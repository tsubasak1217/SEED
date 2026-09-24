// ============================================================
//  RunTargetEntry.cs — ツールバーの実行先セレクタ（実行ボタンの隣のコンボ）の 1 行
//
//  【行の種類】
//    Pc            … この PC（従来の Play。エディタに埋め込んだランタイム）
//    AndroidDevice … adb に見える Android の実機・エミュレータ（状態が device 以外・ABI が合わない端末は「選べない行」）
//    Notice        … 選べない案内の行（端末を探している・見つからない・Android を使えない理由）
//  行を作るのは RunTargetCatalogBuilder（純粋な処理）。表示（アイコン＋文言・ツールチップ・選べるか）は
//  MainWindow.xaml のコンボの ItemTemplate がこの型の値をそのまま出すだけ。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using SEEDEditor.Android.Adb;
using SEEDEditor.Android.State;

namespace SEEDEditor.AndroidRun;

/// <summary>実行先の行の種類。</summary>
public enum RunTargetKind
{
    /// <summary>この PC（従来の Play）。</summary>
    Pc,

    /// <summary>Android の端末（実機・エミュレータ）。</summary>
    AndroidDevice,

    /// <summary>選べない案内の行。</summary>
    Notice,
}

/// <summary>実行先セレクタの 1 行。</summary>
public sealed record RunTargetEntry
{
    /// <summary>PC の行の識別子（プロジェクトの実行状態へ「PC を選んだ」と記録する値と同じ）。</summary>
    public const string PcId = AndroidRunState.EditorPcTarget;

    /// <summary>識別子（PC は <see cref="PcId"/>、端末はシリアル、案内の行は "notice:…"）。</summary>
    public required string Id { get; init; }

    /// <summary>行の種類。</summary>
    public required RunTargetKind Kind { get; init; }

    /// <summary>名前（機種・シリアル。状態の注記を含まない。見えなくなった端末の行を作り直すときに使う）。</summary>
    public required string Name { get; init; }

    /// <summary>コンボに出す文言（名前＋種類や状態の注記。例「Pixel_6a（実機）」）。</summary>
    public required string Text { get; init; }

    /// <summary>ツールチップ（詳しい説明・選べない理由と対処）。</summary>
    public required string ToolTip { get; init; }

    /// <summary>この行で実行できるか（選べるか）。案内の行・使えない状態の端末は false。</summary>
    public required bool CanRun { get; init; }

    /// <summary>アイコンのキー（Icons.xaml。PC は Windows、端末は Android、案内は情報・警告）。</summary>
    public required string IconKey { get; init; }

    /// <summary>端末のシリアル（端末の行だけ）。</summary>
    public string? Serial { get; init; }

    /// <summary>端末の種類（端末の行だけ）。</summary>
    public AdbDeviceKind? DeviceKind { get; init; }

    /// <summary>この端末向けにビルドする ABI（端末の行で分かっているときだけ）。</summary>
    public string? BuildAbi { get; init; }

    /// <summary>前回選んだが、いま adb に見えない端末の行か（選択を保つためだけに残す行）。</summary>
    public bool IsMissing { get; init; }

    /// <summary>Android の端末の行か。</summary>
    public bool IsAndroid => Kind == RunTargetKind.AndroidDevice;

    /// <summary>コンボの閉じた状態・読み上げに使う文字列（ItemTemplate の無い場面の既定表示）。</summary>
    /// <returns>文言。</returns>
    public override string ToString() => Text;
}
