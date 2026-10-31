# 一个资源包完成设置

使用配套的 `brs-resources.tar`。它是标准的、不额外压缩的 TAR 归档，包含原来的 `content.zip` 和宿主需要的资源目录、模板、图标及固定协议材料。原游戏资源 ZIP 的字节不变；导入时无需解开 TAR 内的 `content.zip`、手动复制资源或准备第二个输入包。

## 下载资源包

在 [v0.1.14 发行页](https://github.com/fluphus/brsf-server/releases/tag/v0.1.14)下载全部 ZIP 分卷，保存在同一个文件夹：

- `brs-resources.z01`
- `brs-resources.z02`
- `brs-resources.z03`
- `brs-resources.zip`

使用支持分卷 ZIP 的解压工具打开 `brs-resources.zip`，解压得到一个 `brs-resources.tar`。解压工具会自动读取同文件夹中的其余分卷；保留各文件的原名即可。

ZIP 分卷只是下载包装，解压得到的 TAR 与原资源包字节一致。解压需要额外约 8.21 GB 空间；同时保留下载分卷时合计约 16.41 GB。接下来按下面步骤导入这一个 `brs-resources.tar`。

## 导入

Android：停止宿主服务，点击“导入资源包”，在系统文件选择器中选取 `brs-resources.tar`。导入完成后启动服务。

PC：在构建宿主后执行：

```sh
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data --import-content ./brs-resources.tar
dotnet run --project server/Host -c Release --no-build -- --data-directory ./local-data
```

首次导入会把数据存入宿主的数据目录，需要为约 8 GB 资源留出空间。如果资源包也保存在同一设备上，导入时还会同时占用这份源文件的空间。导入器按顺序流式读取，不会把整个归档装入内存，也不额外复制一份外层 TAR。

以后启动无需再次导入。保留源文件有利于重新安装；删除源文件不会删除已导入的资源。

## 旧版本升级

旧版的单独 `content.zip` 缺少这次统一导入所需的补充材料，因此不能直接作为新版的完整资源包。新版 `brs-resources.tar` 已把这些材料合并进去。

已有旧版游戏资源和账号时，使用相同签名覆盖安装宿主，再导入新资源包补齐材料。导入器保留符合版本要求的已有游戏归档和账号，不重新分配账号、不替换存档。数据校验失败时不会把不匹配的材料作为可用资源发布。

取消导入不会让半成品成为可用资源。若应用被系统直接终止，下次可通过宿主的导入临时文件清理功能处理残留，再重试导入。

## 维护者制作资源附件

普通使用者直接下载并导入发行附件，不需要执行本节。

维护者可用 `tools/pack-resources.py` 将现有有效资源材料与原始 `content.zip` 合成一个资源包：

```sh
python tools/pack-resources.py --materials ./release-materials --content ./content.zip --output ./brs-resources.tar
```

打包输入目录只用于维护发行附件，不是编译源码或安装运行的前置条件。材料清单由源码中的资源清单定义；打包工具按清单读取，未列出的存档、日志和签名文件不应进入附件。玩法规则和管理标签由源码构建提供，无需放进资源包。

资源附件、签名私钥、管理员凭据和账号备份不放进源码仓库。固定客户端协议材料随资源附件导入，与每个安装独立生成的管理员凭据及发行签名私钥是不同的内容。
