# dotnet_host スパイク（Android 上で hostfxr 経由の .NET を起動する検証）

段階B（C# スクリプトを実機で動かす）の前提を確かめるための使い捨て検証コード。
結論と数値は `docs/android.md` §11 にまとめてある。ここは「もう一度同じ検証を回す」ためのもの。

## 何を確かめるか

- Rust から `netcorehost`（`default-features = false`、`Hostfxr::load_from_path`）で `libhostfxr.so` を読み、
  `initialize_for_runtime_config` → `load_assembly_from_bytes` → `get_delegate_loader` で
  `UnmanagedCallersOnly` の C# 関数を呼べるか。
- .NET ランタイムパックの種類（linux-bionic の Mono / .NET 10 android の CoreCLR）ごとの起動時間・機能差。
- リフレクション・ジェネリック生成・collectible AssemblyLoadContext・JIT・暗号 API などが端末で動くか。

## 構成

| パス | 役割 |
|---|---|
| `cs/SpikeLib/` | 検証用 C# ライブラリ（`Exports.cs` の `UnmanagedCallersOnly` 入口、`Checks.cs` の機能チェック一式） |
| `cs/SpikePlugin/` | collectible ALC で動的ロードする別 DLL |
| `cs/SpikeLibApp/` | trim（`PublishTrimmed`）の影響を測るための Exe 版 |
| `rs/spike_host/` | Rust ホスト（`options.rs` で経路・レイアウトを切り替える。ルートのワークスペースには属さない） |
| `tools/stage.py` | 端末へ push するディレクトリ一式を ABI ごとに組み立てる（dotnet-root 形式 / 自己完結の平坦形式） |
| `tools/run_on_device.sh` | 端末上で 1 シナリオ実行し、出力と logcat を保存する |
| `tools/summarize_timing.py` | ログから所要時間を抜き出して中央値・最小・最大を表にする |

## 手順の概略

1. `cs/` を `dotnet publish -r linux-bionic-<x64|arm64> --self-contained -p:UseAppHost=false` で発行する
   （ランタイムパックは NuGet から自動取得される。CoreCLR を試すときは
   `Microsoft.NETCore.App.Runtime.android-<x64|arm64>` を別途取得し、同じ版の bionic パックから
   `libhostfxr.so` / `libhostpolicy.so` を組み合わせる。詳細は `tools/stage.py` 冒頭のコメント）。
2. `rs/spike_host` を `cargo ndk -t <abi> build --release` でビルドする。
3. `tools/stage.py` でレイアウトを組み、`tools/run_on_device.sh <serial> <abi> <scenario> -- <引数>` で実行する。
   各スクリプトの使い方は冒頭のコメントに書いてある。

## 注意

- 検証は adb のシェル権限（`/data/local/tmp`）で行う。アプリのプロセス内（SELinux のアプリドメイン、
  private dir からの dlopen、ART のシグナルチェーン）は別途の検証が要る。
- 暗号 API（SHA256 / RandomNumberGenerator）はどちらのランタイムでもプロセスごと落ちる。呼ばないこと。
