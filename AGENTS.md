# HideTrayManager 项目工作约定（Agent 必读）

本文件是 AI 助手（ZCode 等）在本仓库工作时的强制约定。人工开发者也可参考。

## 语言与交流

- 与用户交流用中文；代码注释解释"为什么"。

## 目录约定（单一工作区，勿改结构）

```
HideTrayManager.exe   根目录 exe = 部署目标，必须永远保持为最新构建（不进 git）
src\                  应用源码 + tests\SmokeTests.cs 烟雾测试
tools\                build.ps1（一键部署）/ test.ps1 / Build-UmbrellaIcon.ps1
backup\               构建部署备份（gitignored）
local\                私有配置 HideTrayManager.ini、测试产物、历史封存（gitignored，绝不提交）
```

- 发布模式与 SysFloat 一致：**源码进仓库、成品进 GitHub Releases、本机根目录 exe 常新**。exe 已被 .gitignore（`*.exe`）排除，不要提交。
- 任何推送前运行 SysFloat 侧同款隐私意识检查：确认没有把 `local\`、真实配置、内部记录加入提交。

## 构建与部署（一条命令）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1
```

自动完成：停止运行实例 → 旧 exe 按时间戳备份到 `backup\` → 编译（系统自带 csc.exe，.NET Framework 4.x，无外部依赖）→ 替换根目录 exe → 重启并自检。改完代码一律跑它。

## 测试（改代码后必跑，全过才提交/部署）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test.ps1
```

烟雾测试覆盖：配置解析与保护、窗口探测、最小化隐藏/恢复、配置表格编辑、移除配置段、外部修改冲突、空配置停止轮询。产物只写 `local\tests\`。

## 发布流程

1. 版本号在 `src\TrayProxy.cs` 的 `[assembly: ...Version]` 修改；**升级前获得用户确认**。
2. `gh release create vX.Y.Z HideTrayManager.exe <sha256文件> --target <完整SHA>`（`--target HEAD` 在本仓库会报 target_commitish invalid，用 `git rev-parse HEAD` 的完整值）。
3. 发布后核对服务端 digest 与本地 SHA-256 一致。

## 代码与 UI 约定（来自实战教训）

- C# 5 语法上限（系统 csc 编译器）：**禁用**字符串插值 `$""`、`?.`、`nameof`、表达式体成员、`out var`。
- 深色主题统一走 `UiTheme`（颜色 + StyleButton/StyleGrid/圆角辅助），新控件必须接入。
- 高 DPI：进程已 `SetProcessDPIAware`；窗体几何在控件树构建完后按 `min(DPI/96, 屏幕适配)` 手动 `Scale()`，运行期动态尺寸用同一 `uiScale` 换算；纯代码窗体 `AutoScaleMode.Dpi` 无效（无设计器基准）。
- WinForms 停靠顺序：内容(Fill) 最先加入、标题栏最后加入；Dock=Right 的 FlowLayoutPanel 必须 `AutoSize = true`，否则按钮在高 DPI 下被裁。
- 无边框窗体拖动：标题条被子控件覆盖时，必须在子控件 MouseDown 上发起（ReleaseCapture + `WM_NCLBUTTONDOWN`，**HTCAPTION=2 放 wParam**）；窗体级 WM_NCHITTEST 只对裸露区域有效。
- DataGridView 右键菜单：CellMouseDown 里先把 CurrentCell 移到右键行，否则操作作用于旧选中行。
- 修改编辑器草稿（SetDraft 抑制 TextChanged）后，凡影响表格内容的路径必须显式 `LoadConfigurationTable()`。
- 私有 API 测试走反射（`BindingFlags.NonPublic`）；MessageBox 会阻塞自动化，交互确认框要可绕过。

## 验证证据的纪律

- 优先客观手段：布局用独立探针量矩形、窗口用 EnumWindows/GetWindowRect、内容用 PrintWindow（不被遮挡影响）、交互用真实输入模拟（置前 + mouse_event）。
- 图像分析服务可作参考但会误判/讨好，结论必须与像素/几何数据交叉核对，矛盾时以客观证据为准。
- 如实汇报失败，包括自己的失误；测试命令显式检查退出码，不要 `| tail` 后接 `&&`（管道吞退出码踩过两次）。
