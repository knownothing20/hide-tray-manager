# Hide Tray Manager

一个轻量的 Windows 托盘工具，把指定应用窗口隐藏到系统托盘，并通过托盘图标恢复窗口。

使用 C#、Windows Forms 和 Windows 原生窗口 API 实现。无需安装 AutoHotkey。

## 功能

- 为多个应用分别创建托盘图标，左键点击切换显示和隐藏。
- 可设置启动后隐藏、最小化时隐藏，以及禁用窗口关闭按钮。
- 按进程名匹配应用，也可用完整进程路径和窗口标题进一步筛选。
- 托盘菜单支持显示、隐藏和请求退出应用。
- 管理器右键菜单**自动列出正在运行的托管软件**（标注当前状态），点击条目即切换显示/隐藏；未运行的软件不显示，无运行项时给出占位提示。
- 管理器菜单支持打开配置、重新加载配置和退出管理器。
- 配置窗口重新设计：无边框深色窗口（圆角、可拖动标题栏、自动保持在屏幕内）、左侧导航切换"已配置软件/窗口探测"、卡片化表格与 INI 编辑器、底部状态栏集中"加载/展开编辑器/保存并应用"。
- 已配置软件支持**移除**：表格右键或页面按钮移除所选配置段（仅删配置不结束进程，保存后生效，[General] 受保护）。
- 配置窗口可展开编辑 INI，探测当前软件窗口并勾选加入最小化托盘管理。
- 默认显示可编辑的已配置软件表格，未运行或已禁用的软件也会列出。
- 无启用应用时停止后台轮询；启用后只查询配置中的进程名。
- 新进程最多每三秒探测一次，运行中窗口按配置间隔检查；目标未运行时降低轮询频率。

## 目录

```text
HideTrayManager.exe          编译好的发布版本（一键构建部署后永远最新）
src/
  TrayProxy.cs               托盘和窗口管理源码
  AppConfiguration.cs        配置路径和安全默认配置
  ConfigurationForm.cs       配置界面与窗口探测
  ProcessSnapshot.cs         原生进程快照与目标筛选
  assets/
    tray.ico                 发布版本图标
    tray.png                 图标原图
  tests/
    SmokeTests.cs            功能烟雾测试
tools/
  build.ps1                  一键构建部署（停实例→备份→编译→替换→重启）
  test.ps1                   功能测试脚本
  Build-UmbrellaIcon.ps1     PNG 转 ICO 工具
backup/                      本机构建/部署备份，不上传 GitHub
local/                       本机配置和备份，不上传 GitHub
  HideTrayManager.ini        私人应用配置
```

## 使用

1. 下载仓库 ZIP 并解压到可写目录，或克隆仓库。
2. 运行 `HideTrayManager.exe`，首次运行会生成 `local/HideTrayManager.ini`。加 `--config` 参数启动可直接打开配置窗口。
3. 右键管理器托盘图标，选择 **配置与窗口探测...**。
4. 默认的 **已配置软件** 表格直接加载配置文件，包含未运行或已禁用的软件。可编辑名称、进程名和常用选项，点击 **编辑路径与筛选...** 修改完整路径和窗口筛选；**移除所选软件**（或表格右键）删除所选配置段。
5. 点击 **加载配置** 可重新读取本机配置到表格，修改后点击 **保存并应用** 生效。
6. 添加新软件时切换到 **窗口探测**，点击 **刷新窗口列表**，勾选软件后点击 **加入最小化托盘**。加入后回到配置表格。
7. 新增软件不会立即隐藏，也不会自动启动；手动最小化后隐藏到托盘，左键点击应用托盘图标即可恢复。
8. 点击 **展开配置文件** 可编辑完整配置，文本与表格同步。表格编辑保留原有注释和其他配置项。修改 `AutoStart` 后需重启管理器才会执行自动启动。

扫描列表在首次切换到窗口探测或手动刷新时更新。按应用进程管理其窗口；同一进程只列出一条记录，已配置的软件自动跳过。无法读取完整进程路径时，按进程名匹配。窗口标题只用于当前列表展示，自动加入时不会保存标题。

保存前会检查配置是否被其他程序修改，避免覆盖外部编辑，并把上一份配置备份到 `local/backups/`。仍可通过 **Open config** 使用外部编辑器，通过 **Reload config** 重新加载。

默认示例处于禁用状态，不会启动或隐藏任何应用。建议先保持 `AutoStart=0`，手动启动目标应用验证配置。

```ini
[General]
CheckIntervalMs=700

[ExampleApp]
Enabled=0
Name=Example App
Path=
Exe=ExampleApp.exe
ProcessPath=
TitleContains=
AutoStart=0
HideOnLaunch=1
HideWhenMinimized=1
DisableCloseButton=1
ShowNotifications=0
IconPath=
```

| 配置项 | 含义 |
| --- | --- |
| `CheckIntervalMs` | 检查间隔，单位毫秒，必须大于 0。 |
| `Enabled` | `1` 启用此应用，`0` 禁用。 |
| `Name` | 托盘中显示的应用名称。 |
| `Path` | 应用启动文件的完整路径，可留空。 |
| `Exe` | 目标进程文件名，例如 `ExampleApp.exe`；留空时从 `Path` 推导。 |
| `ProcessPath` | 可选：目标进程可执行文件的完整路径，用于区分同名进程。 |
| `TitleContains` | 可选：窗口标题必须包含的文本。 |
| `AutoStart` | 管理器启动时是否启动尚未运行的应用。 |
| `HideOnLaunch` | 首次检测到应用窗口时自动隐藏。 |
| `HideWhenMinimized` | 窗口最小化后隐藏到托盘。 |
| `DisableCloseButton` | 是否禁用被管理窗口的关闭按钮。 |
| `ShowNotifications` | 隐藏窗口时是否显示通知。 |
| `IconPath` | 可选：用于提取图标的可执行文件路径。 |

为其他应用增加独立配置段即可。启动器和实际进程不一致时，`Path` 填启动器路径，`Exe` / `ProcessPath` 填实际窗口进程。

## 编译

需要 Windows 和 .NET Framework 4.x。使用 Windows PowerShell，在仓库目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1
```

输出为根目录的 `HideTrayManager.exe`。脚本会自动停止正在运行的管理器、把旧版备份到 `backup/`、编译替换并重启，不修改私人配置。

如需从 PNG 重新生成图标：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-UmbrellaIcon.ps1 -SourcePng .\src\assets\tray.png -OutputIcon .\src\assets\tray.ico
```

## 隐私和本机配置

私人应用路径、窗口筛选条件和使用偏好只保存在 `local/HideTrayManager.ini`。整个 `local/` 目录和所有 `.ini` 文件都由 `.gitignore` 排除；仓库中的默认示例不包含个人目录或实际应用配置。探测结果不生成公开日志，配置备份和测试预览也只存放在 `local/`。

程序源码不包含联网、遥测或上传配置的逻辑。不要使用 `git add -f` 强制上传本机配置和备份。

## 使用边界

仅适用于 Windows 桌面窗口。受保护的进程、不同权限级别的应用和特殊窗口可能无法管理。**Quit** 会向目标应用请求正常关闭，并非强制结束进程。退出管理器前，请先通过应用托盘菜单恢复需要继续操作的窗口；退出管理器不会自动恢复所有已隐藏窗口。

## 测试

在 Windows 桌面会话运行以下命令。测试会短暂打开合成测试窗口，验证探测、最小化隐藏、恢复窗口、配置编辑、外部修改保护和空配置停止轮询。测试配置、备份和经过脱敏的界面预览仅写入 `local/tests/`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test.ps1
```
