# Housing To Brio

[English](./README.md)

ハウジングのレイアウトファイルを読み込んで、家具を Brio のプロップとして配置する GPose 用プラグインです。
自分の家がなくても、家具を買わなくても、自分で配置しなくても、好きなハウジングのレイアウトの中で撮影できます。
撮影できる場所とパターンを増やすためのツールです。

## インストール

Dalamud の「設定 > 試験的 > カスタムプラグインリポジトリ」に次のURLを追加し、プラグイン一覧から HousingToBrio をインストールしてください。

```
https://raw.githubusercontent.com/JTayGang/MeowyUtils/main/repo.json
```

## 使い方

1. `/housingtobrio` または `/h2b` でウィンドウを開きます。
2. レイアウトの `.json` ファイルを読み込みます。
3. 室内の家具・屋外の庭具・染色を含めるかどうかを選びます。
4. Brio のフォルダを確認し、プロジェクト名を付けて **Brioプロジェクトとして保存** を押します。
5. Brio を再読み込みします（**保存後にBrioを自動で再読み込みする** にチェックを入れると自動で行われます）。
   先にBrio側で New / Save / Delete Project を使うと、書き出した項目が消えてしまうので使わないでください。
6. Brio の **Load Project** ウィンドウで歯車アイコンを開き、**Relative Object Positions** のチェックを外してから読み込みます。

現在のゲームに存在しない家具モデルなど、対応できなかったアイテムはスキップされ、保存後に一覧で表示されます。

## 言語

ウィンドウの言語は Dalamud の表示言語（英語／日本語）に合わせて切り替わります。
変更したいときは、ウィンドウ下部の **Language / 言語** から選んでください。

## 制限事項

- 建材（壁・床・屋根・扉など）は変換されません。家具・庭具のみです。
- 素材違いのアイテムは、元のアイテムの見た目になります。
- Brio のプロジェクトファイル形式が変わると動かなくなる可能性があります。

## ライセンスについて

Brio (<https://github.com/Etheirys/Brio>) は GPL-3.0 です。
このプラグインは Brio のコードを参照・同梱しておらず、互換性のためにファイル形式だけを合わせています。
ただし公開・再配布する場合は、Brio のライセンスもご自身で確認してください。

## クレジット

次のソースコードを（再利用ではなく）読んで、ファイル形式や仕様を確認しています。

- [Brio](https://github.com/Etheirys/Brio) (GPL-3.0)
- [ReMakePlace plugin](https://github.com/RemakePlace/plugin)
- [Dalamud](https://github.com/goatcorp/Dalamud)

## ソースからビルドする

- Dalamud プラグインの開発環境（Visual Studio または `dotnet build`、XIVLauncher）
- Brio がインストールされていること
- `MessagePack` 3.1.7 を復元できる NuGet 環境

```
dotnet build HousingToBrio.csproj -c Release
```

ビルドしたら、いつもの方法で開発用プラグインとして読み込んでください。
