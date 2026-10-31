# Android 构建

宿主版本为 0.1.14（versionCode 15），包名 `org.brsfragment.localhost`，最低 Android 7.0 / API 24。APK 包含 arm64 和 x64 宿主、完整本地后端与管理 WebUI，提供中文、日文和英文。它不包含游戏客户端。

使用仓库 `global.json` 指定的 .NET 10 SDK、Android workload、JDK 21 和 Android SDK 36。在配置好 SDK 的开发环境中运行：

```powershell
dotnet workload install android
./tools/build-android.ps1
```

默认输出在 `artifacts/android`。可以用 `-ArtifactsPath` 更改输出目录，用 `-DotnetCommand` 指定 dotnet。SDK/JDK 可通过开发环境配置，也可传 `-AndroidSdkDirectory` 和 `-JavaSdkDirectory`；直接运行 `dotnet build android/Brs.AndroidHost.csproj -c Release -t:SignAndroidPackage` 时使用同名 MSBuild 属性。

源码 checkout 可直接构建，无需游戏资源、协议密钥、私有运行输入目录或旧签名库。玩法规则、启动配置和管理界面随源码构建；游戏资源与协议材料在安装后从一个 `brs-resources.tar` 资源包导入，不会编入 APK。资源包的制作与格式见[资源导入说明](resource-import.md)。

首次安装时，点击“导入资源包”，在系统文件选择器中选取完整资源包。导入过程流式处理约 8 GB 的数据，校验通过后才发布；随后点击“启动”。未导入资源时，启动状态显示导入指引，游戏 API、管理 API 和后端均不会报告已就绪。取消或失败的导入不会替换已有资源或账号。

升级安装保留应用内部的账号数据库、存储身份标记和已导入游戏归档。旧版本若尚未存储新格式的运行材料，再导入同一个完整资源包即可补齐；已有游戏归档和导入回执保留。已有协议密钥与导入材料不一致时，启动会拒绝继续，避免把账号存储绑定到不同协议数据。覆盖升级仍要求相同应用签名；卸载会删除 Android 应用内部数据。

运行时显式启动前台服务，API 和管理监听仅限本机回环 `18445` / `18180`；通知和控制页均提供停止入口。管理页导出和备份使用系统保存窗口。导入资源前须停止服务。该宿主没有研究 TLS 证书、研究验收入口或游戏启动按钮；游戏客户端的接入与实际可玩性需另行验证。

## 签名与检查

普通开发构建由 Android 工具链创建或使用开发者自己的 debug 签名，即使配置选择 `Release` 也是如此。仓库不附带签名库，也不要求历史签名。

若需分发签名，通过 `-ReleaseSigningPropertiesFile /path/to/signing.props` 导入仓库外的 MSBuild 属性文件，在其中设置 `AndroidKeyStore`、`AndroidSigningKeyStore`、`AndroidSigningKeyAlias`、`AndroidSigningKeyPass` 和 `AndroidSigningStorePass`。签名凭据应由本地秘密配置提供。不要将该文件或 keystore 提交到仓库；不同签名的 APK 不能覆盖安装已有应用。

纯托管检查无需 Android 设备：

```powershell
dotnet run --project tests/AndroidHost.Language -c Release
dotnet run --project tests/AndroidHost.Lifecycle -c Release
dotnet run --project tests/AndroidHost.Packaging -c Release
dotnet run --project tests/AndroidHost.Storage -c Release
```

这些检查覆盖语言与管理页导航、凭证及页所有权、导入互斥与导出流边界，以及合成资源材料的首次设置、密钥身份校验和升级失败保全。APK 构建或这些检查通过不能代替 Android WebView、SAF、前台服务生命周期及游戏客户端的设备验收。


