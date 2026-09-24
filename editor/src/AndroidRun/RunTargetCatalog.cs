// ============================================================
//  RunTargetCatalog.cs — 実行先セレクタの一覧と「どれを選んでおくか」を組み立てる（純粋な処理）
//
//  【一覧の並び】
//    1. PC（常に先頭。既定の実行先）
//    2. adb に見える端末（adb devices -l の順）。使えない状態（unauthorized / offline 等）と ABI が合わない端末は
//       「選べない行」にしてツールチップに理由と対処を書く
//    3. 前回選んだが見えなくなった端末（Keep のときだけ。選択を勝手に PC へ変えないため）
//    4. 案内の行（端末を探している・見つからない・一覧を取れない・Android を使えない理由）
//
//  【どれを選ぶか】
//    Restore（エディタの起動時。前回の選択を戻す）… 前回の端末が見えていて使える状態ならそれ、それ以外は PC
//    Keep（一覧の取り直し。いまの選択を保つ）      … 見えていればその行（使えない状態でも選んだまま）、
//                                                  見えなければ「未接続」の行を足してそれを選んだままにする
//  PC を選んでいれば常に PC。
//
//  Android を使えない環境（SDK / adb が無い・エンジンのリポジトリが無い・プロジェクトが無い）では PC と理由の行だけ
//  （エラーにはしない）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.AndroidRun;

/// <summary>選び方。</summary>
public enum RunTargetSelectionMode
{
    /// <summary>前回の選択を戻す（エディタの起動時）。見えない・使えない端末なら PC。</summary>
    Restore,

    /// <summary>いまの選択を保つ（一覧の取り直し）。見えなくなった端末も「未接続」の行で選んだままにする。</summary>
    Keep,
}

/// <summary>Android の実行先を使えるか（使えなければ理由）。</summary>
/// <param name="IsAvailable">使えるか。</param>
/// <param name="Reason">使えない理由と対処（使えるなら null）。</param>
public sealed record AndroidTargetAvailability(bool IsAvailable, string? Reason)
{
    /// <summary>使える。</summary>
    public static readonly AndroidTargetAvailability Available = new(true, null);

    /// <summary>使えない（理由付き）。</summary>
    /// <param name="reason">理由と対処。</param>
    /// <returns>使えない状態。</returns>
    public static AndroidTargetAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>一覧を組み立てる材料。</summary>
public sealed record RunTargetCatalogInput
{
    /// <summary>Android の実行先を使えるか。</summary>
    public required AndroidTargetAvailability Android { get; init; }

    /// <summary>最後に取れた端末の一覧（まだ一度も取っていなければ null）。</summary>
    public IReadOnlyList<AndroidDeviceEntry>? Devices { get; init; }

    /// <summary>いま一覧を取り直しているか（「探しています」の行を出す）。</summary>
    public bool Refreshing { get; init; }

    /// <summary>最後の一覧の取得が失敗した理由（成功していれば null）。</summary>
    public string? ListError { get; init; }

    /// <summary>選んでおきたいもの（<see cref="RunTargetEntry.PcId"/> か端末のシリアル。null なら PC）。</summary>
    public string? PreferredId { get; init; }

    /// <summary>選んでおきたい端末の名前（機種。見えなくなった端末の行の文言に使う。無ければシリアル）。</summary>
    public string? PreferredName { get; init; }

    /// <summary>選び方。</summary>
    public RunTargetSelectionMode Mode { get; init; } = RunTargetSelectionMode.Keep;
}

/// <summary>組み立てた一覧と選んでおく行。</summary>
/// <param name="Entries">一覧（表示の順）。</param>
/// <param name="Selected">選んでおく行（必ず一覧の中のどれか。既定は PC）。</param>
public sealed record RunTargetCatalog(IReadOnlyList<RunTargetEntry> Entries, RunTargetEntry Selected);

/// <summary>実行先セレクタの一覧を組み立てる。</summary>
public static class RunTargetCatalogBuilder
{
    // ── アイコン（Icons.xaml のキー）────────────────────────

