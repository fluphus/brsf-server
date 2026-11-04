# 游戏客户端构建

用自行取得的 **BRS FRAGMENT 2.5.0 XAPK（版本号 203，arm64-v8a）** 和自己的签名密钥，重建配套的本地游戏客户端。输入必须匹配 [manifest.json](manifest.json) 中固定的原版文件校验值；其他版本不会自动套用补丁。

本目录公开重建脚本、差分数据与校验信息。客户端构建只需原版 XAPK 和自己的签名密钥，其他载荷从 XAPK 与随附差分重建。可直接使用 [v0.1.14 发行页](https://github.com/fluphus/brsf-server/releases/tag/v0.1.14)中的已签名客户端 APK。客户端连接本机 Android 宿主；服务端与资源包的构建、导入方式见[仓库说明](../README.md)。补丁范围和运行时行为见 [PATCHES.md](PATCHES.md)。

## 环境

- Python 3.9 或更新版本；构建脚本仅用标准库。
- JDK 21 或更新版本，配置 `JAVA_HOME` 或把 `java`、`keytool` 加入 `PATH`。
- Android SDK Build Tools 36.0.0，使用其中的 `zipalign`、`aapt2` 和 `lib/apksigner.jar`。可通过 Android Studio 的 SDK Manager 安装。

脚本优先使用 `--build-tools` 指定的目录，否则查找 `--sdk`、`ANDROID_HOME`、`ANDROID_SDK_ROOT` 及常见的用户 SDK 目录。`--java` 可指定 Java 可执行文件。以下命令从仓库根目录执行，Windows PowerShell、Linux 与 macOS 均可使用；路径按自己的文件位置替换。

## 准备签名

已有自己的 keystore 时直接使用。首次构建可在仓库外创建一个，`keytool` 会提示输入密码：

```sh
keytool -genkeypair -keystore ../brsf-private/client-signing.p12 -storetype PKCS12 -alias brsf-local -keyalg RSA -keysize 3072 -validity 10000 -dname "CN=BRSF Local Client"
```

先创建 `../brsf-private` 目录。保存好密钥和密码，后续更新继续使用同一个密钥。用新密钥签名的客户端不能直接覆盖已有的不同签名客户端；要延续原安装的数据，请使用该安装对应的签名密钥。

## 一步重建并签名

```sh
python -B client/package_client.py --xapk ../inputs/BRS-FRAGMENT-2.5.0.xapk --keystore ../brsf-private/client-signing.p12 --alias brsf-local --output ../build/BRSFragment-2.5.0-local-arm64.apk
```

脚本隐藏输入 keystore 密码，默认私钥密码与 keystore 相同；不同则加 `--prompt-key-pass`。无需原发布者的签名密钥。密码不作为命令行参数记录。自动化环境可加 `--store-pass-file` 和可选的 `--key-pass-file`，读取仓库外 UTF-8 文件的第一行。密钥、密码文件和原始输入均应放在仓库外。

需要手动指定工具目录时，例如：

```sh
python -B client/package_client.py --xapk ../inputs/BRS-FRAGMENT-2.5.0.xapk --keystore ../brsf-private/client-signing.p12 --alias brsf-local --build-tools ../android-sdk/build-tools/36.0.0 --output ../build/BRSFragment-2.5.0-local-arm64.apk
```

输出为签名 APK 及旁边的 `.apk.report.json`。已有输出文件不会被覆盖。流程会校验原始输入、重建固定补丁、以 4 字节对齐 ZIP、启用 v1/v2/v3 签名并关闭 v4，随后执行 Android SDK 签名、对齐和 Manifest 检查，并核对签名前后的全部游戏载荷。

包名为 `jp.goodsmile.brsfragment`，版本为 `2.5.0 / 203`，最低 Android 8.0（API 26），目标 API 33，仅包含 arm64-v8a。签名私钥不同会改变证书、签名和完整 APK 的 SHA-256；报告中的载荷校验用于确认补丁内容一致。此构建检查不等同于设备运行验证。

## 仅重建未签名载荷

```sh
python -B client/build_client.py --xapk ../inputs/BRS-FRAGMENT-2.5.0.xapk --output ../build/client-unsigned.apk --report ../build/client-unsigned.report.json
```

该文件需先对齐、再用自己的密钥签名后安装。`package_client.py` 已完成这些步骤；配套的 Android 宿主需另外构建与安装。
