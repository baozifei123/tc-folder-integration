# Total Commander 默认文件管理器（Windows 11）

把 Windows 11 的“打开文件夹”行为交给 Total Commander（TC），并可选地把所有新开的顶层资源管理器窗口转交给 TC。项目最初为微信的“打开文件夹 / 在文件夹中显示”设计，现在的全局转交器不再区分窗口来源。

> 这个工具会修改注册表中的文件夹关联，并会关闭新开的资源管理器窗口。请先阅读“已知限制”，确认接受后再安装。所有改动都提供还原入口。

## 功能

- `Directory` / `Drive` 的 `open` 动词指向 TC
- Everything：打开文件夹 / 打开路径使用 TC
- 全局转交器：新顶层 Explorer 窗口 -> TC（A 方案）
- 一键安装 / 卸载 / 状态

在 Windows 11 26200 上测试。

## 要求

- Windows 11
- Total Commander 64 位，默认路径 `%LOCALAPPDATA%\TotalCMD64\TotalCMD64.exe`
- Everything 1.4（可选；未安装时只配置 Windows 文件夹关联）
- .NET Framework 4.x（Windows 11 自带，用于编译转交器）

如果 TC 或 Everything 安装在其他位置，请修改 `TC-Manager.ps1` 里的 `$tc`、`$evExe`、`$evIni`。

## 安装

### 1. TC 默认文件管理器

1. 在通知区域右键 Everything 图标，选择“退出”。关闭搜索窗口不一定会退出后台进程。
2. 双击 `安装-TC默认.cmd`，按提示确认管理员权限。
3. 完成后手动重新打开 Everything。

安装逻辑：

- 安装前把目标值备份到 `backup\<时间戳>-<随机>\state.json`；
- 只修改目标注册表值和 Everything 的两个配置项，不整份覆盖 `Everything.ini`；
- 如果目标值已被其他程序修改，安装或还原会停止并提示，不盲目覆盖。

### 2. 全局转交器（可选）

双击 `全局转交-安装.cmd`：

- 写入当前用户登录启动项 `TCWeChatRedirector`；
- 启动转交器，通知区域出现图标。

需要临时使用 Explorer 时，右键托盘图标选择“暂停/恢复转交”。

## 一键还原

### 只移除全局转交器

双击 `全局转交-卸载.cmd`：

- 删除当前用户登录启动项；
- 请求转交器退出并等待进程结束；
- 恢复尚未处理的隐藏窗口；
- 写入 `redirector-status.txt`。

这不会撤销 TC 作为默认文件管理器的设置。

### 撤销 TC/Everything 设置

双击 `还原.cmd`。它使用最近一次安装的备份，只恢复当时改过的值，不覆盖无关设置。全局转交器需要单独用 `全局转交-卸载.cmd` 移除。

## 工作方式（简要）

全局转交器使用 `SetWinEventHook(EVENT_OBJECT_SHOW, WINEVENT_OUTOFCONTEXT)` 监听新顶层 Explorer 窗口：

1. 窗口已经显示后，回调到达；
2. 立即隐藏窗口；
3. 通过 `Shell.Application` 读取目录和选中文件；
4. 先启动 TC，成功后再关闭 Explorer；
5. 尝试把 TC 置前。

详细说明、测试限制和设计取舍见 [`docs/DESIGN.md`](docs/DESIGN.md)。

## 已知限制

- **不是无闪方案**：窗口先显示、后隐藏，可能短暂闪烁。
- **影响所有新顶层 Explorer 窗口**：不区分来源；其他程序、手动打开的 Explorer 都会被转交。
- **不覆盖标签页复用**：如果 Windows 在已有窗口中新增标签，而不是新建顶层窗口，转交器看不到。
- **Win+E 未修改**：新开顶层窗口时可能被转交；只在已有窗口加标签时不会。
- **虚拟目录**：此电脑、回收站、`shell:::` 等非本地路径无法可靠转交，窗口约 1.8 秒后恢复。
- **多标签窗口**：关闭窗口会连同其中的标签一起关闭，可能丢失你正在使用的 Explorer 标签。
- **管理员 TC**：如果现有 TC 以管理员权限运行，普通权限转交器可能无法复用该实例，出现两个 TC 窗口。
- **焦点保持**：约 3 秒内主动切换程序，可能被拉回 TC。
- TC 没有 `/SELECT` 命令行参数；`/A /S` 只是尝试定位文件，不保证选中。
- 安装/还原脚本会修改注册表并需要管理员权限；请保留 `backup/`，不要把它提交到公开仓库。

## 构建

`TCWeChatRedirector.exe` 不提交到仓库。用 Windows 自带的 .NET Framework 4.x 编译：

```bat
build.cmd
```

或手动：

```bat
%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /out:TCWeChatRedirector.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TCWeChatRedirector.cs
```

构建前请先从托盘退出正在运行的转交器。程序文件名 `TCWeChatRedirector.exe` 是历史名称，当前执行的是全局转交。

## 隐私

- 转交器只读取窗口的目录和选中文件路径，不读取聊天内容。
- 日志 `explorer-redirect-log.txt` 会记录被转交的目录路径，仅保存在本机；不需要时可以直接删除。
- 安装/还原备份在 `backup/`，包含本机注册表值和 Everything 配置，不应提交到公开仓库（已在 `.gitignore` 中排除）。

## 文件

- `TC-Core.ps1` / `TC-Manager.ps1`：TC/Everything 安装与还原
- `安装-TC默认.cmd` / `还原.cmd`：双击入口
- `安装-TC默认.ps1` / `还原.ps1`：命令行兼容入口
- `TCWeChatRedirector.cs`：全局转交器源码
- `build.cmd`：编译脚本
- `全局转交-安装.cmd` / `全局转交-卸载.cmd` / `全局转交-状态.cmd`：转交器入口
- `docs/DESIGN.md`：设计说明
- `LICENSE`：MIT

## License

MIT