    /// <summary>PC の行のアイコン。</summary>
    public const string PcIconKey = "Icon.Platform.Windows";

    /// <summary>端末の行のアイコン。</summary>
    public const string AndroidIconKey = "Icon.Platform.Android";

    /// <summary>案内の行（探している・見つからない）のアイコン。</summary>
    public const string NoticeIconKey = "Icon.Info";

    /// <summary>案内の行（使えない・失敗）のアイコン。</summary>
    public const string ProblemIconKey = "Icon.Warning";

    // ── 案内の行の識別子 ─────────────────────────────────

    /// <summary>案内の行の識別子の接頭辞（端末のシリアルと混ざらないように）。</summary>
    private const string NoticeIdPrefix = "notice:";

    /// <summary>Android を使えない理由の行。</summary>
    public const string UnavailableNoticeId = NoticeIdPrefix + "unavailable";

    /// <summary>端末を探している行。</summary>
    public const string RefreshingNoticeId = NoticeIdPrefix + "refreshing";

    /// <summary>端末が 1 台も無い行。</summary>
    public const string NoDevicesNoticeId = NoticeIdPrefix + "no-devices";

    /// <summary>一覧を取れなかった行。</summary>
    public const string ListErrorNoticeId = NoticeIdPrefix + "list-error";

    // ── 文言 ───────────────────────────────────────────

    /// <summary>PC の行の文言。</summary>
    public const string PcText = "PC";

    /// <summary>PC の行のツールチップ。</summary>
    public const string PcToolTip = "この PC で実行します（エディタに埋め込んだランタイムでの Play。従来どおり）。";

    /// <summary>文言の書式（{0}=名前、{1}=注記。例「Pixel_6a（実機）」）。</summary>
    private const string TextWithNoteFormat = "{0}（{1}）";

    /// <summary>使える端末のツールチップの書式（{0}=種類、{1}=機種、{2}=シリアル、{3}=ABI の説明）。</summary>
    private const string ReadyToolTipFormat =
        "{0}・{1}・シリアル {2}・{3}\n実行すると ビルド → インストール → 起動 → logcat を Output パネルへ流します（変更の無い工程は飛ばします）。";

    /// <summary>ABI の説明の書式（{0}=ビルドする ABI、{1}=端末の abilist）。</summary>
    private const string AbiDescriptionFormat = "ABI {0}（端末: {1}）";

    /// <summary>ABI を読めなかった端末の ABI の説明。</summary>
    private const string AbiUnknownDescription = "ABI は実行時に端末から読みます";

    /// <summary>機種が分からない端末の機種の表記。</summary>
    private const string UnknownModel = "機種不明";

    /// <summary>ABI が合わない端末の注記。</summary>
    private const string AbiUnsupportedNote = "ABI 非対応";

    /// <summary>ABI が合わない端末のツールチップの書式（{0}=端末の abilist、{1}=ビルドできる ABI）。</summary>
    private const string AbiUnsupportedToolTipFormat = "この端末の ABI（{0}）向けにはビルドできません（ビルドできる ABI: {1}）。";

    /// <summary>見えなくなった端末の注記。</summary>
    private const string MissingNote = "未接続";

    /// <summary>見えなくなった端末のツールチップの書式（{0}=シリアル）。</summary>
    private const string MissingToolTipFormat =
        "前回選んだ端末 {0} が adb に見えません。実機は USB（USB デバッグ）を、エミュレータは起動を確かめてから、一覧を開き直してください。";

    /// <summary>Android を使えない行の文言。</summary>
    public const string UnavailableText = "Android の端末は使えません";

    /// <summary>端末を探している行の文言。</summary>
    public const string RefreshingText = "端末を探しています…";

    /// <summary>端末を探している行のツールチップ。</summary>
    private const string RefreshingToolTip = "adb devices で実機・エミュレータを探しています。";

