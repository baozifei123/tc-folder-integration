# 设计说明：全局 Explorer 转交（A 方案）

本文说明 `TCWeChatRedirector.exe` 的工作方式、为什么它不是“无闪”方案，以及已知边界。更详细的本机调研记录不随仓库发布。

## 目标

让 Windows 11 上“新开的资源管理器窗口”自动交给 Total Commander 打开，同时：

- 不修改微信或系统文件；
- 不向微信或 Explorer 注入 DLL；
- 不替换 `explorer.exe`，不全局劫持 Win+E；
- 提供一键安装、暂停和一键卸载。

## 发行版与路径配置

仓库同时提供脚本入口和单文件管理器 `TC-Folder-Integration.exe`。管理器把内嵌的脚本、转交器和 CMD 入口释放到固定目录：

```text
%LOCALAPPDATA%\TCFolderIntegration\
```

选择这个固定目录而不是临时目录，是为了让“还原默认文件夹关联”始终能找到安装时写入 `backup\` 的备份和 `active-v2.txt` 指针。管理器只更新自己的程序文件；升级时不会覆盖 `backup\`、`redirector-*.txt` 或 `tc-path.txt`。如果目标目录已存在且不是本程序创建的（没有 `.tc-folder-integration` 标记），管理器会停止，不覆盖其中任何文件。

TC 路径由程序目录中的 `tc-path.txt` 配置；没有该文件时使用默认路径 `%LOCALAPPDATA%\TotalCMD64\TotalCMD64.exe`。`TC-Core.ps1` 和转交器读取同一个文件名，因此管理器中选择的路径对两部分都生效。修改路径后，已运行的转交器需要重新启动才会使用新路径。

## 工作流程

1. 以当前用户权限启动，安装 `SetWinEventHook(EVENT_OBJECT_SHOW, WINEVENT_OUTOFCONTEXT)`。
2. 收到顶层 `CabinetWClass` / `ExploreWClass` 窗口的显示事件后，立即 `ShowWindow(SW_HIDE)`。
3. 通过 `Shell.Application.Windows()` 找到该窗口，读取当前目录；如果只有一个选中文件，也读取文件路径。
4. 先启动 TC：
   - 目录：`TotalCMD64.exe /O /T /L="目录"`
   - 文件：`TotalCMD64.exe /O /A /S "文件"`（尝试定位）
5. TC 启动成功后，再向 Explorer 窗口发送 `WM_CLOSE`。
6. 尝试把 TC 置前，并在约 3 秒内保持焦点。

先启动 TC、再关闭 Explorer，是为了在 TC 启动失败时保留资源管理器窗口。

## 为什么不能保证无闪

`EVENT_OBJECT_SHOW` 的语义是“窗口已经显示”；`WINEVENT_OUTOFCONTEXT` 的跨进程事件需要排队并异步投递。因此回调到达时，窗口已经可见，隐藏只能缩短可见时间，不能保证零帧。

本机独立测试还观察到：

- 在 `EVENT_OBJECT_CREATE` 时提前隐藏，不能阻止窗口所有者之后再次调用显示；
- 从其他进程调用 `DwmSetWindowAttribute(DWMWA_CLOAK)` 返回 `0x80070005`（访问被拒绝），不能依赖跨进程 DWM 遮蔽。

真正的“显示前拦截”需要 `WINEVENT_INCONTEXT` 或全局 `WH_CBT` 钩子，也就是把代码注入目标进程。A 方案明确不使用这种方式。

## 已知边界

- **影响所有新顶层 Explorer 窗口**：不区分来源；其他程序、手动打开的 Explorer 都会被转交。
- **标签页复用不覆盖**：如果 Windows 在已有 Explorer 窗口中新增标签，而不是新建顶层窗口，转交器看不到。
- **Win+E 未修改**：新开顶层窗口时可能被转交；只在已有窗口加标签时不会。
- **虚拟目录**：此电脑、回收站、`shell:::` 等非本地路径无法可靠转交，窗口会在约 1.8 秒后恢复。
- **多标签窗口**：关闭窗口会连同其中的标签一起关闭。
- **管理员 TC**：如果现有 TC 以管理员权限运行，普通权限转交器可能无法复用该实例。
- **焦点保持**：约 3 秒内主动切换程序，可能被拉回 TC。
- **失败恢复**：TC 启动失败会恢复/保留 Explorer 窗口；关闭失败会恢复显示；退出时会恢复仍在 pending 队列中的隐藏窗口。

## 隐私

日志 `explorer-redirect-log.txt` 记录被转交的目录路径和错误信息，不读取聊天内容。日志只保存在本机；如果不需要，可以直接删除，或在托盘菜单暂停转交器。

## 相关文件

- `TCWeChatRedirector.cs`：转交器源码
- `TC-Folder-Integration.cs`：单文件管理器，内嵌脚本、转交器和 CMD 入口
- `build.cmd`：使用 Windows 自带的 .NET Framework 4.x `csc.exe` 编译转交器
- `build-release.ps1`：构建单文件发行版并运行隔离测试与打包自检
- `全局转交-安装.cmd` / `全局转交-卸载.cmd` / `全局转交-状态.cmd`：入口
- `TC-Core.ps1` / `TC-Manager.ps1`：TC/Everything 文件夹关联的安装与还原
