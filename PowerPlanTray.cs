// PowerPlanTray —— Windows 系统托盘电源计划快速切换工具
// 右键点击托盘图标弹出快速菜单；双击图标直达 Windows 电源选项。
// 直接调用 powrprof.dll（PowerEnumerate / PowerGetActiveScheme / PowerSetActiveScheme），
// 不依赖 powercfg.exe，不会闪黑窗。
// 编译：双击运行 build.cmd（使用 Windows 自带的 .NET Framework csc.exe）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PowerPlanTray")]
[assembly: AssemblyDescription("系统托盘电源计划快速切换工具")]
[assembly: AssemblyProduct("PowerPlanTray")]
[assembly: AssemblyVersion("1.8.7.0")]
[assembly: AssemblyFileVersion("1.8.7.0")]

namespace PowerPlanTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 隐藏参数：导出三档图标 PNG（256px，README 素材/预览用）后退出
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && string.Equals(args[1], "/export-icons", StringComparison.OrdinalIgnoreCase))
            {
                TrayContext.ExportIcons(Application.StartupPath);
                return;
            }

            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, @"Local\PowerPlanTray.SingleInstance", out createdNew))
            {
                if (!createdNew) return; // 已有实例在运行
                Application.EnableVisualStyles();
                try
                {
                    Application.Run(new TrayContext());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("PowerPlanTray 发生错误，即将退出。\n\n" + ex.GetType().Name + ": " + ex.Message,
                        "PowerPlanTray", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class PlanInfo
    {
        public Guid Guid;
        public string Name;
        public bool IsActive;
    }

    // 托盘菜单根据系统实际活动的电源计划绘制选中状态。

    // 可选配置文件 PowerPlanTray.ini（与本 exe 同目录），不存在时用内置默认值
    internal sealed class ToolConfig
    {
        public string[] WeakWords = new string[] { "电影", "办公", "节能", "省电", "低热", "低耗", "低功耗" };   // 绿色/最弱档
        public string[] MidWords = new string[] { "AI", "平衡" };                                              // 蓝色/中间档
        public string[] StrongWords = new string[] { "游戏", "卓越", "高性能", "极致" };                        // 红色/最强档
        public int RefreshMs = 3000;

        public static ToolConfig Load()
        {
            ToolConfig cfg = new ToolConfig();
            try
            {
                // 固定配置文件名：发布 exe 可能带版本号，配置不随 exe 名变化
                string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PowerPlanTray.ini");
                if (!File.Exists(path)) return cfg;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    string[] words = val.Split(new char[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < words.Length; i++) words[i] = words[i].Trim();

                    if (key == "弱档关键词" && words.Length > 0) cfg.WeakWords = words;
                    else if (key == "中档关键词" && words.Length > 0) cfg.MidWords = words;
                    else if (key == "强档关键词" && words.Length > 0) cfg.StrongWords = words;
                    else if (key == "刷新间隔毫秒")
                    {
                        int ms;
                        if (int.TryParse(val, out ms) && ms >= 1000) cfg.RefreshMs = ms;
                    }
                }
            }
            catch { } // 配置文件损坏时静默回退默认值
            return cfg;
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "PowerPlanTray";
        private const string AppKeyPath = @"Software\PowerPlanTray";
        private const string WelcomeValueName = "WelcomeShown";
        private const uint AccessScheme = 16; // powrprof.h: ACCESS_SCHEME
        private const uint ERROR_NO_MORE_ITEMS = 259; // PowerEnumerate 正常枚举结束

        private readonly OwnerForm ownerForm;
        private readonly IntPtr ownerHandle;
        private readonly NotifyIcon tray;
        private readonly Timer refreshTimer;
        private TrayPopupForm popupForm;
        private Icon currentIcon;
        private readonly ToolConfig cfg = ToolConfig.Load();
        private string lastSignature = "";
        private bool menuOpen;
        private uint lastPlanError;      // 最近一次 GetPlans 遇到的真实 API 错误码（0 = 无）
        private uint reportedPlanError;  // 已提示过的错误码，避免重复弹窗

        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, byte[] Buffer, ref uint BufferSize);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettings, IntPtr PowerSettingGuid, byte[] Buffer, ref uint BufferSize);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point p);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public TrayContext()
        {
            // 隐藏窗体：接收 TaskbarCreated 广播，资源管理器重启后自动补回托盘图标。
            ownerForm = new OwnerForm(OnTaskbarCreated);
            ownerHandle = ownerForm.Handle;

            List<PlanInfo> plans = GetPlans();
            lastSignature = Signature(plans);
            // 不使用系统默认 ContextMenuStrip；使用自绘悬浮菜单。
            tray = new NotifyIcon
            {
                Text = GetTooltipText(plans),
                Visible = true
            };
            ApplyIconForActive(plans);
            tray.MouseClick += OnTrayClick;
            tray.MouseDoubleClick += OnTrayDoubleClick;
            ReportPlanError();

            // 定期检查电源计划是否有外部变化（比如在控制面板里改了）
            refreshTimer = new Timer { Interval = cfg.RefreshMs };
            refreshTimer.Tick += delegate { RefreshIfChanged(); };
            refreshTimer.Start();

            FixAutoRunPath();

            // 欢迎气球仅首次运行弹一次（注册表标记），之后启动保持安静
            if (ShouldShowWelcome())
            {
                tray.BalloonTipTitle = "PowerPlanTray 已在托盘运行";
                tray.BalloonTipText = "右键点击托盘图标打开快速菜单；双击打开 Windows 电源选项。";
                tray.ShowBalloonTip(3000);
            }
        }

        private void OnTaskbarCreated()
        {
            try { tray.Visible = false; tray.Visible = true; } catch { }
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return; // 仅右键唤出面板
            TogglePopupMenu();
        }

        // 只显示托盘悬浮菜单，不显示独立主窗口。
        private void TogglePopupMenu()
        {
            try
            {
                if (popupForm != null && !popupForm.IsDisposed && popupForm.Visible)
                {
                    HidePopup();
                    return;
                }

                List<PlanInfo> plans = GetPlans();
                if (popupForm == null || popupForm.IsDisposed)
                {
                    popupForm = new TrayPopupForm(this, plans, SafeIsAutoRun());
                    popupForm.Deactivate += delegate
                    {
                        if (popupForm == null || popupForm.IsDisposed || !popupForm.Visible) return;
                        // 点击任务栏/托盘引起的失焦不自动收起，交给托盘右键显式切换
                        if (CursorOverTaskbar()) return;
                        HidePopup();
                    };
                }
                else
                {
                    popupForm.RefreshData(plans, SafeIsAutoRun());
                }

                PositionPopup(popupForm);
                menuOpen = true;
                popupForm.Show(ownerForm);
                popupForm.Activate();
                SetForegroundWindow(popupForm.Handle);
            }
            catch (Exception ex)
            {
                tray.BalloonTipTitle = "无法打开快速菜单";
                tray.BalloonTipText = ex.Message;
                tray.ShowBalloonTip(2500);
            }
        }

        private static void PositionPopup(Form form)
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = Cursor.Position.X - form.Width + S(18);
            int y = Cursor.Position.Y - form.Height - S(8);
            if (y < area.Top) y = Cursor.Position.Y + S(8);
            if (x < area.Left) x = area.Left;
            if (x + form.Width > area.Right) x = area.Right - form.Width;
            if (y < area.Top) y = area.Top;
            if (y + form.Height > area.Bottom) y = area.Bottom - form.Height;
            form.Location = new Point(x, y);
        }

        private void HidePopup()
        {
            if (popupForm != null && !popupForm.IsDisposed) popupForm.Hide();
            menuOpen = false;
        }

        // 桌面工作区之外即任务栏，托盘溢出面板单独按窗口类名识别
        private static bool CursorOverTaskbar()
        {
            try
            {
                Point p = Cursor.Position;
                if (!Screen.FromPoint(p).WorkingArea.Contains(p)) return true;

                IntPtr h = WindowFromPoint(p);
                for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
                {
                    StringBuilder sb = new StringBuilder(128);
                    if (GetClassName(h, sb, sb.Capacity) > 0)
                    {
                        string cls = sb.ToString();
                        if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                            cls.IndexOf("Overflow", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    h = GetParent(h);
                }
                return false;
            }
            catch { return false; }
        }

        private void ClosePopupMenu()
        {
            menuOpen = false;
            TrayPopupForm old = popupForm;
            popupForm = null;
            if (old != null && !old.IsDisposed)
            {
                try { old.Hide(); old.Dispose(); } catch { }
            }
        }

        private static bool SafeIsAutoRun()
        {
            try { return IsAutoRun(); }
            catch { return false; }
        }

        // 双击直达 Windows 电源选项；先收起托盘悬浮菜单
        private void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ClosePopupMenu();
            OpenPowerOptions();
        }

        private static void OpenPowerOptions()
        {
            try { Process.Start("control.exe", "/name Microsoft.PowerOptions"); }
            catch { }
        }

        // 欢迎气球只弹一次：HKCU\Software\PowerPlanTray\WelcomeShown 存在即不再弹；删该值可恢复
        private static bool ShouldShowWelcome()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppKeyPath))
                {
                    if (key == null || key.GetValue(WelcomeValueName) != null) return false;
                    key.SetValue(WelcomeValueName, 1, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { return false; }
        }

        private void ActivatePlan(Guid guid)
        {
            Guid g = guid;
            uint err = PowerSetActiveScheme(IntPtr.Zero, ref g);
            if (err != 0)
            {
                tray.BalloonTipTitle = "切换失败";
                tray.BalloonTipText = "PowerSetActiveScheme 返回错误码 " + err;
                tray.ShowBalloonTip(2500);
                return;
            }
            List<PlanInfo> plans = GetPlans();
            ReportPlanError();
            lastSignature = Signature(plans);
            ApplyIconForActive(plans);
            tray.Text = GetTooltipText(plans);
            if (popupForm != null && !popupForm.IsDisposed)
                popupForm.RefreshData(plans, SafeIsAutoRun());
        }

        private void ForceRefresh()
        {
            try
            {
                List<PlanInfo> plans = GetPlans();
                ReportPlanError();
                lastSignature = Signature(plans);
                ApplyIconForActive(plans);
                tray.Text = GetTooltipText(plans);
                if (popupForm != null && !popupForm.IsDisposed)
                    popupForm.RefreshData(plans, SafeIsAutoRun());
            }
            catch (Exception ex)
            {
                tray.BalloonTipTitle = "刷新失败";
                tray.BalloonTipText = ex.Message;
                tray.ShowBalloonTip(2000);
            }
        }

        private void RefreshIfChanged()
        {
            try
            {
                if (menuOpen) return;
                List<PlanInfo> plans = GetPlans();
                ReportPlanError();
                string sig = Signature(plans);
                if (sig == lastSignature) return;
                lastSignature = sig;
                ApplyIconForActive(plans);
                tray.Text = GetTooltipText(plans);
                if (popupForm != null && !popupForm.IsDisposed)
                    popupForm.RefreshData(plans, SafeIsAutoRun());
            }
            catch { }
        }

        // 电源 API 真正报错时给出一次适度提示（同一错误码只弹一次）
        private void ReportPlanError()
        {
            if (lastPlanError == 0 || lastPlanError == reportedPlanError) return;
            reportedPlanError = lastPlanError;
            try
            {
                tray.BalloonTipTitle = "读取电源计划出错";
                tray.BalloonTipText = "PowerEnumerate 返回错误码 " + lastPlanError + "，计划列表可能不完整。";
                tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        private void ExitApp()
        {
            try { refreshTimer.Stop(); } catch { }
            try { ClosePopupMenu(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            if (currentIcon != null) { currentIcon.Dispose(); currentIcon = null; }
            try { ownerForm.Close(); } catch { }
            Application.Exit();
        }

        private static string Signature(List<PlanInfo> plans)
        {
            var sb = new StringBuilder();
            foreach (PlanInfo p in plans)
                sb.Append(p.Guid.ToString("D")).Append('|').Append(p.IsActive ? '1' : '0').Append('|').Append(p.Name).Append('\n');
            return sb.ToString();
        }

        private static string GetTooltipText(List<PlanInfo> plans)
        {
            PlanInfo active = null;
            foreach (PlanInfo p in plans) { if (p.IsActive) { active = p; break; } }
            string text = "电源计划：" + (active != null ? active.Name : "未知");
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        // ---- 电源计划 API ----

        private List<PlanInfo> GetPlans()
        {
            lastPlanError = 0;
            var plans = new List<PlanInfo>();
            for (uint index = 0; ; index++)
            {
                uint size = 16;
                byte[] buf = new byte[16];
                uint err = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buf, ref size);
                if (err == ERROR_NO_MORE_ITEMS) break; // 正常枚举结束
                if (err != 0)
                {
                    // 真正的 API 错误：记录，不假装列表已完整
                    lastPlanError = err;
                    break;
                }
                if (size < 16) break; // 返回数据不足以容纳一个 GUID
                var guid = new Guid(buf);
                plans.Add(new PlanInfo { Guid = guid, Name = GetFriendlyName(guid) });
            }

            IntPtr activePtr;
            if (PowerGetActiveScheme(IntPtr.Zero, out activePtr) == 0 && activePtr != IntPtr.Zero)
            {
                try
                {
                    Guid active = (Guid)Marshal.PtrToStructure(activePtr, typeof(Guid));
                    foreach (PlanInfo p in plans) p.IsActive = (p.Guid == active);
                }
                finally { LocalFree(activePtr); } // 按 API 契约用 LocalFree 释放，异常路径同样安全
            }

            // 排序：性能从弱到强 —— 弱档 → 中间档 → 强档（关键词组可在 ini 里配置）；同级按名称
            plans.Sort((a, b) =>
            {
                int r = PlanRank(a.Name).CompareTo(PlanRank(b.Name));
                return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return plans;
        }

        private int PlanRank(string name)
        {
            if (ContainsAny(name, cfg.StrongWords)) return 2;
            if (ContainsAny(name, cfg.MidWords)) return 1;
            if (ContainsAny(name, cfg.WeakWords)) return 0;
            return 1; // 未识别的计划按中间档处理
        }

        private static bool ContainsAny(string name, string[] words)
        {
            foreach (string w in words)
            {
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string GetFriendlyName(Guid schemeGuid)
        {
            uint size = 0;
            Guid scheme = schemeGuid;
            uint err = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (err != 0 || size == 0) return schemeGuid.ToString("B").ToUpperInvariant();

            byte[] buf = new byte[size];
            Guid scheme2 = schemeGuid;
            err = PowerReadFriendlyName(IntPtr.Zero, ref scheme2, IntPtr.Zero, IntPtr.Zero, buf, ref size);
            if (err != 0 || size < 2) return schemeGuid.ToString("B").ToUpperInvariant();

            string name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return string.IsNullOrEmpty(name) ? schemeGuid.ToString("B").ToUpperInvariant() : name;
        }

        // ---- 开机自启动（HKCU Run 键，不需要管理员权限） ----

        private static bool IsAutoRun()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        // 返回是否写入成功；失败由调用方提示，不影响程序继续运行
        private static bool SetAutoRun(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch { return false; }
        }

        // exe 被移动后自动修正自启动注册表里的路径；失败仅局部处理
        private static void FixAutoRunPath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    string value = key.GetValue(RunValueName) as string;
                    string expected = "\"" + Application.ExecutablePath + "\"";
                    if (value != null && value != expected) key.SetValue(RunValueName, expected);
                }
            }
            catch { }
        }

        // ---- 图标：渐变圆角方块 + 白色电池（内含闪电），颜色随档位变化 ----

        // 档位语义：绿=弱档（电影/安静），蓝=中档（AI/日常），红=强档（游戏/高性能）
        private void ApplyIconForActive(List<PlanInfo> plans)
        {
            PlanInfo active = null;
            foreach (PlanInfo p in plans) { if (p.IsActive) { active = p; break; } }
            int rank = active != null ? PlanRank(active.Name) : 1;

            Icon newIcon = CreateIcon(rank);
            Icon old = currentIcon;
            tray.Icon = newIcon;
            currentIcon = newIcon;
            if (old != null) old.Dispose();
        }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        // 各档位主题色（iOS 系统色系）：图标与菜单色点共用
        private static void ThemePalette(int rank, out Color top, out Color mid, out Color bottom, out Color inner)
        {
            if (rank == 0)      { top = C(0x6E, 0xE9, 0x97); mid = C(0x34, 0xC7, 0x59); bottom = C(0x1F, 0xA8, 0x45); inner = C(0x1B, 0x9A, 0x3E); }
            else if (rank == 2) { top = C(0xFF, 0x8A, 0x78); mid = C(0xFF, 0x3B, 0x30); bottom = C(0xDE, 0x2C, 0x22); inner = C(0xC8, 0x28, 0x1F); }
            else                { top = C(0x64, 0xB5, 0xFF); mid = C(0x0A, 0x84, 0xFF); bottom = C(0x00, 0x63, 0xD6); inner = C(0x00, 0x59, 0xC2); }
        }

        private static Icon CreateIcon(int rank)
        {
            // 按“托盘真实显示尺寸”出图：超采样绘制后高质量缩到目标尺寸，系统零缩放显示。
            int target = (int)Math.Round(16f * DeviceDpi / 96f);
            if (target < 16) target = 16;
            if (target > 64) target = 64;

            using (Bitmap bmp = RenderIconBitmap(rank, target))
            {
                IntPtr hIcon = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
                finally { DestroyIcon(hIcon); }
            }
        }

        // 2x（小尺寸 4x）超采样画布绘制（画法使用 64 逻辑坐标），再双三次缩到 size
        internal static Bitmap RenderIconBitmap(int rank, int size)
        {
            bool small = size <= 28; // 托盘 16~20px：细节会被插值抹平，改走简化画法
            int ss = Math.Max(2, size * (small ? 4 : 2));
            using (var design = new Bitmap(ss, ss))
            using (Graphics gd = Graphics.FromImage(design))
            {
                gd.SmoothingMode = SmoothingMode.AntiAlias;
                gd.ScaleTransform(ss / 64f, ss / 64f);
                DrawIconArtwork(gd, rank, small);

                var bmp = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(design, new Rectangle(0, 0, size, size), new Rectangle(0, 0, ss, ss), GraphicsUnit.Pixel);
                }
                return bmp;
            }
        }

        private static void DrawIconArtwork(Graphics gd, int rank, bool small)
        {
            Color top, mid, bottom, inner;
            ThemePalette(rank, out top, out mid, out bottom, out inner);

            using (GraphicsPath tile = Squircle(new RectangleF(0.5f, 0.5f, 63f, 63f)))
            {
                using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), top, bottom, 90f))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { top, mid, bottom };
                    blend.Positions = new[] { 0f, 0.55f, 1f };
                    gradient.InterpolationColors = blend;
                    gd.FillPath(gradient, tile);
                }
                // 顶部一层极淡的受光；小尺寸上它只会让电池边缘发灰，故省略
                if (!small)
                {
                    gd.SetClip(tile);
                    using (var sheen = new LinearGradientBrush(new RectangleF(0f, 0f, 64f, 30f),
                        Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        gd.FillRectangle(sheen, new RectangleF(0f, 0f, 64f, 30f));
                    gd.ResetClip();
                }
            }

            // 实心白电池 + 档位色闪电：小尺寸时主体放大、闪电加粗，否则缩到 20px 直接糊掉
            RectangleF bodyRect = small ? new RectangleF(11f, 21.5f, 36f, 21f) : new RectangleF(13f, 22.5f, 32f, 19f);
            RectangleF capRect = small ? new RectangleF(49.5f, 27f, 6f, 11f) : new RectangleF(47.5f, 27.5f, 5f, 9f);
            using (var white = new SolidBrush(Color.White))
            {
                using (GraphicsPath body = RoundedRect(bodyRect, small ? 5.5f : 5f))
                    gd.FillPath(white, body);
                using (GraphicsPath cap = RoundedRect(capRect, small ? 2.4f : 2.2f))
                    gd.FillPath(white, cap);
            }
            using (GraphicsPath bolt = BoltPath(29f, 32f, small ? 1.15f : 1f))
            {
                using (var fill = new SolidBrush(inner))
                    gd.FillPath(fill, bolt);
                using (var rounder = new Pen(inner, small ? 3.4f : 2f) { LineJoin = LineJoin.Round })
                    gd.DrawPath(rounder, bolt);
            }
        }

        // 六段式闪电（圆角拐点），中心 + 缩放可调
        private static GraphicsPath BoltPath(float cx, float cy, float s)
        {
            PointF[] pts =
            {
                new PointF(cx + 2.5f * s, cy - 6f * s),
                new PointF(cx - 4.5f * s, cy + 1.5f * s),
                new PointF(cx - 0.5f * s, cy + 1.5f * s),
                new PointF(cx - 2.5f * s, cy + 6f * s),
                new PointF(cx + 4.5f * s, cy - 2f * s),
                new PointF(cx + 0.5f * s, cy - 2f * s)
            };
            var path = new GraphicsPath();
            path.AddPolygon(pts);
            return path;
        }

        // /export-icons：导出三档图标 PNG（256px）到 assets/，供 README 配图
        internal static void ExportIcons(string dir)
        {
            string[] names = { "green", "blue", "red" };
            string outDir = Path.Combine(dir, "assets");
            Directory.CreateDirectory(outDir);
            for (int rank = 0; rank < 3; rank++)
            {
                string file = Path.Combine(outDir, "icon-" + names[rank] + ".png");
                using (Bitmap bmp = RenderIconBitmap(rank, 256))
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // iOS 风格连续圆角：超椭圆 |x/a|^n + |y/b|^n = 1（n≈5），采样成多边形
        private static GraphicsPath Squircle(RectangleF r)
        {
            var path = new GraphicsPath();
            const int steps = 192;
            const double n = 5.0;
            double p = 2.0 / n;
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            float a = r.Width / 2f;
            float b = r.Height / 2f;
            PointF[] pts = new PointF[steps];
            for (int i = 0; i < steps; i++)
            {
                double t = 2.0 * Math.PI * i / steps;
                double ct = Math.Cos(t);
                double st = Math.Sin(t);
                pts[i] = new PointF(
                    (float)(cx + a * Math.Sign(ct) * Math.Pow(Math.Abs(ct), p)),
                    (float)(cy + b * Math.Sign(st) * Math.Pow(Math.Abs(st), p)));
            }
            path.AddPolygon(pts);
            return path;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // ---- 托盘弹出菜单的圆角与 DPI 度量 ----

        // 度量基准：设计稿在这台 120 DPI（125%）机器上定稿，故以 120 为锚点等比缩放。
        // 120 DPI 时 S() 恒等（与已验收稿逐像素一致），100%/150% 机器上留白与色点跟着字号一起缩。
        private static readonly float DeviceDpi = GetDeviceDpi();

        private static float GetDeviceDpi()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX;
        }

        private static int S(int designPx)
        {
            return (int)Math.Round(designPx * DeviceDpi / 120f);
        }

        private static float S(float designPx)
        {
            return designPx * DeviceDpi / 120f;
        }

        // ---- Apple-inspired 托盘悬浮菜单：仅作为快速菜单，不提供独立主窗口 ----
        private sealed class TrayPopupForm : Form
        {
            private readonly TrayContext owner;
            private List<PlanInfo> plans;
            private bool autoRun;
            private const int DesignWidth = 344;
            private static readonly Color Bg = Color.FromArgb(239, 241, 246);
            private static readonly Color Card = Color.White;
            private static readonly Color TextMain = Color.FromArgb(31, 41, 55);
            private static readonly Color TextSub = Color.FromArgb(112, 124, 142);
            private static readonly Color Line = Color.FromArgb(226, 230, 237);
            private static readonly Font FontTitle = new Font("Microsoft YaHei UI", 10.2f, FontStyle.Bold);
            private static readonly Font FontLabel = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
            private static readonly Font FontSub = new Font("Microsoft YaHei UI", 8.1f, FontStyle.Regular);
            private static readonly Font FontGlyph = new Font("Segoe MDL2 Assets", 12.0f, FontStyle.Regular);
            private static readonly Font FontHeaderGlyph = new Font("Segoe MDL2 Assets", 17.0f, FontStyle.Regular);

            public TrayPopupForm(TrayContext owner, List<PlanInfo> plans, bool autoRun)
            {
                this.owner = owner;
                this.plans = plans ?? new List<PlanInfo>();
                this.autoRun = autoRun;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = Bg;
                Padding = Padding.Empty;
                DoubleBuffered = true;
                BuildMainView();
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                    cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            public void RefreshData(List<PlanInfo> newPlans, bool currentAutoRun)
            {
                plans = newPlans ?? new List<PlanInfo>();
                autoRun = currentAutoRun;
                BuildMainView();
            }

            private void BuildMainView()
            {
                ClearControls();
                int w = TrayContext.S(DesignWidth), x = TrayContext.S(12), innerW = TrayContext.S(320);
                int y = TrayContext.S(12);
                PopupHeader header = new PopupHeader(GetActiveModeText(), GetActiveRank());
                header.SetBounds(x, y, innerW, TrayContext.S(60));
                Controls.Add(header);
                y += TrayContext.S(68);

                int rowH = TrayContext.S(56);
                int listH = Math.Max(rowH, plans.Count * rowH);
                Panel group = new Panel { BackColor = Card, Bounds = new Rectangle(x, y, innerW, listH) };
                group.Region = new Region(TrayContext.RoundedRect(new RectangleF(0, 0, group.Width, group.Height), TrayContext.S(13)));
                Controls.Add(group);
                for (int i = 0; i < plans.Count; i++)
                {
                    PlanInfo plan = plans[i];
                    PopupModeRow row = new PopupModeRow(owner, plan, owner.PlanRank(plan.Name), plan.IsActive);
                    row.SetBounds(TrayContext.S(4), i * rowH, innerW - TrayContext.S(8), rowH);
                    row.Click += delegate
                    {
                        Guid guid = plan.Guid;
                        owner.ClosePopupMenu();
                        owner.ActivatePlan(guid);
                    };
                    group.Controls.Add(row);
                }
                y += listH + TrayContext.S(8);

                AutoStartRow autoRow = new AutoStartRow(autoRun);
                autoRow.SetBounds(x, y, innerW, TrayContext.S(36));
                autoRow.Click += delegate
                {
                    bool want = !TrayContext.IsAutoRun();
                    bool ok = TrayContext.SetAutoRun(want);
                    autoRun = TrayContext.SafeIsAutoRun();
                    if (!ok || autoRun != want)
                    {
                        try
                        {
                            owner.tray.BalloonTipTitle = "设置失败";
                            owner.tray.BalloonTipText = want
                                ? "无法启用开机自动启动（注册表写入失败）。"
                                : "无法取消开机自动启动（注册表写入失败）。";
                            owner.tray.ShowBalloonTip(2000);
                        }
                        catch { }
                    }
                    BuildMainView();
                };
                Controls.Add(autoRow);
                y += TrayContext.S(36);

                AddActionRow(x, ref y, innerW, "刷新电源计划列表", RowGlyph.Refresh, delegate { owner.ForceRefresh(); });

                AddDivider(x + TrayContext.S(12), y + TrayContext.S(2), innerW - TrayContext.S(24));
                y += TrayContext.S(9);
                AddActionRow(x, ref y, innerW, "电源计划详情", RowGlyph.Chart, delegate
                {
                    owner.ClosePopupMenu();
                    TrayContext.OpenPowerOptions();
                });
                AddActionRow(x, ref y, innerW, "退出", RowGlyph.Power, delegate { owner.ExitApp(); });
                y += TrayContext.S(8);
                ClientSize = new Size(w, y);
                ApplyPopupRegion();
            }

            private string GetActiveModeText()
            {
                foreach (PlanInfo p in plans)
                    if (p.IsActive) return "当前：" + p.Name;
                return "当前：未知模式";
            }

            private int GetActiveRank()
            {
                foreach (PlanInfo p in plans)
                    if (p.IsActive) return owner.PlanRank(p.Name);
                return 1;
            }

            private enum RowGlyph { Power, Refresh, Chart }

            // 三个动作行共用同一 16×16 设计网格与线宽，保证图标视觉尺寸一致
            private static void DrawRowGlyph(Graphics g, Rectangle slot, RowGlyph kind, Color color)
            {
                float left = slot.X + (slot.Width - TrayContext.S(16f)) / 2f;
                float top = slot.Y + (slot.Height - TrayContext.S(16f)) / 2f;
                GraphicsState state = g.Save();
                g.TranslateTransform(left, top);
                using (Pen pen = new Pen(color, TrayContext.S(1.4f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    if (kind == RowGlyph.Power)
                    {
                        g.DrawLine(pen, TrayContext.S(8f), TrayContext.S(2f), TrayContext.S(8f), TrayContext.S(6.4f));
                        g.DrawArc(pen, TrayContext.S(3.4f), TrayContext.S(4.1f), TrayContext.S(9.2f), TrayContext.S(9.2f), 300f, 300f);
                    }
                    else if (kind == RowGlyph.Refresh)
                    {
                        g.DrawArc(pen, TrayContext.S(2.9f), TrayContext.S(2.9f), TrayContext.S(10.2f), TrayContext.S(10.2f), 330f, 300f);
                        using (SolidBrush brush = new SolidBrush(color))
                            g.FillPolygon(brush, new PointF[] {
                                new PointF(TrayContext.S(7.9f), TrayContext.S(1.5f)),
                                new PointF(TrayContext.S(7.9f), TrayContext.S(4.3f)),
                                new PointF(TrayContext.S(11.1f), TrayContext.S(2.9f))
                            });
                    }
                    else
                    {
                        using (GraphicsPath box = TrayContext.RoundedRect(new RectangleF(TrayContext.S(2.9f), TrayContext.S(2.9f), TrayContext.S(10.2f), TrayContext.S(10.2f)), TrayContext.S(2.2f)))
                            g.DrawPath(pen, box);
                        g.DrawLines(pen, new PointF[] {
                            new PointF(TrayContext.S(5.3f), TrayContext.S(9.7f)),
                            new PointF(TrayContext.S(7.1f), TrayContext.S(6.7f)),
                            new PointF(TrayContext.S(9f), TrayContext.S(8.4f)),
                            new PointF(TrayContext.S(11f), TrayContext.S(5.3f))
                        });
                    }
                }
                g.Restore(state);
            }

            private void AddActionRow(int x, ref int y, int width, string text, RowGlyph glyph, Action action)
            {
                PopupActionRow row = new PopupActionRow(text, glyph);
                row.SetBounds(x, y, width, TrayContext.S(36));
                row.Click += delegate { action(); };
                Controls.Add(row);
                y += TrayContext.S(36);
            }

            private void AddDivider(int x, int y, int width)
            {
                Panel divider = new Panel { BackColor = Line, Bounds = new Rectangle(x, y, width, Math.Max(1, TrayContext.S(1))) };
                Controls.Add(divider);
            }

            private void ClearControls()
            {
                for (int i = Controls.Count - 1; i >= 0; i--)
                {
                    Control c = Controls[i];
                    Controls.RemoveAt(i);
                    c.Dispose();
                }
            }

            private void ApplyPopupRegion()
            {
                Region old = Region;
                using (GraphicsPath path = TrayContext.RoundedRect(new RectangleF(0, 0, Width, Height), TrayContext.S(13)))
                    Region = new Region(path);
                if (old != null) old.Dispose();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(220, 224, 232)))
                using (GraphicsPath path = TrayContext.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), TrayContext.S(13)))
                    e.Graphics.DrawPath(pen, path);
            }

            private static Color Accent(int rank)
            {
                return rank == 0 ? Color.FromArgb(52, 199, 89) : rank == 2 ? Color.FromArgb(255, 59, 48) : Color.FromArgb(0, 122, 255);
            }

            private static Color Tint(int rank)
            {
                return rank == 0 ? Color.FromArgb(222, 247, 229) : rank == 2 ? Color.FromArgb(255, 226, 225) : Color.FromArgb(224, 237, 255);
            }

            private sealed class PopupHeader : Control
            {
                private readonly string subtitle;
                private readonly int rank;
                public PopupHeader(string subtitle, int rank)
                {
                    this.subtitle = subtitle; this.rank = rank;
                    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                    Font = FontLabel; BackColor = Color.Transparent;
                }
                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = TrayContext.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), TrayContext.S(13)))
                    using (SolidBrush b = new SolidBrush(Card)) e.Graphics.FillPath(b, p);
                    int d = TrayContext.S(36), left = TrayContext.S(12), top = (Height - d) / 2;
                    Rectangle circle = new Rectangle(left, top, d, d);
                    using (SolidBrush b = new SolidBrush(Tint(rank))) e.Graphics.FillEllipse(b, circle);
                    TextRenderer.DrawText(e.Graphics, "\uE945", FontHeaderGlyph, circle, Accent(rank), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    int textX = left + d + TrayContext.S(10);
                    TextRenderer.DrawText(e.Graphics, "PowerPlanTray", FontTitle, new Rectangle(textX, TrayContext.S(10), Width - textX - TrayContext.S(8), TrayContext.S(22)), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, subtitle, FontSub, new Rectangle(textX, TrayContext.S(32), Width - textX - TrayContext.S(8), TrayContext.S(18)), TextSub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            private sealed class PopupModeRow : Control
            {
                private readonly TrayContext owner;
                private readonly PlanInfo plan;
                private readonly int rank;
                private readonly bool active;
                private bool hovered;
                public PopupModeRow(TrayContext owner, PlanInfo plan, int rank, bool active)
                {
                    this.owner = owner; this.plan = plan; this.rank = rank; this.active = active;
                    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                    BackColor = Color.Transparent; Cursor = Cursors.Hand; Font = FontLabel;
                    AccessibleName = plan.Name;
                }
                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    RectangleF hit = new RectangleF(TrayContext.S(2), TrayContext.S(2), Width - TrayContext.S(4), Height - TrayContext.S(4));
                    if (active || hovered)
                    {
                        Color fill = active ? Color.FromArgb(241, 247, 255) : Color.FromArgb(247, 248, 251);
                        using (GraphicsPath p = TrayContext.RoundedRect(hit, TrayContext.S(9))) using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillPath(b, p);
                        if (active) using (GraphicsPath p = TrayContext.RoundedRect(hit, TrayContext.S(9))) using (Pen pen = new Pen(Color.FromArgb(202, 222, 255), 1f)) e.Graphics.DrawPath(pen, p);
                    }
                    int d = TrayContext.S(34), x = TrayContext.S(8), y = (Height - d) / 2;
                    Rectangle icon = new Rectangle(x, y, d, d);
                    using (SolidBrush b = new SolidBrush(Tint(rank))) e.Graphics.FillEllipse(b, icon);
                    DrawModeGlyph(e.Graphics, icon, rank);
                    int tx = x + d + TrayContext.S(12);
                    TextRenderer.DrawText(e.Graphics, plan.Name, FontLabel, new Rectangle(tx, 0, Width - tx - TrayContext.S(40), Height), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    int markD = TrayContext.S(17), mx = Width - TrayContext.S(27), my = (Height - markD) / 2;
                    Rectangle mark = new Rectangle(mx, my, markD, markD);
                    if (active)
                    {
                        using (SolidBrush b = new SolidBrush(Accent(1))) e.Graphics.FillEllipse(b, mark);
                        using (Pen pen = new Pen(Color.White, TrayContext.S(1.7f)))
                        {
                            e.Graphics.DrawLine(pen, mx + TrayContext.S(4), my + TrayContext.S(9), mx + TrayContext.S(7), my + TrayContext.S(12));
                            e.Graphics.DrawLine(pen, mx + TrayContext.S(7), my + TrayContext.S(12), mx + TrayContext.S(13), my + TrayContext.S(5));
                        }
                    }
                    else using (Pen pen = new Pen(Color.FromArgb(181, 192, 208), TrayContext.S(1.2f))) e.Graphics.DrawEllipse(pen, mark);
                }
                private void DrawModeGlyph(Graphics g, Rectangle r, int rank)
                {
                    // 三档统一使用闪电图标，仅颜色随档位变化
                    Color c = Accent(rank);
                    Point[] bolt = new Point[] {
                        new Point(r.X + TrayContext.S(19), r.Y + TrayContext.S(6)),
                        new Point(r.X + TrayContext.S(11), r.Y + TrayContext.S(18)),
                        new Point(r.X + TrayContext.S(17), r.Y + TrayContext.S(18)),
                        new Point(r.X + TrayContext.S(14), r.Y + TrayContext.S(28)),
                        new Point(r.X + TrayContext.S(24), r.Y + TrayContext.S(14)),
                        new Point(r.X + TrayContext.S(18), r.Y + TrayContext.S(14))
                    };
                    using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, bolt);
                }
                protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
                protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
            }

            private sealed class PopupActionRow : Control
            {
                private readonly RowGlyph glyph;
                private bool hovered;
                public PopupActionRow(string text, RowGlyph glyph)
                {
                    Text = text; this.glyph = glyph; Font = FontLabel;
                    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                    BackColor = Color.Transparent; Cursor = Cursors.Hand;
                }
                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (hovered)
                    {
                        using (GraphicsPath p = TrayContext.RoundedRect(new RectangleF(TrayContext.S(2), TrayContext.S(1), Width - TrayContext.S(4), Height - TrayContext.S(2)), TrayContext.S(8)))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 234, 241))) e.Graphics.FillPath(b, p);
                    }
                    DrawRowGlyph(e.Graphics, new Rectangle(TrayContext.S(12), 0, TrayContext.S(22), Height), glyph, Color.FromArgb(63, 79, 101));
                    TextRenderer.DrawText(e.Graphics, Text, FontLabel, new Rectangle(TrayContext.S(43), 0, Width - TrayContext.S(68), Height), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, "\uE76C", FontGlyph, new Rectangle(Width - TrayContext.S(28), 0, TrayContext.S(16), Height), TextSub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
                protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
            }

            private sealed class AutoStartRow : Control
            {
                private readonly bool enabled;
                private bool hovered;

                public AutoStartRow(bool enabled)
                {
                    this.enabled = enabled;
                    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                    BackColor = Color.Transparent; Cursor = Cursors.Hand; Font = FontLabel;
                    AccessibleName = "开机自动启动";
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (hovered)
                    {
                        using (GraphicsPath p = TrayContext.RoundedRect(new RectangleF(TrayContext.S(2), TrayContext.S(1), Width - TrayContext.S(4), Height - TrayContext.S(2)), TrayContext.S(8)))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(230, 234, 241))) e.Graphics.FillPath(b, p);
                    }
                    DrawRowGlyph(e.Graphics, new Rectangle(TrayContext.S(12), 0, TrayContext.S(22), Height), RowGlyph.Power, Color.FromArgb(63, 79, 101));
                    TextRenderer.DrawText(e.Graphics, "开机自动启动", FontLabel, new Rectangle(TrayContext.S(43), 0, Width - TrayContext.S(110), Height), TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                    Rectangle track = new Rectangle(Width - TrayContext.S(56), (Height - TrayContext.S(24)) / 2, TrayContext.S(42), TrayContext.S(24));
                    using (GraphicsPath p = TrayContext.RoundedRect(track, TrayContext.S(12)))
                    using (SolidBrush b = new SolidBrush(enabled ? Color.FromArgb(48, 209, 88) : Color.FromArgb(205, 210, 218))) e.Graphics.FillPath(b, p);
                    int d = TrayContext.S(18), cx = enabled ? track.Right - d - TrayContext.S(3) : track.X + TrayContext.S(3);
                    using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.FillEllipse(b, cx, track.Y + TrayContext.S(3), d, d);
                }

                protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
                protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
            }
        }

        // 只收消息、永不显示的窗体
        private sealed class OwnerForm : Form
        {
            private readonly Action onTaskbarCreated;
            private readonly uint taskbarCreatedMessage;

            public OwnerForm(Action onTaskbarCreated)
            {
                this.onTaskbarCreated = onTaskbarCreated;
                taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0D;
            }

            protected override void WndProc(ref Message m)
            {
                if (taskbarCreatedMessage != 0 && (uint)m.Msg == taskbarCreatedMessage && onTaskbarCreated != null)
                    onTaskbarCreated();
                base.WndProc(ref m);
            }
        }
    }
}
