using System;
using System.IO;
using SEEDEditor.AI;
using SEEDEditor.Assets;
using SEEDEditor.Scene;
using SeedMcpServer;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace AiSafetyTests;

/// <summary>
/// AI ブリッジ／MCP の安全機構の単体テスト。
///
/// <para>
/// 事故（利用者のエディタが AI コマンドを受け取り、別シーンの内容で .scene を
/// 上書きしたうえで終了させられた）の再発を、コードの側から止められていることを確認する。
/// 検証の柱:
/// </para>
/// <list type="number">
///   <item>トークン検証 — トークンを持つインスタンスは一致しない要求を通さない</item>
///   <item>読み取り専用の既定 — 対話エディタは変更系コマンドを通さない</item>
///   <item>MCP の束縛 — 起動していないインスタンスへ変更系ツールを投げない</item>
///   <item>空きポート選択 — 既定ポートを避けた専用レンジから空きを選べる</item>
///   <item>シーンロック — 死んだプロセスのロックは無効、生きているロックは有効</item>
///   <item>バックアップ世代 — 最新 N 件だけ残し、別ファイルの世代を巻き込まない</item>
/// </list>
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── 1. トークン検証 ──
        harness.Add("トークン未設定なら誰でも通す（既定エディタ）",       TokenNotConfiguredAllowsAnyone);
        harness.Add("トークン設定時は一致しない要求を拒否する",           TokenMismatchIsRejected);
        harness.Add("トークン設定時は一致する要求だけ通す",               TokenMatchIsAccepted);

        // ── 2. 読み取り専用の既定 ──
        harness.Add("対話エディタの既定は読み取り専用（変更系を拒否）",   InteractiveDefaultsToReadOnly);
        harness.Add("観測系コマンドは読み取り専用でも通る",               ReadOnlyCommandsAlwaysAllowed);
        harness.Add("パネル内蔵 AI（利用者操作）は常に許可される",        UserInitiatedAlwaysAllowed);
        harness.Add("ヘッドレスは既定で変更系を許可する",                 HeadlessAllowsMutations);
        harness.Add("shutdown は対話エディタでは既定で拒否される",        ShutdownDeniedOnInteractive);
        harness.Add("shutdown は許可済み対話エディタでは通る",            ShutdownAllowedWhenEnabled);

        // ── 3. MCP の束縛 ──
        harness.Add("未束縛なら変更系ツールを拒否する",                   UnboundRejectsMutatingTools);
        harness.Add("未束縛でも観測系ツールは許可する",                   UnboundAllowsReadOnlyTools);
        harness.Add("束縛後はすべてのツールを許可する",                   BoundAllowsEverything);
        harness.Add("起動インスタンスの同一性は pid とトークンの両方で見る", InstanceIdentityNeedsBoth);

        // ── 4. 空きポート選択 ──
        harness.Add("空きポートは専用レンジから選ばれる",                 FreePortIsInDedicatedRange);
        harness.Add("使用中ポートは空きと判定しない",                     UsedPortIsNotFree);

        // ── 5. シーンロック ──
        harness.Add("死んだプロセスのロックは無効（上書きしてよい）",     DeadProcessLockIsStale);
        harness.Add("生きている別プロセスのロックは有効",                 LiveProcessLockIsHeld);
        harness.Add("自分自身のロックは無効扱い（張り直し）",             OwnLockIsStale);
        harness.Add("別マシンのロックは有効扱い（生死不明なので安全側）", RemoteMachineLockIsHeld);
        harness.Add("ロックの取得と解放が往復する",                       LockAcquireAndRelease);

        // ── 6. バックアップ ──
        harness.Add("バックアップは最新 N 世代だけ残る",                  BackupRotationKeepsNewest);
        harness.Add("バックアップ判定は別ファイルを巻き込まない",         BackupNameMatchingIsExact);
        harness.Add("上書き保存で旧版がバックアップされる",               WriteCreatesBackupOfPrevious);

        return harness.Run();
    }

    // ============================================================
    //  1. トークン検証
    // ============================================================

    private static void TokenNotConfiguredAllowsAnyone()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        Check.True(AiOperationPolicy.IsTokenValid(null), "トークン未設定ならヘッダー無しでも通るはず");
        Check.True(AiOperationPolicy.IsTokenValid("なんでも"), "トークン未設定なら任意の値でも通るはず");
    }

    private static void TokenMismatchIsRejected()
    {
        AiOperationPolicy.Configure(port: 7301, token: "abc123", isHeadless: true);
        Check.True(!AiOperationPolicy.IsTokenValid(null),     "ヘッダー無しは拒否されるはず");
        Check.True(!AiOperationPolicy.IsTokenValid(""),       "空トークンは拒否されるはず");
        Check.True(!AiOperationPolicy.IsTokenValid("abc124"), "1 文字違いは拒否されるはず");
        Check.True(!AiOperationPolicy.IsTokenValid("abc1234"), "長さ違いは拒否されるはず");
    }

    private static void TokenMatchIsAccepted()
    {
        var token = AiOperationPolicy.GenerateToken();
        Check.Equal(AiOperationPolicy.TOKEN_BYTES * 2, token.Length, "生成トークンの桁数");
        AiOperationPolicy.Configure(port: 7302, token: token, isHeadless: true);
        Check.True(AiOperationPolicy.IsTokenValid(token), "一致するトークンは通るはず");
    }

    // ============================================================
    //  2. 読み取り専用の既定
    // ============================================================

    private static void InteractiveDefaultsToReadOnly()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        Check.True(!AiOperationPolicy.MutationsEnabled, "対話エディタの既定は読み取り専用のはず");

        foreach (var cmd in new[] { "save_scene", "set_value", "add_actor", "remove_actor",
                                    "play_control", "send_ipc", "anim_preview", "write_asset_file" })
        {
            var denial = AiOperationPolicy.CheckAllowed(cmd, AiCommandOrigin.Remote);
            Check.True(denial is not null, $"{cmd} は既定で拒否されるはず");
        }
    }

    private static void ReadOnlyCommandsAlwaysAllowed()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        foreach (var cmd in new[] { "get_scene_info", "list_asset_files", "get_hierarchy",
                                    "get_editor_state", "get_log", "screenshot", "screenshot_gpu" })
        {
            Check.True(AiOperationPolicy.CheckAllowed(cmd, AiCommandOrigin.Remote) is null,
                       $"{cmd} は観測系なので常に許可されるはず");
        }
    }

    private static void UserInitiatedAlwaysAllowed()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        // パネル内蔵 AI（利用者自身の依頼）は読み取り専用ポリシーの対象外。
        Check.True(AiOperationPolicy.CheckAllowed("save_scene", AiCommandOrigin.UserInitiated) is null,
                   "利用者操作は許可されるはず");
        Check.True(AiOperationPolicy.CheckAllowed("shutdown", AiCommandOrigin.UserInitiated) is null,
                   "利用者操作の shutdown は許可されるはず");
    }

    private static void HeadlessAllowsMutations()
    {
        AiOperationPolicy.Configure(port: 7303, token: "t", isHeadless: true);
        Check.True(AiOperationPolicy.MutationsEnabled, "ヘッドレスは既定で変更を許可するはず");
        Check.True(AiOperationPolicy.CheckAllowed("save_scene", AiCommandOrigin.Remote) is null,
                   "ヘッドレスの save_scene は許可されるはず");
    }

    private static void ShutdownDeniedOnInteractive()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        var denial = AiOperationPolicy.CheckAllowed("shutdown", AiCommandOrigin.Remote);
        Check.Equal(AiOperationPolicy.DENY_SHUTDOWN, denial, "対話エディタの shutdown 拒否理由");
    }

    private static void ShutdownAllowedWhenEnabled()
    {
        AiOperationPolicy.Configure(port: null, token: null, isHeadless: false);
        AiOperationPolicy.MutationsEnabled = true;   // 利用者が環境設定で明示的に許可した状態
        Check.True(AiOperationPolicy.CheckAllowed("shutdown", AiCommandOrigin.Remote) is null,
                   "明示的に許可されていれば shutdown も通るはず");
        AiOperationPolicy.MutationsEnabled = false;  // 後続テストへ影響させない
    }

    // ============================================================
    //  3. MCP の束縛
    // ============================================================

    private static void UnboundRejectsMutatingTools()
    {
        SeedInstance.Clear();
        foreach (var tool in new[] { "seed_save_scene", "seed_batch", "seed_play",
                                     "seed_select", "seed_send_ipc", "seed_shutdown",
                                     "seed_anim_preview", "seed_anim_reload" })
        {
            var denial = SeedInstance.CheckToolAllowed(tool);
            Check.True(denial is not null, $"{tool} は未束縛では拒否されるはず");
            Check.True(denial!.Contains(SeedInstance.DENY_NOT_BOUND, StringComparison.Ordinal),
                       $"{tool} の拒否理由に定型文が含まれるはず");
        }
    }

    private static void UnboundAllowsReadOnlyTools()
    {
        SeedInstance.Clear();
        foreach (var tool in new[] { "seed_query", "seed_state", "seed_hierarchy",
                                     "seed_log", "seed_screenshot", "seed_launch",
                                     "seed_attach", "seed_instance" })
        {
            Check.True(SeedInstance.CheckToolAllowed(tool) is null,
                       $"{tool} は未束縛でも許可されるはず");
        }
    }

    private static void BoundAllowsEverything()
    {
        SeedInstance.Bind(7311, "tok", 1234, headless: true, attached: false);
        try
        {
            Check.True(SeedInstance.IsBound, "束縛済みのはず");
            Check.Equal("http://localhost:7311/seed-ai", SeedInstance.ApiBase, "束縛先の API ベース URL");
            Check.True(SeedInstance.CheckToolAllowed("seed_save_scene") is null,
                       "束縛後は変更系も許可されるはず");
        }
        finally
        {
            SeedInstance.Clear();
        }
    }

    private static void InstanceIdentityNeedsBoth()
    {
        const int pid = 4242;
        const string token = "deadbeef";
        var good = $"{{\"ok\":true,\"pid\":{pid},\"token\":\"{token}\"}}";
        Check.True(Launcher.IsExpectedInstance(good, pid, token), "pid・トークンとも一致すれば true");

        // 事故そのもの: 応答したのは別プロセス（利用者のエディタ）だった場合。
        var otherPid = $"{{\"ok\":true,\"pid\":{pid + 1},\"token\":\"{token}\"}}";
        Check.True(!Launcher.IsExpectedInstance(otherPid, pid, token), "pid 違いは false");

        var otherToken = $"{{\"ok\":true,\"pid\":{pid},\"token\":\"other\"}}";
        Check.True(!Launcher.IsExpectedInstance(otherToken, pid, token), "トークン違いは false");

        // トークンを持たない既定エディタ（従来の 7234 に居る相手）も拒否する。
        var noToken = $"{{\"ok\":true,\"pid\":{pid}}}";
        Check.True(!Launcher.IsExpectedInstance(noToken, pid, token), "トークン欠落は false");

        Check.True(!Launcher.IsExpectedInstance(null, pid, token), "応答なしは false");
        Check.True(!Launcher.IsExpectedInstance("これはJSONではない", pid, token), "壊れた応答は false");
    }

    // ============================================================
    //  4. 空きポート選択
    // ============================================================

    private static void FreePortIsInDedicatedRange()
    {
        var port = Launcher.PickFreePort();
        Check.True(port is not null, "空きポートが 1 つは見つかるはず");
        Check.True(port!.Value is >= 7300 and <= 7399, $"専用レンジ外のポートが選ばれた: {port}");
        // 既定ポート（利用者のエディタの指定席）は絶対に選ばない。
        Check.True(port.Value != SeedInstance.DEFAULT_PORT, "既定ポートを選んではいけない");
    }

    private static void UsedPortIsNotFree()
    {
        var port = Launcher.PickFreePort();
        Check.True(port is not null, "空きポートが必要");

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port!.Value);
        listener.Start();
        try
        {
            Check.True(!Launcher.IsPortFree(port.Value), "掴んでいるポートは空きと判定されないはず");
            // 別のポートは引き続き選べる（1 つ埋まってもレンジ全体は死なない）。
            var next = Launcher.PickFreePort();
            Check.True(next is not null && next.Value != port.Value, "別の空きポートが選ばれるはず");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ============================================================
    //  5. シーンロック
    // ============================================================

    /// <summary>テスト用の一時ディレクトリを作る。</summary>
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"seed_ai_safety_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeadProcessLockIsStale()
    {
        var info = new SceneLockInfo { Pid = 999_999, Machine = Environment.MachineName };
        // 「そのプロセスは生きていない」と答える判定関数を注入する。
        var stale = SceneLock.IsStale(info, Environment.ProcessId, Environment.MachineName, _ => false);
        Check.True(stale, "死んだプロセスのロックは無効のはず");
    }

    private static void LiveProcessLockIsHeld()
    {
        var info = new SceneLockInfo { Pid = 999_999, Machine = Environment.MachineName };
        var stale = SceneLock.IsStale(info, Environment.ProcessId, Environment.MachineName, _ => true);
        Check.True(!stale, "生きている別プロセスのロックは有効のはず");
    }

    private static void OwnLockIsStale()
    {
        var pid  = Environment.ProcessId;
        var info = new SceneLockInfo { Pid = pid, Machine = Environment.MachineName };
        Check.True(SceneLock.IsStale(info, pid, Environment.MachineName, _ => true),
                   "自分自身のロックは張り直してよいので無効扱いのはず");
    }

    private static void RemoteMachineLockIsHeld()
    {
        var info = new SceneLockInfo { Pid = 1, Machine = "OTHER-PC" };
        // 別マシンのプロセスは生死を確認できない。安全側（有効）に倒す。
        Check.True(!SceneLock.IsStale(info, Environment.ProcessId, Environment.MachineName, _ => false),
                   "別マシンのロックは有効扱いのはず");
    }

    private static void LockAcquireAndRelease()
    {
        var dir   = TempDir("lock");
        var scene = Path.Combine(dir, "MainGame.scene");
        File.WriteAllText(scene, "{}");
        try
        {
            Check.True(SceneLock.TryAcquire(scene, headless: false, out _), "初回の取得は成功するはず");
            Check.True(File.Exists(SceneLock.LockPathFor(scene)), "ロックファイルが作られるはず");

            var read = SceneLock.Read(scene);
            Check.True(read is not null, "ロックを読み戻せるはず");
            Check.Equal(Environment.ProcessId, read!.Pid, "ロックの PID");

            SceneLock.Release(scene);
            Check.True(!File.Exists(SceneLock.LockPathFor(scene)), "解放でロックファイルが消えるはず");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ============================================================
    //  6. バックアップ
    // ============================================================

    private static void BackupRotationKeepsNewest()
    {
        var dir = TempDir("rotate");
        try
        {
            // 15 世代 + 無関係ファイルを置く
            for (int i = 0; i < 15; i++)
                File.WriteAllText(Path.Combine(dir, $"MainGame.20260901-0000{i:D2}.scene"), "x");
            File.WriteAllText(Path.Combine(dir, "Other.20260901-000000.scene"), "x");
            File.WriteAllText(Path.Combine(dir, "MainGame.scene"), "x");

            var removed = SafeFileWriter.RotateBackups(dir, "MainGame", ".scene", SafeFileWriter.BackupKeep);
            Check.Equal(5, removed, "削除された世代数");

            var left = new List<string>();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileName(f);
                if (SafeFileWriter.IsBackupName(n, "MainGame", ".scene")) left.Add(n);
            }
            left.Sort(StringComparer.Ordinal);
            Check.Equal(SafeFileWriter.BackupKeep, left.Count, "残った世代数");
            Check.Equal("MainGame.20260901-000005.scene", left[0], "残ったうち最古の世代");

            Check.True(File.Exists(Path.Combine(dir, "Other.20260901-000000.scene")),
                       "別ファイルの世代は消してはいけない");
            Check.True(File.Exists(Path.Combine(dir, "MainGame.scene")),
                       "本体ファイルは消してはいけない");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void BackupNameMatchingIsExact()
    {
        Check.True(SafeFileWriter.IsBackupName("MainGame.20260907-101112.scene", "MainGame", ".scene"),
                   "通常のバックアップ名");
        Check.True(SafeFileWriter.IsBackupName("MainGame.20260907-101112_3.scene", "MainGame", ".scene"),
                   "同一秒の重複回避付き");
        Check.True(!SafeFileWriter.IsBackupName("MainGameOld.20260907-101112.scene", "MainGame", ".scene"),
                   "名前が前方一致するだけの別ファイルは対象外");
        Check.True(!SafeFileWriter.IsBackupName("MainGame.scene", "MainGame", ".scene"),
                   "本体ファイルはバックアップではない");
        Check.True(!SafeFileWriter.IsBackupName("MainGame.20260907-101112.actor", "MainGame", ".scene"),
                   "拡張子違いは対象外");
    }

    private static void WriteCreatesBackupOfPrevious()
    {
        var root = TempDir("write");
        try
        {
            var file = Path.Combine(root, "sub", "S.scene");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // 新規作成ではバックアップを作らない
            var firstBackup = SafeFileWriter.WriteAllTextAtomic(file, "one", root);
            Check.True(firstBackup is null, "新規作成でバックアップは作られないはず");
            Check.Equal("one", File.ReadAllText(file), "新規作成の内容");

            // 上書き時は旧版が .backup へ退避される
            var backup = SafeFileWriter.WriteAllTextAtomic(file, "two", root);
            Check.True(backup is not null, "上書き時はバックアップが作られるはず");
            Check.Equal("one", File.ReadAllText(backup!), "バックアップの中身は上書き前の内容");
            Check.Equal("two", File.ReadAllText(file), "本体は新しい内容");

            // バックアップはアセットルート直下の .backup/<相対ディレクトリ> に置かれる
            var expectedDir = Path.Combine(root, SafeFileWriter.BackupDirName, "sub");
            Check.Equal(Path.GetFullPath(expectedDir), Path.GetFullPath(Path.GetDirectoryName(backup!)!),
                        "バックアップ置き場");

            // 一時ファイルが残っていないこと
            Check.True(!File.Exists(file + SafeFileWriter.TempSuffix), "一時ファイルは残らないはず");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
