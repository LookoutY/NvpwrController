# NvpwrController

面向部分 NVIDIA RTX 笔记本显卡的实验性功耗控制工具，使用原生 C# / WPF 构建，
提供 KDU 与 EfiGuard 两种驱动加载方式、功耗策略回读和系统安全状态面板。
当前版本：**3.0.6**。

> **使用风险**：本工具涉及内核驱动和签名校验状态，可能引发蓝屏、数据丢失或安全软件告警。
> 超出 OEM 功耗限制可能损坏供电及散热部件。175–300 W 是程序输入范围，**不是硬件安全额定范围**。
> 本项目不承诺所有 GPU、Windows 或 NVIDIA 驱动版本兼容，也不承诺反作弊兼容。

## 下载与运行

从 [GitHub Releases](https://github.com/LookoutY/NvpwrController/releases/latest) 下载
[Nvpwr.exe](https://github.com/LookoutY/NvpwrController/releases/latest/download/Nvpwr.exe)，放到一个可写文件夹后运行。
无需 BAT、PS1、PowerShell 启动器或外置 XAML。源码树不保存编译产物，EXE 通过 Releases 附件分发。

环境要求：Windows 10 1809+ / Windows 11 x64、.NET Framework 4.8。
内核操作需要管理员权限；具体硬件和 NVIDIA 驱动兼容性由随附底层组件决定，检测到型号不等于已经验证可用。

1. 首次可用 `Nvpwr.exe --read-only` 查看真实状态，不自动加载驱动。
2. 正常启动会请求管理员权限，并在检查通过后尝试加载上次选择的后端；不会自动应用功耗。
3. 阅读风险说明，确认供电和散热条件后，选择功耗并点击“应用功耗”。观察实际回读及外部温度监控。
4. 每次操作的独立日志位于右侧；回读不符、调用超时或恢复失败时，按明确提示处理，不反复强行加载。

不要从 `obj` 构建中间目录启动程序。需要只查看界面时使用 `--preview`，需要手动加载时使用 `--manual-load`。

## 界面与功能

- 固定 940 × 550，灰蓝主题，三页一屏显示，右侧大日志。
- 功耗输入、下拉选择及校验统一为 175–300 W，5 W 步进。
- 主页可切换 KDU / EfiGuard。活动会话未结束或 DSE 尚待恢复时禁止切换。
- 每次检测、加载、应用、重启、结束和恢复都有独立日志组，记录时间、序号、后端及结果；日志下拉框可查看之前的操作。
- 每次程序运行的日志另存于 EXE 旁的 `NvpwrData/logs/运行时间-标识/`，不混入其他启动会话。
- 正常启动请求管理员权限并尝试准备驱动，不自动应用功耗。
- 应用无需逐次确认；保留风险勾选及输入、回读、驱动、恢复等异常告警。
- 测试签名模式显示当前与启动配置状态，右侧“测试模式设置”经选择与确认修改下次启动选项；Secure Boot、HVCI 保留各自系统设置入口。

## 两种加载方式

### 为什么 EfiGuard 点不了

正常启动会尝试加载上次保存的后端。出现“驱动会话已就绪”时，两个后端选择按钮均被锁定，
避免在同一会话内混用不同的加载与恢复方式。**先点“结束会话”，等待完成，再切换后端并点“加载驱动”。**
操作进行中或 DSE 待恢复也会锁定，界面会直接显示具体原因，灰色按钮悬停同样可查看。
这不是“电脑不支持 EfiGuard”的检测结果；选择成功也不表示 EFI 钩子已经安装。
“加载方式”旁的说明按钮始终可用，打开 EXE 内嵌的 [选用与操作指南](README.txt)。

### 如何选择

| 情况 | 建议与边界 |
| --- | --- |
| KDU 已能正常加载 | 优先沿用，无需为了切换后端改动启动链。 |
| KDU 在当前 Windows/阻止策略下加载失败 | 先核对日志和兼容性；了解 EFI 启动与恢复、确认平台支持后才考虑 EfiGuard，不保证切换就成功。 |
| 驱动已加载，但功耗回读异常或 NVIDIA `0x65` 超时 | 两种后端共用同一个功耗驱动，更换加载方式不保证解决。 |
| 必须维持系统保护或反作弊环境 | 不建议采用 EfiGuard；无法满足条件时停止使用绕过加载路线，不自动关闭更多保护。 |

KDU 在 Windows 内借助有签名但存在漏洞的第三方驱动临时调整 DSE，再通过系统服务加载 Nvpwr，
本工具随后恢复原始 DSE。它不要求事先从 EFI 启动盘启动，但仍有漏洞驱动、PatchGuard 和蓝屏风险。
EfiGuard 在 Windows 启动前修补启动链与内存内核，涉及 PatchGuard 和 EFI 运行时钩子；
本工具在进入 Windows 后使用已有钩子加载 Nvpwr。它更改了更早的启动环境，不是更安全的“一键替代”。
两者的功耗范围、Nvpwr.sys 与 NvpwrCtl.exe 相同，均不要求测试签名模式，也均不保证反作弊兼容。

### EfiGuard 操作

1. 保存工作、准备 BitLocker 恢复密钥，并确认能通过固件菜单选择原来的 Windows Boot Manager。先核对当前 Windows 与随附 EfiGuard 的兼容性。
2. 到“运行依赖 → 导出 EFI 文件”，导出到空目录；把完整 `EFI` 目录复制到 FAT32 U 盘根目录，最终路径为 `EFI/Boot/bootx64.efi` 和 `EFI/Boot/EfiGuardDxe.efi`。不要覆盖系统 EFI 分区或现有引导。
3. 未纳入固件信任链的随附 EFI 文件通常需要手动关闭 Secure Boot；自己签名/管理证书是另一种高级配置，本工具不会自动处理。还需确认 HVCI 与 VBS **实际未运行**，仅关闭内存完整性不一定关闭 VBS。不接受降低保护时不要继续，也不要删除系统策略文件。
4. 通过固件一次性启动菜单选择 U 盘的 UEFI 项，应看到 EfiGuard 的启动信息并进入 Windows；如补丁失败或版本不兼容，应停止尝试。
5. 打开 Nvpwr。若已有 KDU 会话，先“结束会话”，再选 EfiGuard 并“加载驱动”。上次保存 EfiGuard 的情况下会自动尝试，但仍须 EFI 钩子检测通过。选择按钮本身不会安装 EFI 补丁。
6. 程序按“检查钩子 → 读取 DSE 原值 → 临时关闭 → 加载驱动 → 恢复并读回”处理；就绪后才应用功耗。检查失败时核对本次启动路径和日志，不连续强行重试。

退出 EFI 路线时，保存工作并**完整重启到原来的 Windows Boot Manager**，不要再次选择 EfiGuard U 盘，
再按需要恢复系统保护并核对状态。结束 Nvpwr 会话或恢复 DSE 不等于撤销当前启动中的 EFI 补丁，
拔出 U 盘、关闭窗口或改选 KDU 也不能使本次已启动的内核重新初始化。

本地 EfiDSEFix 的 `-r` 是读取，**不是恢复**；本工具用 `-e 原值` 恢复并再次读取验证。
其控制台输出通过原生 Windows ConPTY 捕获，不经过 PowerShell 或 CMD。

“运行依赖”中的 EFI 导出只将两个启动文件写入你选择的空目录，拒绝覆盖已有文件。
它不会挂载系统分区、修改 UEFI 启动项或自动重启。更改固件设置前应准备 BitLocker 恢复密钥。

上游依据：[EfiGuard 使用与限制](https://github.com/Mattiwatti/EfiGuard)、[KDU 原理与风险](https://github.com/hfiref0x/KDU)。

## 功耗回读

目标与 `Current F7` 不一致时禁止继续写入，并提示重启显卡。
完整且一致的 `STOCK_BASELINE` 状态单独显示“已处于原厂功耗”，
因此不会仅因本地控制器对 175 W 原厂状态返回退出码 6 / Win32=23 而误报未生效。
缺失、矛盾或底层错误状态不适用该例外。

F7 是驱动策略回读，不是实际耗电，也不是硬件安全额定值。
内核操作可能蓝屏，修改功耗可能损坏供电或散热部件；没有测试模式水印不代表系统保护没有被改变。
程序不会自动删除系统策略、关闭驱动阻止列表、绕过 HVCI 或承诺反作弊兼容。

`0x65 / NV_ERR_TIMEOUT` 表示 NVIDIA 驱动调用超时，即使读回值相同也不视为正常完成。
程序保留原始 NVIDIA/NT 状态、退出码和调用耗时，不自动重复写入或重启显卡。
偶发时稍候再手动尝试一次；持续发生则停止频繁调节。仅凭该状态不能确定温度、供电或内部竞争等根因。

Windows 服务通过 SCM 原生 API 查询，已有停止服务会复用并更新路径；创建冲突 1073 会重新查询，
不会直接删除服务，也不会接管其他路径正在运行的驱动。

## 文件布局

```text
.gitignore            构建产物、运行数据与本地资料的忽略规则
.gitattributes        文本及二进制资源属性
app/                  原生 C# / XAML 源码、图标与应用清单
tests/Native.Tests.cs  原生隔离回归测试
vendor/               七个必要的原始二进制构建输入及来源说明
Nvpwr.csproj           可重复构建的 MSBuild 项目
README.md             项目说明
README.txt            内嵌到 EXE 的用户说明

以下仅保留在本地，不提交 Git：
release/Nvpwr.exe      编译生成的独立程序
release/NvpwrData/     运行组件、设置和日志
obj/                  可删除、可重新生成的中间文件
.local/               旧版本归档及保留的历史运行资料
```

### GitHub 上传范围

提交 `app/`、`tests/`、`vendor/`、项目文件、两份 README 和 Git 配置文件。
不要提交 `obj/`、`bin/`、`release/`、任何 `NvpwrData/`、`.local/` 或旧 ZIP。
这些目录已经列入 `.gitignore`；运行日志可能包含本机路径，不应混入源码仓库。

`vendor` 中的 EXE、SYS、DLL、EFI 是当前项目明确引用的构建输入，不能作为缓存删除，
也没有使用会误排除它们的全局二进制忽略规则。`.gitattributes` 将这些资源和图标按二进制处理。
公开上传这些第三方组件或分发内嵌它们的 EXE 前，应核对各自许可证、来源与再分发条件；
本项目没有替上游授予额外许可，也没有擅自指定新的开源许可证。

所有自动生成文件统一收拢到 `Nvpwr.exe` 旁的 **NvpwrData** 文件夹，EXE 同级不散落组件和日志：

```text
Nvpwr.exe
NvpwrData/
	Nvpwr.sys
	NvpwrCtl.exe
	KDU.exe
	drv64.dll
	EfiDSEFix.exe
	backend.txt
	startup.log
	README.txt
	logs/
	diagnostics/ui/
```

上述组件和文件按需生成；KDU 的 EXE 与数据库保持在同一文件夹内。
程序不再使用 AppData 或按版本划分的 runtime 缓存。
路径以 EXE 位置为准，与启动时的工作目录无关；目录不可写时报告错误，不回退到其他位置。
EFI 启动文件仍只导出到用户主动选择的位置，以保留必要的 `EFI/Boot/` 目录结构。
复制 `release/Nvpwr.exe` 即可携带程序，首次使用时会创建旁边的 `NvpwrData` 并释放所需组件，不生成启动脚本。

## 构建与验证

要求 Windows 10 1809+ / Windows 11 x64、.NET Framework 4.8。
用 Visual Studio / Build Tools 打开或构建 [Nvpwr.csproj](Nvpwr.csproj)：

```console
MSBuild.exe Nvpwr.csproj /p:Configuration=Release /p:Platform=x64
release\Nvpwr.exe --check
release\Nvpwr.exe --self-test
release\Nvpwr.exe --live-self-test
```

`--check` 仅检查内嵌资源；`--self-test` 运行原生隔离测试、分组日志与界面布局测试；
`--live-self-test` 经过真实 WPF 和系统状态查询，强制只读，不提权、不加载驱动。
结果位于 EXE 旁的 `NvpwrData/startup.log`，截图位于 `NvpwrData/diagnostics/ui/`。

`--preview` 为明确标注的模拟界面，`--read-only` 查看本机状态且禁止工具写入，
`--manual-load` 请求管理员权限但关闭启动自动加载。日常验证禁止使用无参数启动来代替自测。

验证覆盖原生协议、驱动状态机的模拟调用、设置条件、日志分组、固定布局及真实只读状态查询。
模拟测试和只读启动通过不等于已验证某一硬件在目标功耗下安全稳定。

## 上游与许可

- [原功耗项目](https://github.com/LevinAi-arch/rtx-5070ti-laptop-160w-power-limit)：Nvpwr 内核驱动与命令行控制器的来源参考。
- [KDU](https://github.com/hfiref0x/KDU)：内核驱动加载工具。
- [EfiGuard](https://github.com/Mattiwatti/EfiGuard)：EFI 启动与 DSE 辅助工具。
- [NVIDIA 状态码定义](https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/src/common/sdk/nvidia/inc/nvstatuscodes.h)：驱动状态诊断参考。

KDU、EfiGuard 和 Nvpwr 二进制仍受各自上游许可约束；新的界面不改变其归属或再分发要求。
已保留 [KDU 的 MIT 许可原文](vendor/LICENSE.KDU.txt) 和
[EfiGuard 的 GPLv3 许可原文](vendor/LICENSE.EfiGuard.txt)。原功耗项目没有声明许可证，
公开可下载不等于授予再分发许可；本仓库也不为第三方组件额外授权。
随附文件是原本地项目的构建输入，未证明与某个上游源码版本逐字对应，详见 [组件说明](vendor/README.txt)。
EXE 和窗口图标由 [NVIDIA 官方矢量标志](https://www.nvidia.com/en-us/about-nvidia/legal-info/logo-brand-usage/) 生成，
包含 16、20、24、32、40、48、64、96、128、256 像素十个独立的 32 位图像帧，适配小图标、大图标和高 DPI。
矢量源保留在 [app/nvidia-logo.svg](app/nvidia-logo.svg)，ICO 位于 [app/nvidia.ico](app/nvidia.ico)。
本工具是独立项目，不代表 NVIDIA 官方出品或认可。