    /// <summary>端末が無い行の文言。</summary>
    public const string NoDevicesText = "Android の端末が見つかりません";

    /// <summary>端末が無い行のツールチップ。</summary>
    private const string NoDevicesToolTip =
        "実機は USB でつなぎ「USB デバッグ」を許可してください。エミュレータは Android Studio の Device Manager 等で起動してから、この一覧を開き直してください。";

    /// <summary>一覧を取れなかった行の文言。</summary>
    public const string ListErrorText = "端末の一覧を取れません";

    /// <summary>端末の状態ごとの注記（使えない状態の端末の行に出す）。</summary>
    private static readonly IReadOnlyDictionary<AdbDeviceState, string> StateNotes = new Dictionary<AdbDeviceState, string>
    {
        [AdbDeviceState.Unauthorized]  = "未許可",
        [AdbDeviceState.Offline]       = "応答なし",
        [AdbDeviceState.NoPermissions] = "権限なし",
        [AdbDeviceState.Authorizing]   = "許可の確認中",
        [AdbDeviceState.Connecting]    = "接続中",
    };

    /// <summary>PC の行（どの一覧でも同じ）。</summary>
    public static RunTargetEntry Pc { get; } = new()
    {
        Id = RunTargetEntry.PcId,
        Kind = RunTargetKind.Pc,
        Name = PcText,
        Text = PcText,
        ToolTip = PcToolTip,
        CanRun = true,
        IconKey = PcIconKey,
    };

    /// <summary>PC だけの一覧（初期状態）。</summary>
    public static RunTargetCatalog PcOnly { get; } = new(new[] { Pc }, Pc);

    /// <summary>
    /// 一覧と選んでおく行を組み立てる。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <returns>一覧。</returns>
    public static RunTargetCatalog Build(RunTargetCatalogInput input)
    {
        var entries = new List<RunTargetEntry> { Pc };
        var notices = new List<RunTargetEntry>();

        if (!input.Android.IsAvailable)
        {
            notices.Add(Notice(UnavailableNoticeId, UnavailableText, input.Android.Reason ?? UnavailableText, ProblemIconKey));
        }
        else
        {
            if (input.Devices is not null) entries.AddRange(input.Devices.Select(FromDevice));
            if (input.Refreshing)
            {
                notices.Add(Notice(RefreshingNoticeId, RefreshingText, RefreshingToolTip, NoticeIconKey));
            }
            else if (input.ListError is not null)
            {
                notices.Add(Notice(ListErrorNoticeId, ListErrorText, input.ListError, ProblemIconKey));
            }
            else if (input.Devices is { Count: 0 })
            {
                notices.Add(Notice(NoDevicesNoticeId, NoDevicesText, NoDevicesToolTip, NoticeIconKey));
            }
        }

        var selected = ChooseSelection(input, entries);
        entries.AddRange(notices);
        return new RunTargetCatalog(entries, selected);
    }

    /// <summary>
    /// 選んでおく行を決める（見えなくなった端末を Keep で保つときは、その行を一覧へ足す）。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <param name="entries">PC と端末の行（見えなくなった端末の行を足すことがある）。</param>
    /// <returns>選んでおく行。</returns>
    private static RunTargetEntry ChooseSelection(RunTargetCatalogInput input, List<RunTargetEntry> entries)
    {
        var preferred = input.PreferredId;
        if (string.IsNullOrWhiteSpace(preferred) || preferred == RunTargetEntry.PcId || !input.Android.IsAvailable)
        {
            return Pc;
        }

        var match = entries.FirstOrDefault(entry => entry.IsAndroid && string.Equals(entry.Serial, preferred, StringComparison.Ordinal));
        if (match is not null)
        {
            // 起動時は使える端末だけを戻す（使えない状態の端末を選んだまま起動すると、実行ボタンが理由なく押せないように見える）
            return input.Mode == RunTargetSelectionMode.Restore && !match.CanRun ? Pc : match;
        }
        if (input.Mode == RunTargetSelectionMode.Restore) return Pc;

        // Keep: いま見えない端末も選んだままにする（勝手に PC で実行してしまわないように。実行ボタンは理由付きで押せない）
        var missing = Missing(preferred, input.PreferredName);
        entries.Add(missing);
        return missing;
    }

