# BRSF Server

[简体中文](README.md) | [日本語](README.ja.md) | [English](README.en.md)

BRS FRAGMENT をローカルで動作させるためのプロジェクトです。Android 8.0 以降の ARM64 端末に対応します。

## ダウンロード

**APK 2 個とリソース分割ファイル 4 個すべて**をダウンロードしてください。

| 用途 | ファイル |
| --- | --- |
| ゲームクライアント | [BRSFragment-2.5.0-local-arm64.apk](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/BRSFragment-2.5.0-local-arm64.apk) |
| BRS ローカルサービス | [BRSFragment-LocalHost-0.1.14.apk](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/BRSFragment-LocalHost-0.1.14.apk) |
| リソース分割ファイル 1 | [brs-resources.z01](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z01) |
| リソース分割ファイル 2 | [brs-resources.z02](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z02) |
| リソース分割ファイル 3 | [brs-resources.z03](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z03) |
| リソース分割ファイル 4（ここから解凍） | [brs-resources.zip](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.zip) |

## インストール・解凍・起動

1. 表にある 6 個のファイルをダウンロードします。リソース分割ファイルは名前を変えず、すべて同じフォルダーに保存してください。
2. Android の画面の案内に従って、2 個の APK をインストールします。
3. 分割 ZIP 対応の解凍ツールで **`brs-resources.zip`** を開きます。`.z01`、`.z02`、`.z03` が自動で読み込まれ、**`brs-resources.tar`** が生成されます。
4. **BRS ローカルサービス**を開きます。サービスを停止した状態で **「リソースを取り込む」**を押し、生成された `brs-resources.tar` を選択して、取り込み完了まで待ちます。
5. **「起動」**を押してサービスの準備完了を待ち、**BRS FRAGMENT** を開いてプレイします。

リソースの取り込みは初回のみ必要です。次回からは、ローカルホストのサービスを起動してからゲームを開いてください。

解凍と取り込みのため、約 25 GB の空き容量を用意してください。取り込み完了後は、ダウンロードした分割ファイルと TAR を削除できます。取り込んだリソースはホスト内に保存されます。

更新時は戦闘を正常に終了し、ゲームとホストのサービスを終了してから、新しい APK を上書きインストールしてください。

## 開発・ビルド

リポジトリには共通サーバー、WebUI、Android ホスト、クライアントパッチ、ビルドツールが含まれます。ホストと WebUI は中国語・日本語・英語に対応し、初期状態では端末の言語に従います。

- [原版 2.5.0 XAPK からゲームクライアントをビルド](client/README.md)
- [Android ホストのビルド](docs/build-android.md)
- [PC サーバーのビルドと設定](server/BUILDING.md)
- [リソースパッケージの詳細](docs/resource-import.md)

PC サーバーには .NET SDK 10.0.402、または同じ系列の新しいパッチ版を使用します。データの保存先を指定し、リソース TAR をインポートしてから起動してください。

```sh
dotnet build server/Host/Brs.Local.Host.csproj -c Release
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data --import-content ./brs-resources.tar
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data
```

PC の管理画面は `http://127.0.0.1:18180/` です。管理者の認証情報は指定したデータフォルダー内の `owner.key` に生成されます。サーバーの終了には Ctrl+C を押します。
