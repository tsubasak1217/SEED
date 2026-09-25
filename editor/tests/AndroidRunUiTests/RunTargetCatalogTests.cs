using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.State;
using SEEDEditor.AndroidRun;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>実行先セレクタの一覧（PC + 端末・選べない状態・案内の行）と前回の選択の復元・保存。</summary>
public static class RunTargetCatalogTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("一覧: PC が先頭・次に Android（自動）・端末は adb の順（実機／エミュレータの注記とアイコン）・案内の行は無し", ListsPcThenDevices);
        harness.Add("一覧: Android（自動）の行（Android の既定。規則と AVD の設定をツールチップに・端末が無くても選べる）", AndroidAutoEntry);
        harness.Add("一覧: unauthorized・offline の端末は選べず、ツールチップに理由と対処", NotReadyDevicesAreDisabled);
        harness.Add("一覧: ABI が合わない端末は選べない・ABI を読めなかった端末は選べる", AbiRules);
        harness.Add("一覧: Android を使えない環境は PC と理由の行だけ（選択は PC）", UnavailableShowsPcAndReason);
        harness.Add("一覧: 取り直し中は「探しています」、0 台は「見つかりません」、失敗は理由の行", NoticeRows);
        harness.Add("前回の選択（起動時）: 見えていて使える端末なら戻し、見えない・使えない端末は Android（自動）、pc・記録なしは PC", RestoresOnlyReadyVisibleDevice);
        harness.Add("いまの選択（取り直し）: 見えなくなった端末は「未接続」の行で選んだまま（エミュレータで実行できる）・使えない状態でも選んだまま", KeepsSelectionAcrossRefresh);
        harness.Add("前回の選択の保存: editor_target を書き、無ければ last_target を前回の選択とみなす・auto も覚える", StoresSelectionPerProject);
    }

    /// <summary>Android（自動）の行。</summary>
    private static void AndroidAutoEntry()
    {
        var catalog = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput { Android = AndroidTargetAvailability.Available });
        Check.Equal(2, catalog.Entries.Count, "端末の一覧を取る前でも PC と Android（自動）");
        var auto = catalog.Entries[1];
        Check.Equal(RunTargetKind.AndroidAuto, auto.Kind, "種類");
        Check.Equal("Android（自動）", auto.Text, "文言");
        Check.Equal(RunTargetEntry.AndroidAutoId, auto.Id, "識別子（実行状態へ記録する値）");
        Check.Equal("auto", auto.Id, "中核へ渡す serial と同じ語");
        Check.True(auto.IsAndroid && auto.IsAndroidAuto && auto.CanRun, "Android の行で、いつでも選べる");
        Check.Equal(RunTargetCatalogBuilder.AndroidIconKey, auto.IconKey, "Android のアイコン");
        Check.True(auto.ToolTip.Contains("実機") && auto.ToolTip.Contains("起動中のエミュレータ") && auto.ToolTip.Contains("seed_pixel6_api35"),
            $"規則と既定の AVD: {auto.ToolTip}");

        var configured = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available, EmulatorAvd = "wop", PreferredId = RunTargetEntry.AndroidAutoId,
        });
        Check.True(configured.Entries[1].ToolTip.Contains("「wop」") && configured.Entries[1].ToolTip.Contains("android.emulator_avd"),
            $"設定の AVD: {configured.Entries[1].ToolTip}");
        Check.Equal(RunTargetEntry.AndroidAutoId, configured.Selected.Id, "auto を選んでいれば Android（自動）");
    }

    /// <summary>PC が先頭で、端末は adb の順。</summary>
    private static void ListsPcThenDevices()
    {
        var catalog = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[] { Fixtures.ReadyEmulator(), Fixtures.ReadyPhone() },
        });

        Check.Equal(4, catalog.Entries.Count, "PC + Android（自動）+ 端末 2 台（案内の行は無し）");
        var pc = catalog.Entries[0];
        Check.Equal(RunTargetKind.Pc, pc.Kind, "先頭は PC");
        Check.Equal("PC", pc.Text, "PC の文言");
        Check.Equal(RunTargetCatalogBuilder.PcIconKey, pc.IconKey, "PC のアイコン");
        Check.True(pc.CanRun, "PC は選べる");
        Check.Equal(pc, catalog.Selected, "既定の選択は PC");
        Check.Equal(RunTargetKind.AndroidAuto, catalog.Entries[1].Kind, "2 番目は Android（自動）（端末より前）");

        var emulator = catalog.Entries[2];
        Check.Equal("emulator-5554（エミュレータ）", emulator.Text, "エミュレータの文言（シリアル＋種類。機種＝システムイメージ名は見分けに使えない）");
        Check.True(emulator.ToolTip.Contains("sdk_gphone64_x86_64"), "エミュレータの機種はツールチップに出す");
        Check.Equal(Fixtures.EmulatorSerial, emulator.Serial, "シリアル");
        Check.Equal(AdbDeviceKind.Emulator, emulator.DeviceKind, "種類");
        Check.Equal("x86_64", emulator.BuildAbi, "エミュレータは x86_64 でビルドする");
        Check.Equal(RunTargetCatalogBuilder.AndroidIconKey, emulator.IconKey, "端末のアイコン");
        Check.True(emulator.CanRun, "使える端末は選べる");
        Check.True(emulator.ToolTip.Contains("シリアル emulator-5554") && emulator.ToolTip.Contains("ABI x86_64"),
            $"ツールチップにシリアルと ABI: {emulator.ToolTip}");

        var phone = catalog.Entries[3];
        Check.Equal("Pixel_6a（実機）", phone.Text, "実機の文言");
        Check.Equal("arm64-v8a", phone.BuildAbi, "実機は arm64-v8a");
        Check.Equal(Fixtures.PhoneSerial, phone.Id, "端末の識別子はシリアル");
    }

    /// <summary>使えない状態の端末。</summary>
    private static void NotReadyDevicesAreDisabled()
    {
        var catalog = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[]
            {
                Fixtures.NotReady("R58M12345XY", AdbDeviceState.Unauthorized, "unauthorized"),
                Fixtures.NotReady("emulator-5556", AdbDeviceState.Offline, "offline"),
                Fixtures.NotReady("X1", AdbDeviceState.Other, "recovery"),
            },
        });
        var unauthorized = catalog.Entries[2];
        Check.Equal("R58M12345XY（未許可）", unauthorized.Text, "未許可の注記");
        Check.True(!unauthorized.CanRun, "未許可は選べない");
        Check.True(unauthorized.ToolTip.Contains("USB デバッグを許可"), $"対処を示す: {unauthorized.ToolTip}");
        Check.Equal("emulator-5556（応答なし）", catalog.Entries[3].Text, "応答なしの注記");
        Check.True(catalog.Entries[3].ToolTip.Contains("offline"), "offline の理由");
        Check.Equal("X1（recovery）", catalog.Entries[4].Text, "表に無い状態は adb の文字列のまま");
        Check.Equal(RunTargetEntry.PcId, catalog.Selected.Id, "選択は PC");
    }

    /// <summary>ABI の決まり。</summary>
    private static void AbiRules()
    {
        var catalog = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[]
            {
                Fixtures.Ready("OLD32", "Old_Phone", AdbDeviceKind.Physical, "armeabi-v7a,armeabi"),
                Fixtures.Ready("NOABI", "Quiet_Phone", AdbDeviceKind.Physical, null),
            },
        });
        var unsupported = catalog.Entries[2];
        Check.Equal("Old_Phone（ABI 非対応）", unsupported.Text, "ABI が合わない注記");
        Check.True(!unsupported.CanRun, "ABI が合わない端末は選べない");
        Check.True(unsupported.ToolTip.Contains("armeabi-v7a") && unsupported.ToolTip.Contains("arm64-v8a,x86_64"),
            $"端末の ABI とビルドできる ABI: {unsupported.ToolTip}");

        var unknown = catalog.Entries[3];
        Check.True(unknown.CanRun, "ABI を読めなかった使える端末は選べる（実行時に読む）");
        Check.True(unknown.BuildAbi is null, "ビルドする ABI は未定");
        Check.True(unknown.ToolTip.Contains("実行時"), $"実行時に読む旨: {unknown.ToolTip}");
    }

    /// <summary>Android を使えない環境。</summary>
    private static void UnavailableShowsPcAndReason()
    {
        const string reason = "Android SDK が見つかりません。環境変数 ANDROID_SDK_ROOT を設定してください。";
        var catalog = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Unavailable(reason),
            Devices = new[] { Fixtures.ReadyPhone() },
            PreferredId = Fixtures.PhoneSerial,
            Mode = RunTargetSelectionMode.Keep,
        });
        Check.Equal(2, catalog.Entries.Count, "PC と理由の行だけ（Android（自動）も端末も出さない）");
        var notice = catalog.Entries[1];
        Check.Equal(RunTargetKind.Notice, notice.Kind, "案内の行");
        Check.Equal(RunTargetCatalogBuilder.UnavailableText, notice.Text, "使えない旨");
        Check.Equal(reason, notice.ToolTip, "ツールチップが理由");
        Check.True(!notice.CanRun, "案内の行は選べない");
        Check.Equal(RunTargetCatalogBuilder.ProblemIconKey, notice.IconKey, "警告のアイコン");
        Check.Equal(RunTargetEntry.PcId, catalog.Selected.Id, "前回が端末でも PC（エラーにしない）");
    }

    /// <summary>案内の行。</summary>
    private static void NoticeRows()
    {
        var refreshing = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[] { Fixtures.ReadyPhone() },
            Refreshing = true,
        });
        Check.Equal(4, refreshing.Entries.Count, "前回の一覧（PC・自動・端末）を残したまま「探しています」を足す");
        Check.Equal(RunTargetCatalogBuilder.RefreshingText, refreshing.Entries[^1].Text, "探しています");
        Check.Equal(RunTargetCatalogBuilder.RefreshingNoticeId, refreshing.Entries[^1].Id, "識別子はシリアルと混ざらない");

        var none = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = Array.Empty<AndroidDeviceEntry>(),
        });
        Check.Equal(RunTargetCatalogBuilder.NoDevicesText, none.Entries[^1].Text, "0 台は見つからない旨");
        Check.True(none.Entries[^1].ToolTip.Contains("USB デバッグ"), "対処を示す");
        Check.True(none.Entries[^1].ToolTip.Contains("Android（自動）"), "自動ならエミュレータを起動して実行できる旨");

        var failed = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            ListError = "端末の一覧を取れません: adb server version doesn't match",
        });
        Check.Equal(RunTargetCatalogBuilder.ListErrorText, failed.Entries[^1].Text, "失敗の行");
        Check.True(failed.Entries[^1].ToolTip.Contains("server version"), "理由をツールチップに");

        var never = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput { Android = AndroidTargetAvailability.Available });
        Check.Equal(2, never.Entries.Count, "まだ一度も探していなければ PC と Android（自動）だけ（一覧を開くと探す）");
    }

    /// <summary>起動時の復元。</summary>
    private static void RestoresOnlyReadyVisibleDevice()
    {
        RunTargetCatalog Restore(string? preferred, params AndroidDeviceEntry[] devices) => RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = devices,
            PreferredId = preferred,
            PreferredName = "Pixel_6a",
            Mode = RunTargetSelectionMode.Restore,
        });

        Check.Equal(Fixtures.PhoneSerial, Restore(Fixtures.PhoneSerial, Fixtures.ReadyEmulator(), Fixtures.ReadyPhone()).Selected.Id,
            "見えていて使える端末は戻す");
        var invisible = Restore(Fixtures.PhoneSerial, Fixtures.ReadyEmulator());
        Check.Equal(RunTargetEntry.AndroidAutoId, invisible.Selected.Id, "見えなければ Android（自動）（前回 Android を使っていたので Android の既定へ）");
        Check.True(invisible.Entries.All(entry => !entry.IsMissing), "起動時は「未接続」の行を足さない");
        Check.Equal(RunTargetEntry.AndroidAutoId,
            Restore(Fixtures.PhoneSerial, Fixtures.NotReady(Fixtures.PhoneSerial, AdbDeviceState.Unauthorized, "unauthorized")).Selected.Id,
            "使えない状態なら Android（自動）");
        Check.Equal(RunTargetEntry.AndroidAutoId, Restore(RunTargetEntry.AndroidAutoId).Selected.Id, "auto を選んでいたら Android（自動）（一覧が無くても）");
        Check.Equal(RunTargetEntry.PcId, Restore(RunTargetEntry.PcId, Fixtures.ReadyPhone()).Selected.Id, "pc を選んでいたら PC");
        Check.Equal(RunTargetEntry.PcId, Restore(null, Fixtures.ReadyPhone()).Selected.Id, "記録が無ければ PC（勝手に端末を選ばない）");
    }

    /// <summary>取り直しでの選択の維持。</summary>
    private static void KeepsSelectionAcrossRefresh()
    {
        var missing = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[] { Fixtures.ReadyEmulator() },
            PreferredId = Fixtures.PhoneSerial,
            PreferredName = "Pixel_6a",
            Mode = RunTargetSelectionMode.Keep,
        });
        var selected = missing.Selected;
        Check.True(selected.IsMissing, "見えなくなった端末の行");
        Check.Equal("Pixel_6a（未接続）", selected.Text, "名前と未接続の注記");
        Check.True(selected.CanRun, "実行できる（見えなければエミュレータで実行する。段階C-3）");
        Check.True(selected.ToolTip.Contains(Fixtures.PhoneSerial) && selected.ToolTip.Contains("エミュレータ"),
            $"ツールチップにシリアルとエミュレータで実行する旨: {selected.ToolTip}");
        Check.True(missing.Entries.Contains(selected), "選んだ行は一覧の中にある");
        Check.Equal(4, missing.Entries.Count, "PC・自動・エミュレータ・未接続の行");

        var noName = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = Array.Empty<AndroidDeviceEntry>(),
            PreferredId = "ABC123",
            Mode = RunTargetSelectionMode.Keep,
        });
        Check.Equal("ABC123（未接続）", noName.Selected.Text, "名前が分からなければシリアル");
        Check.Equal(RunTargetCatalogBuilder.NoDevicesText, noName.Entries[^1].Text, "案内の行は最後");

        var unauthorized = RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = AndroidTargetAvailability.Available,
            Devices = new[] { Fixtures.NotReady(Fixtures.PhoneSerial, AdbDeviceState.Unauthorized, "unauthorized") },
            PreferredId = Fixtures.PhoneSerial,
            Mode = RunTargetSelectionMode.Keep,
        });
        Check.Equal(Fixtures.PhoneSerial, unauthorized.Selected.Id, "使えない状態でも選んだまま");
        Check.True(!unauthorized.Selected.CanRun, "ただし実行はできない");
    }

    /// <summary>前回の選択の保存と読み込み。</summary>
    private static void StoresSelectionPerProject()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        Check.Equal(RunTargetMemory.None, RunTargetSelectionStore.Load(temp.Path), "記録が無ければ記録なし");
        Check.Equal(RunTargetMemory.None, RunTargetSelectionStore.Load(null), "プロジェクトが無ければ記録なし");
        Check.True(!RunTargetSelectionStore.Save(string.Empty, RunTargetEntry.PcId), "プロジェクトが無ければ書かない");

        // SeedAndroid だけで実行していたプロジェクト: last_target が前回の選択
        var path = AndroidRunState.PathForProject(temp.Path);
        new AndroidRunState
        {
            LastTarget = new AndroidTargetRecord { Serial = Fixtures.PhoneSerial, Kind = "physical", Model = "Pixel_6a", ApplicationId = "a.b" },
        }.Save(path);
        var fromLast = RunTargetSelectionStore.Load(temp.Path);
        Check.Equal(Fixtures.PhoneSerial, fromLast.PreferredId, "last_target のシリアル");
        Check.Equal("Pixel_6a", fromLast.PreferredName, "機種");
        Check.True(fromLast.PrefersAndroidDevice, "端末を選んでいた");

        // PC を選ぶと editor_target に pc（last_target より優先）
        Check.True(RunTargetSelectionStore.Save(temp.Path, RunTargetEntry.PcId), "書いた");
        Check.True(!RunTargetSelectionStore.Save(temp.Path, RunTargetEntry.PcId), "同じ値なら書かない");
        var pc = RunTargetSelectionStore.Load(temp.Path);
        Check.Equal(RunTargetEntry.PcId, pc.PreferredId, "pc");
        Check.True(!pc.PrefersAndroidDevice, "PC を選んでいた");
        Check.Equal(Fixtures.PhoneSerial, AndroidRunState.Load(path).LastTarget?.Serial, "他の記録（last_target）は保つ");

        // 別の端末を選ぶ（機種は last_target と同じ端末のときだけ分かる）
        RunTargetSelectionStore.Save(temp.Path, Fixtures.EmulatorSerial);
        var emulator = RunTargetSelectionStore.Load(temp.Path);
        Check.Equal(Fixtures.EmulatorSerial, emulator.PreferredId, "エミュレータのシリアル");
        Check.True(emulator.PreferredName is null, "last_target と違う端末の機種は分からない");

        // Android（自動）を選ぶ: editor_target に auto。起動時に端末の一覧を取りに行かない（adb を起こさない）
        RunTargetSelectionStore.Save(temp.Path, RunTargetEntry.AndroidAutoId);
        var auto = RunTargetSelectionStore.Load(temp.Path);
        Check.Equal("auto", auto.PreferredId, "auto");
        Check.True(!auto.PrefersAndroidDevice, "自動は一覧が無くても戻せる");
        Check.Equal("auto", AndroidRunState.Load(path).EditorTarget, "run_state.json の editor_target");
    }
}
