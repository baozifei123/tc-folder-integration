# TC Folder Integration

把 Windows 11 的“打开文件夹”交给 [Total Commander](https://www.ghisler.com/)（以下简称 TC），并可选地把新打开的资源管理器窗口自动转交到 TC。

项目最初为微信的“打开文件夹 / 在文件夹中显示”而做；现在的全局转交器不再区分窗口来源，所有新开的顶层资源管理器窗口都会被转交。

> **注意**：本项目会修改 `HKEY_LOCAL_MACHINE\Software\Classes` 下的文件夹打开方式，并可选地修改 Everything 的两个配置项；全局转交器还会关闭新开出的资源管理器窗口。发行版 EXE 没有代码签名。安装前请先阅读[已知限制](#已知限制)，并确认你可以接受这些行为。所有改动都提供还原入口。

## 功能

- **默认文件夹关联**：`Directory`、`Drive` 的 `open` 动词指向 TC；双击文件夹、磁盘时由 TC 打开。
- **Everything 集成**：安装 Everything 时，“打开文件夹”“打开路径”也交给 TC。
- **全局窗口转交（可选）**：新开出的顶层资源管理器窗口自动隐藏、读取路径、用 TC 打开，然后关闭原窗口。
- **单文件管理器**：一个 EXE 带图形界面，负责释放程序文件、选择 TC 路径、安装/还原/查看状态。
- **暂停与还原**：托盘菜单可暂停转交；关联和 Everything 设置可按安装前的备份还原。

## 系统要求

- Windows 11（在 Windows 11 26200 上测试）。
- Total Commander 64 位。默认位置为 `%LOCALAPPDATA%\TotalCMD64\TotalCMD64.exe`，也可以在管理器中选择其他位置。
- .NET Framework 4.x（Windows 11 自带，用于编译和运行转交器）。
- Everything 1.4 可选；未安装时只配置 Windows 文件夹关联。
- 安装文件夹关联需要管理员权限；全局转交器按当前用户运行，不需要管理员权限。

## 安装

### 方式一：发行版 EXE（推荐）

1. 从 [Releases](https://github.com/baozifei123/tc-folder-integration/releases) 下载 `TC-Folder-Integration.exe`，以及同一页面的 `SHA256SUMS.txt`。
2. 校验文件（可选但推荐）：

   ```powershell
   Get-FileHash .\TC-Folder-Integration.exe -Algorithm SHA256
   ```

   输出应与 `SHA256SUMS.txt` 中的哈希一致。
3. 双击 EXE。首次打开只会把程序文件释放到 `%LOCALAPPDATA%\TCFolderIntegration`，**不会修改系统设置**。
4. 确认界面中的 TC 路径；如果路径不对，点“浏览…”选择 `TotalCMD64.exe`。
5. 按需操作：
   - **安装默认文件夹关联**：会请求管理员权限，修改文件夹/磁盘的打开方式；如果检测到 Everything，会同步修改它的两个配置项。
   - **开启全局转交**：写入当前用户的登录启动项，并启动托盘程序。它会转交所有新开的顶层资源管理器窗口，不只是微信。
   - **查看转交状态**、**打开数据和备份目录**：用于检查运行状态和备份。
6. 安装关联前，请先**完全退出 Everything**：在系统托盘右键 Everything 图标选择“退出”，仅关闭搜索窗口不一定退出后台进程。安装完成后重新打开 Everything。

> 发行版 EXE 未签名。Windows SmartScreen 可能提示“未知发布者”；请先核对哈希，或直接阅读源码并从源码构建。

### 方式二：从脚本运行

仓库中的 CMD 脚本提供同样的功能，适合审查源码后使用：

```text
安装-TC默认.cmd       安装默认文件夹关联（管理员权限）
还原.cmd              按最近一次安装的备份还原
全局转交-安装.cmd      写入登录启动项并启动全局转交器
全局转交-卸载.cmd      停止转交器并删除登录启动项
全局转交-状态.cmd      显示转交器运行和启动项状态
```

使用脚本时，TC 路径默认同样为 `%LOCALAPPDATA%\TotalCMD64\TotalCMD64.exe`。如果 TC 在其他位置，在同一目录创建 `tc-path.txt`，写入 TC 可执行文件的绝对路径：

```text
D:\Tools\Total Commander\TotalCMD64.exe
```

安装和还原脚本会在脚本所在目录创建 `backup\`；**请保留该目录**，删除后无法按原状态还原。全局转交器需要先运行 `build.cmd` 生成 `TCWeChatRedirector.exe`，或使用发行版 EXE 中已释放的副本。

### 从旧版脚本安装升级

如果以前用其他目录中的脚本安装过：

1. 先在**原目录**运行原来的 `还原.cmd`，并保留原 `backup\`。
2. 再使用新版 EXE 安装。

新版管理器不会读取旧目录的备份；直接在新位置安装会导致旧设置无法由新程序还原。

## 还原

1. **只停止全局转交**：点击“停止并卸载全局转交”。它会删除当前用户的登录启动项、请求托盘程序退出，并恢复仍在处理队列中的隐藏窗口。不会撤销 TC 的默认文件夹关联。
2. **撤销文件夹关联和 Everything 设置**：先完全退出 Everything，再点击“还原默认文件夹关联”。它使用 `%LOCALAPPDATA%\TCFolderIntegration\backup` 中最近一次安装的备份，只恢复当时改过的值，不整份覆盖 `Everything.ini`。
3. 如果安装过程中发生错误，备份会保留；界面不会把失败报告为成功。请先检查备份和错误信息，再重试还原。

## 它修改了什么

默认文件夹关联安装会写入以下注册表值（64 位视图）：

| 位置 | 值 |
| --- | --- |
| `HKLM\Software\Classes\Directory\shell\open\command` | 默认值改为 TC，删除 `DelegateExecute` |
| `HKLM\Software\Classes\Drive\shell\open\command` | 默认值改为 TC，删除 `DelegateExecute` |
| `HKLM\Software\Classes\Directory\shell\OpenWithExplorer` | 仅在不存在同名菜单时创建“用资源管理器打开” |

TC 命令行：

```text
TotalCMD64.exe /O /T /L="目录"
```

如果 Everything 存在，还会修改 `%APPDATA%\Everything\Everything.ini`：

- `open_folder_command2`
- `open_path_command2`

安装前会把目标值保存到 `backup\<时间戳>-<随机>\state.json`，并保留 `Everything.before.ini`。安装和还原都会先做冲突检查：如果目标值已被其他程序改动，会停止而不是盲目覆盖。

## 工作方式（简要）

全局转交器以当前用户权限运行，不注入微信或资源管理器，不替换 `explorer.exe`：

1. 用 `SetWinEventHook(EVENT_OBJECT_SHOW, WINEVENT_OUTOFCONTEXT)` 监听新出现的顶层 `CabinetWClass` / `ExploreWClass` 窗口。
2. 回调到达后立即隐藏窗口。
3. 通过 `Shell.Application` 找到该窗口，读取目录；如果只有一个选中的文件，也读取文件路径。
4. **先启动 TC**：目录用 `/O /T /L=`，文件用 `/O /A /S`。
5. TC 启动成功后，再向资源管理器窗口发送 `WM_CLOSE`。
6. 尝试把 TC 置前，并在约 3 秒内保持焦点。

先启动 TC、再关闭资源管理器，是为了在 TC 启动失败时保留原窗口。更完整的设计取舍见 [`docs/DESIGN.md`](docs/DESIGN.md)。

## 已知限制

- **不是无闪方案**：窗口先显示、后隐藏，可能短暂闪烁。
- **影响所有新顶层资源管理器窗口**：不区分来源；其他程序、手动打开的窗口都会被转交。
- **不覆盖标签页复用**：如果 Windows 在已有窗口中新增标签，而不是新建顶层窗口，转交器看不到。
- **Win+E 未修改**：新开顶层窗口时可能被转交；只在已有窗口加标签时不会。
- **虚拟目录**：此电脑、回收站、`shell:::` 等非本地路径无法可靠转交，窗口约 1.8 秒后恢复。
- **多标签窗口**：关闭窗口会连同其中的标签一起关闭，可能丢失你正在使用的资源管理器标签。
- **管理员 TC**：如果现有 TC 以管理员权限运行，普通权限转交器可能无法复用该实例，出现两个 TC 窗口。
- **焦点保持**：约 3 秒内主动切换程序，可能被拉回 TC。
- **TC 没有 `/SELECT`**：`/A /S` 只是尝试定位文件，不保证选中。
- **未签名**：发行版 EXE 没有代码签名；请核对哈希或自行构建。

## 隐私

- 转交器只读取窗口的目录和选中文件路径，不读取聊天内容。
- `explorer-redirect-log.txt` 记录被转交的目录路径和错误信息，仅保存在程序目录；不需要时可以直接删除。
- 安装/还原备份保存在本地 `backup\`，包含本机注册表值和 Everything 配置，不会上传。
- 程序不包含联网功能。

## 构建

### 只编译转交器

需要 Windows 自带的 .NET Framework 4.x C# 编译器：

```bat
build.cmd
```

或手动：

```bat
%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /out:TCWeChatRedirector.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TCWeChatRedirector.cs
```

构建前请先从托盘退出正在运行的转交器，否则无法覆盖 EXE。

### 构建发行版 EXE

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-release.ps1
```

脚本会：

1. 在 `dist\build\` 中编译转交器；
2. 把转交器、脚本和管理器编译成单文件 `dist\TC-Folder-Integration.exe`；
3. 运行 `tests\Test-TCIntegration.ps1`：语法检查、临时目录中的路径配置、**独立的临时 HKCU 测试键**中的安装/还原/冲突/保留测试；
4. 运行打包自检：释放文件、校验内容、确认升级不覆盖备份和 `tc-path.txt`、调用转交器 `--selftest`；
5. 生成 `dist\SHA256SUMS.txt`。

测试不会修改真实的文件夹关联，也不会写入 `%LOCALAPPDATA%\TCFolderIntegration`。测试键在结束后删除。

单独运行测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-TCIntegration.ps1
```

打包自检：

```powershell
.\dist\TC-Folder-Integration.exe --selftest
```

## 项目结构

```text
TC-Folder-Integration.cs    单文件管理器（WinForms，内嵌发行文件）
TC-Core.ps1                 注册表和 INI 的安全读写、冲突检查
TC-Manager.ps1              Install / Restore 编排
TCWeChatRedirector.cs       全局转交器（历史文件名，当前为全局模式）
build.cmd                   只编译转交器
build-release.ps1           构建、测试并生成单文件发行版
tests/                      隔离测试
安装-TC默认.cmd / 还原.cmd   双击入口
全局转交-*.cmd              转交器安装 / 卸载 / 状态
docs/DESIGN.md              设计与边界说明
```

## License

MIT，见 [`LICENSE`](LICENSE)。
