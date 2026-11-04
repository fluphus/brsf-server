# BRSF Server

[简体中文](README.md) | [日本語](README.ja.md) | [English](README.en.md)

BRS FRAGMENT 本地运行版，适用于 Android 8.0 及以上的 ARM64 设备。

## 下载

请下载下面的 **两个 APK 和全部四个资源分卷**。

| 用途 | 文件 |
| --- | --- |
| 游戏客户端 | [BRSFragment-2.5.0-local-arm64.apk](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/BRSFragment-2.5.0-local-arm64.apk) |
| BRS 本机服务 | [BRSFragment-LocalHost-0.1.14.apk](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/BRSFragment-LocalHost-0.1.14.apk) |
| 资源分卷 1 | [brs-resources.z01](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z01) |
| 资源分卷 2 | [brs-resources.z02](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z02) |
| 资源分卷 3 | [brs-resources.z03](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.z03) |
| 资源分卷 4（解压入口） | [brs-resources.zip](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/brs-resources.zip) |

## 安装、解压与启动

1. 下载表中的六个文件。将四个资源分卷放在同一文件夹，保留原文件名。
2. 安装两个 APK，按 Android 的系统提示完成安装。
3. 用支持分卷 ZIP 的解压工具打开 **`brs-resources.zip`**。工具会自动读取 `.z01`、`.z02`、`.z03`，解出 **`brs-resources.tar`**。
4. 打开 **BRS 本机服务**，在服务停止时点击 **“导入资源包”**，选择解出的 `brs-resources.tar`，等待导入完成。
5. 点击 **“启动”**，等待服务就绪，再打开 **BRS FRAGMENT** 开始游戏。

资源只需导入一次。之后每次游玩，先启动 BRS 本机服务，再打开游戏。

解压和导入期间建议准备约 25 GB 可用空间。导入成功后，可以删除下载分卷及解出的 TAR，宿主会保留已导入资源。

更新时先正常结束战斗、退出游戏并停止本机服务，再覆盖安装新版 APK。资源细节见[资源导入说明](docs/resource-import.md)。

## 仓库内容

```text
server/    共享服务端、桌面入口和默认玩法配置
webui/     管理界面、样式和翻译
android/   Android 前台服务、资源导入和 WebView 封装
client/    从原版 XAPK 构建本地游戏客户端的工具和补丁
tests/     核心逻辑与宿主离线测试
tools/     构建、资源打包和源码检查工具
docs/      构建与资源导入说明
```

默认规则、启动设置、管理名称和翻译随源码提供。原版图片、游戏资源、资源目录、配套模板及固定协议材料放在独立的 `brs-resources.tar` 中，导入时读取。仓库不包含玩家存档、管理员凭据、签名私钥或原版游戏 APK。

## PC 使用

需要 .NET SDK 10.0.402 或同一补丁系列的后续版本。先编译，再导入配套资源包；`local-data` 是自行选择的账号与资源保存目录。

```sh
dotnet build server/Host/Brs.Local.Host.csproj -c Release
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data --import-content ./brs-resources.tar
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data
```

管理页地址为 `http://127.0.0.1:18180/`。首次启动生成的管理员凭据保存在所选数据目录的 `owner.key` 中，供本机登录使用。服务默认只监听本机。关闭时按 Ctrl+C。

资源包只需导入一次；之后直接启动即可。更多参数见[服务端说明](server/BUILDING.md)。

## Android 使用

配置 .NET Android 工作负载、Android SDK 36 和 JDK 21，然后构建：

```powershell
./tools/build-android.ps1
```

安装生成的宿主 APK，打开宿主，点击“导入资源包”并选择 `brs-resources.tar`。导入完成后点击“启动”，再打开配套游戏客户端。无需在构建时提供资源、协议材料或历史签名。

详见 [Android 构建说明](docs/build-android.md)和[资源导入说明](docs/resource-import.md)。覆盖升级需要使用相同的应用签名；更新前正常结束游戏并停止宿主。不要通过卸载或清除数据来更新。

## 开发与测试

WebUI 随服务端编译嵌入，修改后重新构建宿主。WebUI 测试需要 Node.js 24 或更新版本及 npm；源码检查和资源打包工具使用 Python 3。测试使用合成数据和临时目录，不需要玩家存档或连接设备。

```sh
dotnet run --project tests/server -c Release
dotnet run --project tests/AndroidHost.Language -c Release
dotnet run --project tests/AndroidHost.Lifecycle -c Release
dotnet run --project tests/AndroidHost.Packaging -c Release
dotnet run --project tests/AndroidHost.Storage -c Release
npm ci --prefix tests/webui
npm test --prefix tests/webui
python -B tests/tools/test_source_tree.py
python tools/check-source-tree.py
```
