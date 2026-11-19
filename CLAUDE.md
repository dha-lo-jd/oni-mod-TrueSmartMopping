# CLAUDE.md

ONI の Mod「"TRUE" Smart Mopping」。全 Mod 共通の規約は HQ（`../CLAUDE.md`、`../CONVENTIONS.md`）。ここにはこの Mod 固有のことだけを書く。

## 方針
- バニラの拭き取りコマンドより**速くしない**。複製人間 1 人あたりの拭き取り速度はバニラのまま（`MopAmountPerTick` はバニラの `Moppable` と同じ）。省くのは手間（範囲指定と並べ方）だけ。2 人以上のときの並びは、バニラのコマンドでいちばん速い進め方（1 マスあけて端から交代）を再現するもの。

## 構成（`TrueSmartMopping/`）
- `SmartMopChore.cs`: 拭き取り専用の Chore（`SmartMopChore`）。厚い所から先に取り、残りを端から乾かす。助っ人の割り当て（リレー）もここ。
- `SmartMopTargets.cs`: 次に拭くマスの選択（純粋なロジック）。
- `SmartMopTool.cs`: 「スマート拭き取り」ツール（クリックで水たまりに作業を置く）。
- `AutoMopMonitor.cs`: 複製人間ごとに付く監視。液体に触れたら作業を自動で作る。立てる場所が見つからなかったマスはしばらく再探索しない（探索が最大 4000 マスに及ぶため）。
- `AutoMopOverlay*.cs`、`SmartMopMarker.cs`、`SmartMopSaveData.cs`、`ModSettings.cs`、`MinionIdentityPatch.cs`、`STRINGS.cs` と `translations/ja.po`。
- `HarnessTests.cs`（`#if DEBUG`）: 実機テスト。

## 実機検証
- 実行: `python ../GravitasTestHarness/Gravitas.TestProbe/probe.py run --project . --summary --tests '{"tags":["tsmbench"]}'`（ゲームを起動し本番の `mods.json` を書き換えるので、依頼の範囲か確かめる）。
- `tsmbench` は 1.1.0 時点で U60 全件合格。`manual` タグのテスト（`tsmvanilla`、`tsmgas`、`tsmcmp`、`tsmrelay`）はバニラとの比較測定で時間がかかるので、タグを指定したときだけ走らせる。`--tests` の `exclude_tags` は効かず、`tsmbench` を指定すると `manual` 付きの `tsmbench.*` も走る。
- 測定用の複製人間は感情表現や足の濡れに反応しないようにしてある（測定が乱れるため）。

## 版
- 版は `TrueSmartMopping/mod_info.yaml`（と `mod.yaml`）。1.1.0 から U60（ビルド 763796）以降が必要。Workshop 説明文と変更履歴は `Workshop/`（英日）。
