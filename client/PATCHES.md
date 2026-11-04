# 客户端改动说明

输入是 `jp.goodsmile.brsfragment` 2.5.0 / versionCode 203 的原始 XAPK，包含主 APK 和 `config.arm64_v8a.apk`。目标是保留的 `BRSFragment-2.5.0-local-arm64.apk` 游戏载荷；包名、版本、minSdk 26、targetSdk 33 保持，两个安装分包合并为一个 ARM64 APK。构建和签名方法见 [README.md](README.md)。

`manifest.json` 逐个记录 983 个载荷成员、来源分包、输入与输出摘要和操作：974 个未改成员直接复制；八个改动成员使用 `patches/*.bsdiff`；新增网络配置使用 `additions/`。签名成员重新生成，不能用于比较不同签名 APK 的整文件摘要。

## 运行时改动与载荷对应

| 成员 | 差分文件 | 保留的具体改动 |
| --- | --- | --- |
| `classes.dex` | `patches/004.bsdiff` | 将 32 个字符串持有类的 1,448 个静态字符串恢复为已采集的 UTF-16 值；三个入口改为直接返回：`StartupLauncher.launch()`、`SignatureCheck.verifyIntegrity(Context)`、`LicenseClientV3.onActivityCreate(Activity)`。恢复四个 Firebase Java 回调，见下文。 |
| `lib/arm64-v8a/libunity.so` | `patches/007.bsdiff` | 恢复动态导入、符号/重定位元数据及采集确认的 Unity 静态数据。保留的镜像来自最终原生恢复版本。 |
| `lib/arm64-v8a/libil2cpp.so` | `patches/006.bsdiff` | 同样恢复动态导入及 metadata usage token；在恢复镜像上叠加 11 处、共 484 字节的本地兼容改动，见下文和 `source/native-edits.json`。 |
| `lib/arm64-v8a/libFirebaseCppApp-10_0_1.so` | `patches/005.bsdiff` | 替换为 Firebase Unity App 10.0.1 官方包的同版本 ARM64 库。其 SONAME、9,640 个导出和直接消费者需要的符号均匹配；此库不再依赖 `libpairipcore.so`。 |
| `assets/bin/Data/4924e3f23928142959415eedc614677f`、`assets/bin/Data/fd9c1b5dae75643fdbe0fe2d41db4a5a` | `patches/002.bsdiff`、`patches/003.bsdiff` | 两份 `_release` 服务配置的 API origin 改为 `http://127.0.0.1:18445`。 |
| `AndroidManifest.xml`、`resources.arsc` | `patches/001.bsdiff`、`patches/009.bsdiff` | 增加 `networkSecurityConfig` 和资源 `0x7f0f0002`；删除 `requiredSplitTypes`、`isSplitRequired` 和两条 Play split 元数据，以支持单 APK。未开启 debuggable/testOnly。 |
| 新增 `res/xml/network_security_config.xml` | `additions/008-network_security_config.axml` | 默认禁止明文 HTTP，只对 `127.0.0.1` 放行，不包含子域。 |

原生恢复涉及的动态表、静态数据和 usage token 已包含在两个原生库的最终差分中，不要求使用者重新连接历史研究设备采集内存。11 处后续 IL2CPP 改动是：

- Crashlytics `SetUserId`、`SetCustomKey`、`LogException` 三个包装入口直接返回。
- IAP 初始化等待缩短至 1 秒；本地购买 provider、普通付费商品 gate、VIP gate 和 VIP wrapper 使用本地服务端路径；购买 telemetry 调用跳过。商品校验、幂等和发奖由服务端实现，不产生 Google Play 付款凭据。
- Google Play 评分 gate 走跳过路径。
- CDN origin 使用 API origin，资源请求走同一个本机宿主的 `/Android/...`；API 请求走 `/api/...`。

## Firebase、Google 与 PairIP 的边界

Firebase Java 的四个恢复入口是 `com.google.firebase.iid.v$a.onReceive`、`com.google.firebase.messaging.y0$a.onReceive`、`com.google.firebase.messaging.b1$a.onReceive` 和 `com.google.firebase.messaging.cpp.RegistrationIntentService.onHandleWork`。方法体来自 Firebase IID 21.1.0、Messaging 23.1.0 及 Unity Messaging 10.0.1 的编译库，映射到游戏已有的混淆类名和成员名。最终打包前已移除两处临时 VM 诊断改动，保留这四个恢复后的回调。

这不等于删除 Google、Firebase 或 PairIP：Manifest 仍包含 Firebase 初始化、Messaging、Play Games 和 Billing 组件；Firebase 自动初始化和 token 请求未整体禁用。`com.pairip.application.Application`、`VMRunner`、原始 VM 资源及 `libpairipcore.so` 仍保留，其中 PairIP 原生库与原 XAPK 字节一致。三个许可/完整性入口的兼容改动也不代表所有 VM 回调或原生初始化都被删除。不能据此声称客户端绝不访问外网，或已经通过所有无 GMS 设备的测试。

## 维护参考源码

`source/` 保存独立于开发者机器路径的补丁逻辑：

- `RestoreHolders.java`：字段恢复和三个入口改写，要求完整的字段 TSV。
- `LoopbackContainer.java`：新增网络安全资源和 Manifest 引用，检查原有资源值保持。
- `FirebaseRestore.java`：四个官方 SDK 回调的符号映射与成员契约检查。
- `ReleaseGame.java`：恢复两个临时诊断方法并清理 split 要求。
- `DexSnapshot.java`：上述 DEX 语义比较的辅助程序。
- `native-edits.json`：11 处 IL2CPP 后续改动的名称、偏移、前后字节。其前像是已恢复的原生镜像，不能直接用于原始受保护库。

这些文件用于理解和继续修改补丁，不是原版 Unity 项目的完整源码，也不是第二套从零构建命令。Java 参考程序使用 smali dexlib2、ARSCLib 等第三方 API；其完整历史输入和依赖未打包。当前可移植构建路径是本目录的原版输入校验、完整差分重建、载荷校验及自备密钥签名。

## 审计范围

本次离线审计核对了原 XAPK、最终保留 APK、上述补丁源码和历史构建记录，并直接确认最终原生库及 484 字节后续改动与所述来源相符。原始 APK、完整替换库、玩家数据、签名私钥和密码文件不放入源码树。签名证书、证书摘要和原客户端已有的协议常量属于公开载荷或兼容数据，不是可用于签名的私钥；换用自己的签名密钥不会改变要求重建的游戏载荷。

静态载荷一致不等于新签名安装后的运行验收。本次未安装、启动或操作设备，也未重新验证登录、战斗、外网行为或各厂商系统兼容性。
