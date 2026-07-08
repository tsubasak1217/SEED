---
name: build-and-verify-seed
description: SEEDのコード変更後にRust runtime / C# editor / C# scripting の3系統を正しい順序でビルドし、動作確認までを一貫して行う。「ビルドして」「動作確認して」「ビルド確認して」と言われたとき、またはコード変更を master へ ff-merge する前の確認として使用。
---

# SEED ビルド＆動作確認

SEED は 3 系統のハイブリッド構成。ビルド確認の粒度がブレると、確認後の
master への ff-merge 運用で壊れたものが取り込まれる。この手順で判定を定型化する。

## 3 系統と依存関係

- **scripting/**（C#, `SEEDScripting.csproj`, net9.0）: スクリプト API 基底クラス＋CLR ブリッジ。ビルド成果物 `SEEDScripting.dll` を runtime が実行時ロードする。
- **editor/**（C# WPF, `SEEDEditor.sln`）: エディタ本体。runtime を子プロセスとして起動する。
- **runtime/**（Rust, cargo workspace メンバー）: エンジン本体 `SEED.exe`。起動時に `SEEDScripting.dll` を CLR にロードする。

依存の要点（`runtime/src/engine/core/scripting/mod.rs` の `resolve_dll_path()`）:
runtime はワーキングディレクトリ基準で `../scripting/bin/Debug/net9.0/SEEDScripting.dll`
を探してロードする。**runtime は scripting の DLL 成果物にランタイム依存**する（コンパイル依存ではない）。
このため scripting を変更したら runtime を再ビルドしなくても、DLL さえ更新すれば反映される。

## ビルド順序

CI（`.github/workflows/ci.yml`）は dotnet（editor.sln）→ cargo（runtime）の順。
ローカルでは scripting も含め、以下の順で行う。

```bash
# 1. スクリプト API（runtime がロードする DLL を先に更新）
dotnet build scripting/SEEDScripting.csproj

# 2. エディタ
dotnet build editor/SEEDEditor.sln

# 3. Rust ランタイム（必ず runtime/ 配下で実行）
cd runtime
cargo build
```

> **cargo は必ず `runtime/` で実行する。** ルートの `Cargo.toml` は
> runtime / plugin_api / plugins/* を含む workspace 定義。ルートで
> `cargo build` するとプラグインまで巻き込んでビルドする。runtime だけを
> 対象にしたいときは `runtime/` へ入ってからビルドする（CI も
> `working-directory: runtime`）。

## 変更範囲によるビルドの切り分け

無駄な再ビルドを避けるため、触った系統だけを対象にする。

| 変更した場所 | 必要なビルド |
|---|---|
| `runtime/**` の Rust のみ | `cd runtime && cargo build` のみ |
| `scripting/**`（`Api/` の C# ラッパー等） | **`SEEDScripting.csproj` の再ビルド必須** |
| `runtime/.../scripting/host_api.rs`（レジストリ／FFI） | runtime を `cargo build`。C# 側のシグネチャ・レイアウトも変えたなら scripting も再ビルド |
| `editor/**` | `SEEDEditor.sln` のビルド |

**古い DLL が読まれる罠:** scripting のソースを直しても
`SEEDScripting.csproj` を再ビルドしなければ `bin/Debug/net9.0/SEEDScripting.dll`
は古いまま。runtime はそれをロードするので、変更が反映されず「直したのに動かない」
という症状になる。scripting を触ったら DLL 再ビルドを最優先で確認する。

## シャドウコピー（実行中でも再ビルド可）

`mod.rs` の `ScriptingHost::load()` / `shadow_copy()` は、DLL 一式を
プロセス ID 単位のテンポラリ（`%TEMP%/SEED_scripting_shadow/<pid>/`）へ
コピーしてからロードする。このためビルド出力側の DLL はロックされず、
**runtime を起動したまま `dotnet build scripting/...` で再ビルドできる**。
反映するには次回起動、またはエディタ上でのスクリプト保存によるホットリロードが必要。

## 動作確認手順

1. エディタを起動（`dotnet build` 済みの `SEEDEditor.exe`、または Visual Studio から実行）。
2. シーンを開き **Play** を押す（runtime が子プロセスとして起動）。
3. **Output パネル**でエラーを確認する。

**スクリプトコンパイル失敗のサイン:** Output に
`SCRIPTS_RELOADED:-1`（正確には `count` が負値）が出ていたら失敗。
runtime 側 `runtime/src/engine/core/app_base/app/script_ops.rs` の
`handle_reload_scripts()` が `SCRIPTS_RELOADED:{count},{restored}` を送信し、
`count` が負のとき失敗を意味する（`SCRIPTS_RELOADED:-1,CLR not loaded` /
`-1,assets root unknown` など）。エディタ側
`editor/src/Runtime/RuntimeManager.cs` の受信処理は、この負値を検出すると
Output に「[スクリプトエラー] スクリプトのリロードに失敗しました」の区切り枠を
表示する。**失敗時はスクリプトが Placeholder のまま実行されない（サイレント故障）**
ため、Play しても無反応で気づきにくい。詳細な原因は同パネルの `[ScriptCompileError]` 行に出る。

## 「ビルド確認完了」チェックリスト

以下をすべて満たしたら「ビルド確認完了」とし、ff-merge 判断に進んでよい。

- [ ] **触った系統がビルド成功している**（エラーなしで完了）。
  - runtime を触った → `runtime/` で `cargo build` 成功。
  - scripting を触った → `SEEDScripting.csproj` 再ビルド成功（DLL 更新を確認）。
  - editor を触った → `SEEDEditor.sln` ビルド成功。
- [ ] **scripting / host_api.rs のスクリプト API を変更した場合** → DLL を再ビルドし、
      さらに手動 Play で `SCRIPTS_RELOADED:-1` が出ないことを確認。
- [ ] **起動・実行系（runtime の初期化、シーン読込、Play 経路、スクリプト実行）に触れた場合**
      → 手動でエディタ起動 → Play → Output にエラー・パニックが出ないことを確認。
- [ ] 純粋な内部リファクタや UI 微調整でも、最低限「触った系統がビルド成功」までは必須。
- [ ] `docs/scripting_api.md` の更新（スクリプト API を追加・変更した場合。CLAUDE.md の運用ルール）。

判定に迷ったら「起動系に触れたか？」で分岐する。触れていればビルド成功だけでは
不十分で、手動 Play まで行う。

## よくある失敗

- **DLL 再ビルド漏れ**: scripting のソースだけ直して `SEEDScripting.csproj`
  をビルドせず、runtime が古い DLL をロード。変更が反映されない最頻の罠。
- **editor.sln のビルド漏れ**: runtime だけ直して editor を再ビルドせず、
  IPC メッセージ形式などエディタ↔ランタイム間の変更が片側だけ反映される。
- **cargo をルートで実行**: workspace ルートで `cargo build` して plugins まで
  巻き込む／意図した対象がビルドされたか曖昧になる。runtime 対象なら `runtime/` へ入る。
- **Play せずビルド成功だけで完了扱い**: スクリプトコンパイル失敗は
  ビルドでは出ず、実行時の `SCRIPTS_RELOADED:-1`（サイレント故障）で初めて分かる。
  スクリプト・起動系を触ったら必ず Play で確認する。
