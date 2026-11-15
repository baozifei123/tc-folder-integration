// Single-file launcher. Embedded payload is extracted to a stable per-user directory.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("TC Folder Integration")]
[assembly: AssemblyDescription("Windows folder integration manager for Total Commander")]
[assembly: AssemblyProduct("TC Folder Integration")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

internal static class Payload
{
    private const string MarkerName = ".tc-folder-integration";
    private static readonly string[,] Files = {
        { "payload.core", "TC-Core.ps1" },
        { "payload.manager", "TC-Manager.ps1" },
        { "payload.redirector", "TCWeChatRedirector.exe" },
        { "payload.install", "安装-TC默认.cmd" },
        { "payload.restore", "还原.cmd" },
        { "payload.redirect.install", "全局转交-安装.cmd" },
        { "payload.redirect.uninstall", "全局转交-卸载.cmd" },
        { "payload.redirect.status", "全局转交-状态.cmd" }
    };

    private static byte[] ResourceBytes(string name)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (stream == null) throw new InvalidOperationException("缺少内置文件：" + name);
            using (var output = new MemoryStream())
            {
                stream.CopyTo(output);
                return output.ToArray();
            }
        }
    }

    private static bool SameBytes(string path, byte[] desired)
    {
        if (!File.Exists(path)) return false;
        byte[] current = File.ReadAllBytes(path);
        if (current.Length != desired.Length) return false;
        for (int i = 0; i < current.Length; i++)
            if (current[i] != desired[i]) return false;
        return true;
    }

    private static bool RedirectorRunningFrom(string path)
    {
        foreach (Process process in Process.GetProcessesByName("TCWeChatRedirector"))
        {
            try
            {
                if (string.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // If its location cannot be checked, do not risk replacing a running executable.
                return true;
            }
            finally { process.Dispose(); }
        }
        return false;
    }

    private static void ReplaceFile(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Ensure(string directory)
    {
        // Read every resource before changing the destination.
        byte[][] bytes = new byte[Files.GetLength(0)][];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = ResourceBytes(Files[i, 0]);

        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, MarkerName);
        if (!File.Exists(marker))
        {
            if (Directory.GetFileSystemEntries(directory).Length != 0)
                throw new IOException("数据目录已存在但不是本程序创建的，未覆盖其中任何文件：\n" + directory);
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write("TC Folder Integration data directory\r\n");
        }

        string redirector = Path.Combine(directory, "TCWeChatRedirector.exe");
        if (File.Exists(redirector) && !SameBytes(redirector, bytes[2]) && RedirectorRunningFrom(redirector))
            throw new IOException("需要更新全局转交器，请先从其托盘菜单选择“退出（保留登录启动）”，再重新打开管理器。");

        for (int i = 0; i < bytes.Length; i++)
        {
            string path = Path.Combine(directory, Files[i, 1]);
            if (!SameBytes(path, bytes[i])) ReplaceFile(path, bytes[i]);
        }
    }

    public static void Verify(string directory)
    {
        for (int i = 0; i < Files.GetLength(0); i++)
            if (!SameBytes(Path.Combine(directory, Files[i, 1]), ResourceBytes(Files[i, 0])))
                throw new InvalidOperationException("内置文件校验失败：" + Files[i, 1]);
    }
}

internal sealed class ManagerForm : Form
{
    private readonly string dataDirectory;
    private readonly TextBox tcPath;

