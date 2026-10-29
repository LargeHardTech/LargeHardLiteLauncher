# LargeHardLiteLauncher

简体中文 | [English](README.en.md) | [Français](README.fr.md) | [Русский](README.ru.md) | [微软式中文](README.zh-ms.md)

一个跑在终端里的 Minecraft 启动器。用 C# + [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) 写成，
NativeAOT 编译成单个 exe，不依赖 .NET 运行时，也不需要图形界面。

界面布局参考 [PCL2](https://github.com/Hex-Dragon/PCL2)，联机部分参考
[HMCL](https://github.com/HMCL-dev/HMCL) 的陶瓦联机实现。

## 功能

**启动**

- 多游戏目录管理，版本列表按数字段排序
- 版本独立设置：内存模式、Java、JVM 参数、游戏参数、窗口尺寸、隔离方式、启动前命令
- 启动前自动补全缺失的库文件（走镜像回退），缺库不再直接拒绝启动
- Java 按版本要求挑「满足条件的最小主版本」；32 位 Java 自动降内存并提示
- 游戏进程用 `DETACHED_PROCESS` 启动，关掉终端窗口不会连带结束游戏
- 会话记录写到 `running-game.json`，关掉启动器再打开能认出还在跑的游戏
- 崩溃后按日志特征给出原因和建议（14 条规则）
- 自定义主页：预设 / 本地文本 / 远程 URL，支持 `${date}` `${player}` `${version}` 等占位符

**下载与安装**

- 原版游戏安装，源可切换（官方 / BMCLAPI 镜像 / 自动）
- 加载器安装：Forge、NeoForge、Fabric、Quilt
- 社区资源搜索安装：Mod、整合包、数据包、资源包、光影（Modrinth 免 key，CurseForge 需自备 key）
- 下载支持断点续传：网络失败保留 `.tmp` 下次续，校验失败才丢弃
- 装 Mod 时自动下载必选前置
- 整合包安装（`.mrpack`）：自动装对应加载器、解压 overrides
- 版本导出为 `.mrpack`，可开启在线匹配减小体积（实测 62 个 Mod 的包从 518 MB 降到 186 MB）

**版本管理**

- Mod 管理：扫描 mods 目录、批量启用/禁用（改名 `.disabled`）、删除、检查更新
- Mod 更新检查：按 jar 的 SHA1 查 Modrinth 最新版本，一键更新
- 资源包 / 光影管理：读写 `options.txt` 与 `optionsshaders.txt` 的真实启用状态
- 补全版本：按版本 json 逐项核对客户端 jar、库、natives、资源文件
- 删除版本（连同存档和配置）

**联机**

- 内嵌陶瓦联机（Terracotta），创建房间 / 加入房间，不需要单独下载
- 公共中继节点列表自动拉取

**JAR 启动**

- 独立运行任意 jar（服务端、客户端、其他 Java 程序）
- 图形化配置内存、GC 预设（G1 / ZGC / Parallel / Serial）、额外 JVM 和程序参数、服务端模式
- 命令输入框直接向进程 stdin 发送指令
- 命令行传入 jar 路径或把 jar 拖到 exe 上，直接进入该页面

## 界面

六个页面，F1~F6 切换：

```
启动 | 下载 | 联机 | JAR | 设置 | 更多
```

- **启动**：账户、版本列表、启动按钮、主页区
- **下载**：原版游戏 / 加载器 / 社区资源
- **联机**：房间创建与加入
- **JAR**：独立 jar 启动
- **设置**：账户、Java、内存、隔离、下载源、外观、性能
- **更多**：帮助指南、鸣谢、小工具

## 运行要求

- Windows 10 及以上（进程创建、窗口枚举用了 Win32 API）
- 启动游戏需要自备 Java，或在「设置 → Java」里一键下载
- 终端建议 100 列以上，最少 80 列

## 构建

需要 .NET 10 SDK，以及 MSVC 工具链（NativeAOT 链接用）。

```bash
python scripts/build.py build      # Debug 构建，保留全部自测入口
python scripts/build.py publish    # Release 发布，产物在 bin/Release/net10.0/win-x64/publish/
```

`scripts/build.py` 会处理三件事：跳过 restore、清理 MSYS 环境变量、加 `IlcUseEnvironmentalTools`
（让 ILCompiler 直接使用环境里的工具链，不通过注册表探测 Visual Studio）。

Debug 构建带自测入口，Release 不带：

```bash
LargeHardLiteLauncher.exe --uitest        # 六个页面构造冒烟
LargeHardLiteLauncher.exe --selftest      # 31 段断言（不能在 AOT 产物上跑）
LargeHardLiteLauncher.exe --vmtest        # 版本管理导航
LargeHardLiteLauncher.exe --packcfgtest   # 资源包/光影配置读写
LargeHardLiteLauncher.exe --updatetest <版本目录名>   # Mod 更新检查
LargeHardLiteLauncher.exe --launchtest <版本目录名>   # 真启动游戏并跟踪日志
```

## 目录结构

```
Core/            与界面无关的逻辑
  Config.cs          配置读写（SourceGenerator 序列化）
  VersionScanner.cs  版本 json 解析、rules 求值
  GameLauncher.cs    启动链路：参数构建、natives、classpath、崩溃分析
  GameDownloader.cs  下载计划与断点续传
  LoaderApi.cs       各加载器的版本清单
  LoaderInstaller.cs Forge/NeoForge/Fabric/Quilt 安装
  ModScanner.cs      mods 目录扫描与启停
  ModUpdateChecker.cs Mod 更新检查
  ModpackExporter.cs 导出 .mrpack
  ModpackInstaller.cs 安装 .mrpack
  PackConfig.cs      资源包/光影的真实启用状态
  TerracottaService.cs 陶瓦联机进程与 REST 接口
  JarLauncher.cs     独立 jar 启动
  DetachedProcess.cs DETACHED_PROCESS 进程创建
  GameSession.cs     运行中的游戏会话记录
  GameWindowFinder.cs 按窗口类名查找游戏窗口
  HomePageLoader.cs  自定义主页加载与占位符
  CrashAnalyzer.cs   崩溃日志分析
  Localization.cs    界面文案
UI/              界面
  MainWindow.cs      主窗口、页面切换
  PageBase.cs        页面基类与 PageId
  LaunchPage.cs      启动页
  VersionManager*.cs 版本管理对话框（概览 / 设置 / Mod / 资源包 / 光影 / 补全）
  DownloadPage.cs    下载页
  DownloadCommunity.cs 社区资源搜索与详情
  DownloadLoaderInstall.cs 加载器安装页
  TerracottaPage.cs  联机页
  JarLaunchPage.cs   JAR 页
  SettingsPage.cs    设置页
  MorePage.cs        更多页
  TaskPanel.cs       任务栏
  AccountManagerUi.cs 账户管理对话框
Tests/           自测代码（Release 构建不参与编译）
  SelfTest.cs        31 段断言
  InheritTest.cs     版本继承合并
  LaunchTest.cs      真启动游戏并跟踪日志
  LoaderTest.cs      加载器安装
  ModTest.cs         Mod 目录扫描
  MsAuthTest.cs      微软登录请求体
Resources/       内嵌资源（陶瓦内核 zip、AGPL 全文）
scripts/         构建与运行脚本
  build.py           构建入口，处理本机的环境问题
  publish.bat        Windows 下双击发布，顺带删 pdb
  run.bat            直接 dotnet run
Program.cs       入口与自测入口
```

## 数据与配置

```
%USERPROFILE%\.largehardlitelauncher\
  config.json         全局配置
  logs\               启动器日志
  java\               用启动器下载的 Java
  terracotta\         解压出来的联机内核
  cache\              主页缓存
  running-game.json   当前运行中的游戏
```

游戏文件和版本仍在 `.minecraft` 目录里，不会动。

## 已知限制

- 只支持 Windows，没有跨平台计划（进程创建和窗口查找依赖 Win32）
- Mod 更新检查只查 Modrinth，CurseForge 的指纹接口需要 key 且限流严格
- 启动器本身没有更新检查，换版本需要手动替换 exe
- 单实例运行，第二个实例只会弹提示
- 整合包导入只支持 `.mrpack`，CurseForge 的 zip 整合包不支持

## 常见问题

**为什么做成终端界面？**

Minecraft 启动器的主要开销在下载、解压、参数拼接，界面只负责呈现这些结果。
图形界面需要额外处理 DPI 缩放、深色模式、高对比度、无障碍读屏和字体回退，
终端可以省掉这一整层，也能通过 ssh 远程使用。

**为什么 24 MB，而 PCL 只有 5 MB？**

口径不同。PCL 依赖系统中安装的 .NET Desktop Runtime（数百 MB），自身只携带业务代码；
本项目自包含运行时，另有 7.75 MB 是内嵌的联机内核。
按同等口径计算，本项目的业务代码本体约 158 KB。

**会不会删除存档？**

不会。唯一会删除文件的功能是「删除版本」，操作前会弹窗确认并标注「此操作不可撤销」。
Mod 的「禁用」是重命名文件，资源包和光影的「禁用」只修改 `options.txt` 中的一行配置。

## 第三方组件

| 组件 | 用途 | 许可 |
|---|---|---|
| [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) | 终端界面框架 | MIT |
| [Terracotta](https://github.com/burningtnt/Terracotta) | 联机内核，以内嵌方式随程序分发 | AGPL-3.0 |
| [EasyTier](https://easytier.cn) | P2P 组网，陶瓦联机的底层 | Apache-2.0 |

Terracotta 的二进制未做修改，源码在其官方仓库；许可全文可在程序内「更多 → 关于」查看。

下载与资源数据来自 [BMCLAPI](https://bmclapi2.bangbang93.com)、
[Modrinth](https://modrinth.com)、[CurseForge](https://www.curseforge.com)、
[Adoptium](https://adoptium.net)，Mod 中文名参考 [MC 百科](https://www.mcmod.cn)。

## 许可证

本项目代码以 [WTFPL v2](LICENSE) 授权，不附带任何担保。

例外：内嵌分发的 Terracotta 二进制未做修改，仍受上游 AGPL-3.0 约束。
本项目许可证只覆盖自身代码，不改变该二进制的授权条件。

## 参考

- [PCL2](https://github.com/Hex-Dragon/PCL2)：界面布局、版本管理、整合包导出
- [HMCL](https://github.com/HMCL-dev/HMCL)：参数展开规则、加载器安装、陶瓦联机接入方式
