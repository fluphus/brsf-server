# BRSF Server

BRS FRAGMENT 的本地服务端、WebUI 管理界面和 Android 宿主。PC 与 Android 共用服务端逻辑和同一份管理界面，支持中文、日语和英语，默认跟随系统语言。

**构建源码 → 导入一个配套资源包 → 启动服务。** 不需要额外的私有输入包或开发者原工作目录。游戏客户端单独安装。

Android 玩家可以直接安装[已签名的 2.5.0 ARM64 游戏客户端](https://github.com/fluphus/brsf-server/releases/download/v0.1.14/BRSFragment-2.5.0-local-arm64.apk)。如需自行构建，请准备原版 2.5.0 XAPK，按[客户端构建说明](client/README.md)生成并签名 APK。

配套资源见 [v0.1.14 发行页](https://github.com/fluphus/brsf-server/releases/tag/v0.1.14)。下载全部 ZIP 分卷并保存在同一个文件夹，用支持分卷 ZIP 的解压工具打开 `.zip` 文件，解压得到 `brs-resources.tar`。详见[资源下载与导入说明](docs/resource-import.md)。

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