    public ManagerForm(string directory)
    {
        dataDirectory = directory;
        Text = "TC Folder Integration";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(680, 460);
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 9F);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 10, Padding = new Padding(22, 18, 22, 18)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        for (int i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scroll.Controls.Add(layout);
        Controls.Add(scroll);

        AddSpanLabel(layout, 0, "Total Commander 文件夹集成", new Font(Font.FontFamily, 15F, FontStyle.Bold), new Padding(3, 0, 3, 6));
        AddSpanLabel(layout, 1, "选择 TC 程序，然后按需操作。首次打开只准备文件，不会更改系统设置。", Font, new Padding(3, 0, 3, 16));
        AddSpanLabel(layout, 2, "Total Commander 可执行文件", Font, new Padding(3, 0, 3, 4));

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(3, 0, 3, 14) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        pathRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tcPath = new TextBox { Dock = DockStyle.Fill, Text = ReadTcPath(), Margin = new Padding(0, 2, 6, 0) };
        pathRow.Controls.Add(tcPath, 0, 0);
        var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 30), Margin = new Padding(0) };
        browse.Click += delegate { BrowseTc(); };
        pathRow.Controls.Add(browse, 1, 0);
        AddSpanControl(layout, 3, pathRow);

        AddSpanLabel(layout, 4, "默认文件夹关联（需要管理员权限；有 Everything 时请先完全退出）", Font, new Padding(3, 0, 3, 8));
        AddButtonRow(layout, 5, "安装默认文件夹关联", delegate { InstallAssociation(); }, "还原默认文件夹关联", delegate { RestoreAssociation(); });
        AddSpanLabel(layout, 6, "全局窗口转交（可选；会影响所有新打开的资源管理器窗口）", Font, new Padding(3, 12, 3, 8));
        AddButtonRow(layout, 7, "开启全局转交", delegate { InstallRedirector(); }, "停止并卸载全局转交", delegate { UninstallRedirector(); });
        AddButtonRow(layout, 8, "查看转交状态", delegate { RunScript("全局转交-状态.cmd"); }, "打开数据和备份目录", delegate { OpenDataDirectory(); });
        AddSpanLabel(layout, 9, "重要：旧版脚本安装请先用原目录还原；本程序不会读取旧目录的备份。\r\n更改 TC 路径后，已运行的全局转交器需重新启动才会使用新路径。", Font, new Padding(3, 14, 3, 6));
    }

    private static void AddSpanLabel(TableLayoutPanel layout, int row, string text, Font font, Padding margin)
    {
        var label = new Label { Text = text, Font = font, AutoSize = true, MaximumSize = new Size(760, 0), Margin = margin };
        AddSpanControl(layout, row, label);
    }

    private static void AddSpanControl(TableLayoutPanel layout, int row, Control control)
    {
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    private static void AddButtonRow(TableLayoutPanel layout, int rowIndex, string leftText, EventHandler left, string rightText, EventHandler right)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(3, 0, 3, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(CreateButton(leftText, left), 0, 0);
        row.Controls.Add(CreateButton(rightText, right), 1, 0);
        AddSpanControl(layout, rowIndex, row);
    }

    private static Button CreateButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 38), Margin = new Padding(0, 0, 6, 0) };
        button.Click += handler;
        return button;
    }

    private string ReadTcPath()
    {
        string config = Path.Combine(dataDirectory, "tc-path.txt");
        if (File.Exists(config))
        {
            string path = File.ReadAllText(config, Encoding.UTF8).Trim();
            if (path.Length > 0) return path;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotalCMD64", "TotalCMD64.exe");
    }

    private void BrowseTc()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "选择 Total Commander 可执行文件";
            dialog.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            dialog.CheckFileExists = true;
            if (dialog.ShowDialog(this) == DialogResult.OK) tcPath.Text = dialog.FileName;
        }
    }

    private bool SaveTcPath()
    {
        string path = tcPath.Text.Trim();
        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "请选择存在的 Total Commander 可执行文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            string config = Path.Combine(dataDirectory, "tc-path.txt");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(config) || File.ReadAllText(config, Encoding.UTF8).Trim() != fullPath)
                File.WriteAllText(config, fullPath + Environment.NewLine, new UTF8Encoding(true));
            tcPath.Text = fullPath;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法保存 TC 路径：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool Confirm(string message)
    {
        return MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private void InstallAssociation()
    {
        if (!Confirm("将请求管理员权限，修改系统文件夹/磁盘的打开方式；如果检测到 Everything.ini，也会修改其中两个选项。\n\n请先完全退出 Everything。如果以前使用旧目录安装，请先从原目录还原并保留原备份。\n\n继续吗？")) return;
        if (SaveTcPath()) RunScript("安装-TC默认.cmd");
    }

    private void RestoreAssociation()
    {
        if (!Confirm("将使用本程序数据目录中的备份还原文件夹关联及 Everything 设置。请先完全退出 Everything。\n\n旧目录的安装必须在旧目录中还原。继续吗？")) return;
        RunScript("还原.cmd");
    }

    private void InstallRedirector()
    {
        if (!Confirm("全局转交会在登录后运行，并可能关闭所有新打开的资源管理器窗口及其中的标签页；窗口可能闪烁。\n\n这不局限于微信。确定启用吗？")) return;
        if (SaveTcPath()) RunScript("全局转交-安装.cmd");
    }

    private void UninstallRedirector()
    {
        if (!Confirm("将删除当前用户的全局转交登录启动项，并请求正在运行的转交器退出；不会还原默认文件夹关联。继续吗？")) return;
        RunScript("全局转交-卸载.cmd");
    }

    private void RunScript(string name)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dataDirectory, name)) {
                WorkingDirectory = dataDirectory, UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法启动操作：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDataDirectory()
    {
        try { Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal static class Program
{
    private static int SelfTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TCFolderIntegration-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Payload.Ensure(directory);
            Payload.Verify(directory);
            string backup = Path.Combine(directory, "backup");
            Directory.CreateDirectory(backup);
            string saved = Path.Combine(backup, "keep.txt");
            File.WriteAllText(saved, "keep");
            string redirector = Path.Combine(directory, "TCWeChatRedirector.exe");
            string config = Path.Combine(directory, "tc-path.txt");
            File.WriteAllText(config, redirector + Environment.NewLine, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(directory, "TC-Core.ps1"), "outdated");
            Payload.Ensure(directory);
            Payload.Verify(directory);
            if (File.ReadAllText(saved) != "keep" || File.ReadAllText(config, Encoding.UTF8).Trim() != redirector)
                throw new InvalidOperationException("更新内置文件时更改了备份或路径设置。");

            string unmanaged = Path.Combine(directory, "unmanaged");
            Directory.CreateDirectory(unmanaged);
            string existing = Path.Combine(unmanaged, "existing.txt");
            File.WriteAllText(existing, "do not overwrite");
            bool rejected = false;
            try { Payload.Ensure(unmanaged); }
            catch (IOException) { rejected = true; }
            if (!rejected || File.ReadAllText(existing) != "do not overwrite")
                throw new InvalidOperationException("未正确保护已有的非本程序目录。");

            using (var child = Process.Start(new ProcessStartInfo(redirector, "--selftest") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!child.WaitForExit(15000))
                {
                    child.Kill();
                    child.WaitForExit();
                    throw new InvalidOperationException("全局转交器自检超时。");
                }
                string result = Path.Combine(directory, "redirector-selftest.txt");
                if (child.ExitCode != 0 || !File.Exists(result) || File.ReadAllText(result, Encoding.UTF8).Trim() != "PASS")
                    throw new InvalidOperationException("全局转交器自检失败。");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCFolderIntegration");
            if (args.Length == 2 && args[0] == "--data-dir")
            {
                directory = Path.GetFullPath(args[1]);
            }
            else if (args.Length != 0)
            {
                throw new ArgumentException("用法：TC-Folder-Integration.exe [--data-dir 目录]");
            }
            Payload.Ensure(directory);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ManagerForm(directory));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TC Folder Integration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