    /// <summary>
    /// 端末 1 台の行を作る。
    /// </summary>
    /// <param name="entry">端末（ABI 付き）。</param>
    /// <returns>行。</returns>
    public static RunTargetEntry FromDevice(AndroidDeviceEntry entry)
    {
        var device = entry.Device;
        var name = DisplayName(device);
        string note;
        string toolTip;
        bool canRun;
        if (!device.IsReady)
        {
            note = StateNotes.TryGetValue(device.State, out var stateNote) ? stateNote : device.StateText;
            toolTip = AndroidDeviceSelector.DescribeNotReady(device);
            canRun = false;
        }
        else if (entry.AbiList is not null && entry.BuildAbi is null)
        {
            note = AbiUnsupportedNote;
            toolTip = string.Format(AbiUnsupportedToolTipFormat, entry.AbiList, AndroidAbis.Describe(AndroidAbis.Supported));
            canRun = false;
        }
        else
        {
            note = device.KindLabel;
            var abi = entry.BuildAbi is { } buildAbi
                ? string.Format(AbiDescriptionFormat, buildAbi.Name, entry.AbiList)
                : AbiUnknownDescription;
            toolTip = string.Format(ReadyToolTipFormat, device.KindLabel, device.Model ?? UnknownModel, device.Serial, abi);
            canRun = true;
        }

        return new RunTargetEntry
        {
            Id = device.Serial,
            Kind = RunTargetKind.AndroidDevice,
            Name = name,
            Text = string.Format(TextWithNoteFormat, name, note),
            ToolTip = toolTip,
            CanRun = canRun,
            IconKey = AndroidIconKey,
            Serial = device.Serial,
            DeviceKind = device.Kind,
            BuildAbi = entry.BuildAbi?.Name,
        };
    }

    /// <summary>
    /// 端末の名前（行の文言の前半）。実機は機種（adb の表記。例 Pixel_6a）、エミュレータはシリアル（例 emulator-5554）。
    /// エミュレータの機種はシステムイメージの名前（sdk_gphone64_x86_64 等）で見分けに使えず長いので、ツールチップにだけ出す。
    /// </summary>
    /// <param name="device">端末。</param>
    /// <returns>名前。</returns>
    public static string DisplayName(AdbDevice device) =>
        device.Kind == AdbDeviceKind.Emulator ? device.Serial : device.Model ?? device.Serial;

    /// <summary>見えなくなった端末の行を作る。</summary>
    /// <param name="serial">シリアル。</param>
    /// <param name="name">名前（機種。無ければシリアル）。</param>
    /// <returns>行。</returns>
    private static RunTargetEntry Missing(string serial, string? name)
    {
        var shown = string.IsNullOrWhiteSpace(name) ? serial : name;
        return new RunTargetEntry
        {
            Id = serial,
            Kind = RunTargetKind.AndroidDevice,
            Name = shown,
            Text = string.Format(TextWithNoteFormat, shown, MissingNote),
            ToolTip = string.Format(MissingToolTipFormat, serial),
            CanRun = false,
            IconKey = AndroidIconKey,
            Serial = serial,
            IsMissing = true,
        };
    }

    /// <summary>案内の行を作る。</summary>
    /// <param name="id">識別子。</param>
    /// <param name="text">文言。</param>
    /// <param name="toolTip">ツールチップ。</param>
    /// <param name="iconKey">アイコン。</param>
    /// <returns>行。</returns>
    private static RunTargetEntry Notice(string id, string text, string toolTip, string iconKey) => new()
    {
        Id = id,
        Kind = RunTargetKind.Notice,
        Name = text,
        Text = text,
        ToolTip = toolTip,
        CanRun = false,
        IconKey = iconKey,
    };
}
