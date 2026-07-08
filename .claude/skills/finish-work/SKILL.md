---
name: finish-work
description: 一連の実装タスクを仕上げて締めるときに使用。「作業が完了したとき」「『マージして』『仕上げて』と言われたとき」「一連の実装タスクの最後」が発動条件。ビルド確認 → コミット → master への ff-merge → push までを定型手順で通す。
---

# 作業の締め（ビルド確認 → コミット → ff-merge → push）

実装が一段落したら、この手順で締める。**ビルド確認が完了条件**であり、確認が済めば
ユーザーの標準運用として確認を取らずに master へ ff-merge してよい（git の履歴で戻せるため）。
コンフリクトや ff 不可のときは無理に解決せず**停止して報告**する。

## 手順1: ビルド確認（完了条件）

**build-and-verify-seed Skill** に従い、**触った系統だけ**をビルド確認する。
- runtime を触った → `runtime/` で `cargo build` 成功。
- scripting を触った → `dotnet build scripting/SEEDScripting.csproj` 成功（DLL 更新を確認）。
- editor を触った → `dotnet build editor/SEEDEditor.sln` 成功。
- スクリプト API・起動/実行系に触れた場合は、手動 Play で `SCRIPTS_RELOADED:-1`（サイレント故障）が
  出ないことまで確認する。ここが「ビルド確認完了」の判定。詳細は build-and-verify-seed Skill を参照。

ビルドが通らなければここで停止し、原因を報告する（未完成のまま次へ進まない）。

## 手順2: コミット

未コミットの変更をコミットする。**コミットメッセージは git log の既存規約に合わせる**。
`git log --oneline -10` で実際の形式を確認すること。規約は **日本語 + prefix**:
- `feat:` 新機能 / `fix:` 不具合修正 / `docs:` ドキュメント・Skill 等。
- 例: `feat: オブジェクト・コンポーネントのアクティブ切替チェックボックス（Unity風）`
- 例: `fix: カメラ移動キーのスタックによるRMB移動・軸スナップ不能を修正`

```bash
git add -A
git commit -m "feat: ○○"   # メッセージ末尾に Co-Authored-By 行を付ける
```

## 手順3: master への ff-merge

ビルド確認済みなら、確認を取らずに master へ ff-merge してよい。
worktree 上で作業した場合、メイン checkout が master をチェックアウトしているので、
**メインリポジトリルートで `--ff-only` merge** する（メインルートは `git worktree list` で確認）。

```bash
# <ブランチ> は現在の作業ブランチ名（git rev-parse --abbrev-ref HEAD）
# <メインルート> は git worktree list の master 行のパス
git -C <メインルート> merge --ff-only <ブランチ>
```

- ff 可能（master が作業ブランチの祖先）ならそのまま取り込まれる。
- **ff 不可（master が進んでいる）なら rebase せず、状況を報告して停止**する。勝手に解決しない。

## 手順4: origin への push

master を origin へ push する。

```bash
git -C <メインルート> push origin master
```

- master への直接 push が拒否される環境（保護ブランチ等）なら、**作業ブランチの push に切り替えて**
  `git push origin <ブランチ>` を実行し、その旨を報告する（PR 化はユーザー判断）。

## 注意

- コンフリクト・ff 不可・push 拒否のいずれも、無理に突破せず**停止して状況を報告**する。
- worktree はマージ後もそのまま残してよい（クリーンアップはユーザー判断）。
