# SEED

[![CI](https://github.com/tsubasak1217/SEED/actions/workflows/ci.yml/badge.svg)](https://github.com/tsubasak1217/SEED/actions/workflows/ci.yml)

Rust製ゲームエンジンRuntimeと、.NET 9.0 (WPF) 製エディタのハイブリッドプロジェクト。

## プロジェクト構成

- **runtime/**: Rustによるコアエンジン部分。
- **editor/**: C# (WPF) によるエディタ部分。
- **scripting/**: C# によるスクリプトAPI定義。

## ビルド・実行方法

### Rust Runtime
```bash
cd runtime
cargo build
```

### Editor
Visual Studio 2022 または `dotnet build` を使用してください。
```bash
dotnet build editor/SEEDEditor.sln
```
