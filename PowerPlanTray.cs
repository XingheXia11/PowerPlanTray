// PowerPlanTray —— Windows 系统托盘电源计划快速切换工具
// 左键点击托盘图标弹出菜单，点击电源计划名即可切换；双击图标直达控制面板电源选项。
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
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

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

    // 计划项的选中态用 Checked 表示，对勾由 ModernMenuRenderer 统一画在右侧（iOS 风格）

    // 可选配置文件 PowerPlanTray.ini（与本 exe 同目录），不存在时用内置默认值
    internal sealed class ToolConfig
    {
        public string[] WeakWords = new string[] { "电影", "办公", "节能", "省电", "低热", "低耗", "低功耗" };   // 蓝色/最弱档
        public string[] MidWords = new string[] { "AI", "平衡" };                                              // 绿色/中间档
        public string[] StrongWords = new string[] { "游戏", "卓越", "高性能", "极致" };                        // 红色/最强档
        public int RefreshMs = 3000;

        public static ToolConfig Load()
        {
            ToolConfig cfg = new ToolConfig();
            try
            {
                string path = Path.ChangeExtension(Application.ExecutablePath, ".ini");
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

        private readonly OwnerForm ownerForm;
        private readonly IntPtr ownerHandle;
        private readonly NotifyIcon tray;
        private readonly Timer refreshTimer;
        private ContextMenuStrip menu;
        private Icon currentIcon;
        private readonly ToolConfig cfg = ToolConfig.Load();
        private string lastSignature = "";
        private bool menuOpen;

        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, byte[] Buffer, ref uint BufferSize);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettings, IntPtr PowerSettingGuid, byte[] Buffer, ref uint BufferSize);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // 把冷内存页还给系统，让任务管理器里的占用长期保持在个位数 MB
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        private static void TrimMemory()
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public TrayContext()
        {
            // 隐藏窗体：用于接收 TaskbarCreated 广播（资源管理器重启后自动补回图标），
            // 也作为菜单的前台窗口，保证菜单点击别处时能正常收起。
            ownerForm = new OwnerForm(OnTaskbarCreated);
            ownerHandle = ownerForm.Handle;

            List<PlanInfo> plans = GetPlans();
            lastSignature = Signature(plans);
            menu = BuildMenu(plans);

            tray = new NotifyIcon
            {
                Text = GetTooltipText(plans),
                ContextMenuStrip = menu,
                Visible = true
            };
            ApplyIconForActive(plans);
            tray.MouseClick += OnTrayClick;
            tray.MouseDoubleClick += OnTrayDoubleClick;

            // 定期检查电源计划是否有外部变化（比如在控制面板里改了）
            refreshTimer = new Timer { Interval = cfg.RefreshMs };
            refreshTimer.Tick += delegate
            {
                RefreshIfChanged();
                TrimMemory(); // 周期性修剪，常驻内存保持在个位数 MB
            };
            refreshTimer.Start();

            FixAutoRunPath();

            // 欢迎气球仅首次运行弹一次（注册表标记），之后启动保持安静
            if (ShouldShowWelcome())
            {
                tray.BalloonTipTitle = "PowerPlanTray 已在托盘运行";
                tray.BalloonTipText = "左键点击托盘图标即可快速切换电源计划。";
                tray.ShowBalloonTip(3000);
            }
        }

        private void OnTaskbarCreated()
        {
            try { tray.Visible = false; tray.Visible = true; } catch { }
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            SetForegroundWindow(ownerHandle);
            menu.Show(ownerForm, Cursor.Position);
        }

        // 双击直达电源选项；先收起第一次单击弹出的菜单
        private void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (menu != null && menu.Visible) menu.Close();
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

        private ContextMenuStrip BuildMenu(List<PlanInfo> plans)
        {
            MenuPalette pal = GetMenuPalette();
            var strip = new ContextMenuStrip
            {
                Renderer = new ModernMenuRenderer(pal),
                Font = MenuFont,
                Padding = new Padding(12),
                DropShadowEnabled = true
            };

            // 列表卡：三档计划 + 退出，行间发丝分隔线
            foreach (PlanInfo p in plans)
            {
                Guid guid = p.Guid; // 闭包必须用循环内的局部副本
                var item = new ToolStripMenuItem(p.Name)
                {
                    Image = GetDotImage(ThemeAccent(PlanRank(p.Name))),
                    ImageScaling = ToolStripItemImageScaling.None,
                    ForeColor = pal.Text,
                    Padding = ItemPad,
                    Checked = p.IsActive // 对勾由渲染器画在右侧
                };
                item.Click += delegate { ActivatePlan(guid); };
                strip.Items.Add(item);
                strip.Items.Add(new InsetSeparator { AutoSize = false, Height = 6 });
            }

            var exitItem = new ToolStripMenuItem("退出")
            {
                Image = GetDotImage(QuitDot),
                ImageScaling = ToolStripItemImageScaling.None,
                ForeColor = pal.Text,
                Padding = ItemPad
            };
            exitItem.Click += delegate
            {
                refreshTimer.Stop();
                tray.Visible = false;
                tray.Dispose();
                if (currentIcon != null) currentIcon.Dispose();
                ownerForm.Close();
                Application.Exit();
            };
            strip.Items.Add(exitItem);

            strip.Items.Add(new CardGap { AutoSize = false, Height = 14 });

            // 设置卡：功能行
            var autoRun = new ToolStripMenuItem("开机自动启动")
            {
                Checked = IsAutoRun(), ForeColor = pal.Text, Padding = ItemPad,
                Image = TransparentDot, ImageScaling = ToolStripItemImageScaling.None
            };
            autoRun.Click += delegate
            {
                SetAutoRun(!IsAutoRun());
                autoRun.Checked = IsAutoRun();
            };
            strip.Items.Add(autoRun);
            strip.Items.Add(new InsetSeparator { AutoSize = false, Height = 6 });

            var refreshItem = new ToolStripMenuItem("刷新电源计划列表")
            {
                ForeColor = pal.Text, Padding = ItemPad,
                Image = TransparentDot, ImageScaling = ToolStripItemImageScaling.None
            };
            refreshItem.Click += delegate { lastSignature = ""; };
            strip.Items.Add(refreshItem);
            strip.Items.Add(new InsetSeparator { AutoSize = false, Height = 6 });

            var openPanel = new ToolStripMenuItem("打开控制面板电源选项")
            {
                ForeColor = pal.Text, Padding = ItemPad,
                Image = TransparentDot, ImageScaling = ToolStripItemImageScaling.None
            };
            openPanel.Click += delegate { OpenPowerOptions(); };
            strip.Items.Add(openPanel);

            strip.Opened += delegate
            {
                menuOpen = true;
                ApplyRoundedRegion(strip, 14); // 区域圆角：比 Dwm 默认 8px 更接近设计稿，投影跟随区域形状
            };
            strip.Closed += delegate
            {
                menuOpen = false;
                strip.Region = null;
                // 菜单关闭后重建（更新勾选状态），BeginInvoke 避免在自己的事件里销毁自己
                ownerForm.BeginInvoke(new Action(ForceRefresh));
            };
            return strip;
        }

        private void SwapMenu(ContextMenuStrip newMenu)
        {
            ContextMenuStrip old = menu;
            menu = newMenu;
            tray.ContextMenuStrip = newMenu;
            if (old != null) old.Dispose();
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
            lastSignature = Signature(plans);
            ApplyIconForActive(plans);
            tray.Text = GetTooltipText(plans);
            // 菜单勾选会在菜单 Closed -> ForceRefresh 里统一重建
        }

        private void ForceRefresh()
        {
            lastSignature = "";
            RefreshIfChanged();
        }

        private void RefreshIfChanged()
        {
            try
            {
                if (menuOpen) return;
                List<PlanInfo> plans = GetPlans();
                string sig = Signature(plans);
                if (sig == lastSignature) return;
                lastSignature = sig;
                SwapMenu(BuildMenu(plans));
                ApplyIconForActive(plans);
                tray.Text = GetTooltipText(plans);
            }
            catch { }
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
            var plans = new List<PlanInfo>();
            for (uint index = 0; ; index++)
            {
                uint size = 16;
                byte[] buf = new byte[16];
                uint err = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buf, ref size);
                if (err != 0) break; // ERROR_NO_MORE_ITEMS(259) 或其他错误即结束
                var guid = new Guid(buf);
                plans.Add(new PlanInfo { Guid = guid, Name = GetFriendlyName(guid) });
            }

            IntPtr activePtr;
            if (PowerGetActiveScheme(IntPtr.Zero, out activePtr) == 0 && activePtr != IntPtr.Zero)
            {
                Guid active = (Guid)Marshal.PtrToStructure(activePtr, typeof(Guid));
                Marshal.FreeHGlobal(activePtr);
                foreach (PlanInfo p in plans) p.IsActive = (p.Guid == active);
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
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }

        private static void SetAutoRun(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValueName, false);
            }
        }

        // exe 被移动后自动修正自启动注册表里的路径
        private static void FixAutoRunPath()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null) return;
                string value = key.GetValue(RunValueName) as string;
                string expected = "\"" + Application.ExecutablePath + "\"";
                if (value != null && value != expected) key.SetValue(RunValueName, expected);
            }
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

        private static Color ThemeAccent(int rank)
        {
            Color top, mid, bottom, inner;
            ThemePalette(rank, out top, out mid, out bottom, out inner);
            return mid;
        }

        private static Icon CreateIcon(int rank)
        {
            // 按“托盘真实显示尺寸”出图：超采样绘制后高质量缩到目标尺寸，系统零缩放显示。
            // 目标尺寸按真实 DPI 计算（16 基准 × 缩放系数），PerMonitorV2 清单生效后 FromHwnd 返回主屏真实 DPI。
            float dpi;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            int target = (int)Math.Round(16f * dpi / 96f);
            if (target < 16) target = 16;
            if (target > 64) target = 64;

            using (Bitmap bmp = RenderIconBitmap(rank, target))
            {
                IntPtr hIcon = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
                finally { DestroyIcon(hIcon); }
            }
        }

        // 2x 超采样画布绘制（画法使用 64 逻辑坐标），再双三次缩到 size
        internal static Bitmap RenderIconBitmap(int rank, int size)
        {
            int ss = Math.Max(2, size * 2);
            using (var design = new Bitmap(ss, ss))
            using (Graphics gd = Graphics.FromImage(design))
            {
                gd.SmoothingMode = SmoothingMode.AntiAlias;
                gd.ScaleTransform(ss / 64f, ss / 64f);
                DrawIconArtwork(gd, rank);

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

        private static void DrawIconArtwork(Graphics gd, int rank)
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
                // 顶部一层极淡的受光
                gd.SetClip(tile);
                using (var sheen = new LinearGradientBrush(new RectangleF(0f, 0f, 64f, 30f),
                    Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    gd.FillRectangle(sheen, new RectangleF(0f, 0f, 64f, 30f));
                gd.ResetClip();
            }

            // 实心白电池 + 档位色闪电
            using (var white = new SolidBrush(Color.White))
            {
                using (GraphicsPath body = RoundedRect(new RectangleF(13f, 22.5f, 32f, 19f), 5f))
                    gd.FillPath(white, body);
                using (GraphicsPath cap = RoundedRect(new RectangleF(47.5f, 27.5f, 5f, 9f), 2.2f))
                    gd.FillPath(white, cap);
            }
            using (GraphicsPath bolt = BoltPath(29f, 32f, 1f))
            {
                using (var fill = new SolidBrush(inner))
                    gd.FillPath(fill, bolt);
                using (var rounder = new Pen(inner, 2f) { LineJoin = LineJoin.Round })
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

        // ---- iOS 风格菜单：圆角窗口、亮/暗自适应、档位色点、右侧对勾、圆角悬停高亮 ----

        private static readonly Font MenuFont = new Font("Microsoft YaHei UI", 9.5F);
        private static readonly Font MenuCheckFont = new Font("Segoe MDL2 Assets", 10F);
        private static readonly Padding ItemPad = new Padding(4, 5, 0, 5); // 行高 26 -> 36
        private static readonly Color QuitDot = Color.FromArgb(0x8E, 0x8E, 0x93);
        private static readonly Dictionary<string, Bitmap> DotImageCache = new Dictionary<string, Bitmap>();
        private static readonly Bitmap TransparentDot = new Bitmap(18, 18); // 功能行文字对齐占位

        // 档位色点（缓存）；选中态由渲染器画右侧对勾，不在图标上体现
        private static Bitmap GetDotImage(Color accent)
        {
            string key = accent.ToArgb().ToString();
            Bitmap bmp;
            if (!DotImageCache.TryGetValue(key, out bmp))
            {
                bmp = new Bitmap(18, 18);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush brush = new SolidBrush(accent))
                        g.FillEllipse(brush, 3f, 3f, 12f, 12f); // 直径12，圆心(9,9)
                }
                DotImageCache[key] = bmp;
            }
            return bmp;
        }

        private sealed class MenuPalette
        {
            public Color Bg, Card, Text, SubText, Hover, Separator;
        }

        // 固定浅色外观（与设计稿一致）：浅灰底 + 白色分组卡片
        private static MenuPalette GetMenuPalette()
        {
            MenuPalette p = new MenuPalette();
            p.Bg = C(0xEC, 0xEC, 0xF1);
            p.Card = Color.White;
            p.Text = C(0x1C, 0x1C, 0x1E);
            p.SubText = C(0x6C, 0x6C, 0x70);
            p.Hover = Color.FromArgb(10, 0, 0, 0);
            p.Separator = C(0xE3, 0xE3, 0xE8);
            return p;
        }

        private static void ApplyRoundedRegion(ToolStripDropDown strip, int radius)
        {
            using (GraphicsPath path = RoundedRect(new RectangleF(0f, 0f, strip.Width, strip.Height), radius))
            {
                strip.Region = new Region(path);
            }
        }

        private sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly MenuPalette pal;

            public ModernMenuRenderer(MenuPalette palette)
            {
                pal = palette;
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(pal.Bg);

                // 在条目底下垫分组卡片（列表卡 / 设置卡）
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle? list = null, settings = null;
                bool afterExit = false;
                foreach (ToolStripItem item in e.ToolStrip.Items)
                {
                    if (!item.Visible) continue;
                    if (item is ToolStripMenuItem)
                    {
                        if (item.Text == "退出") { list = Union(list, item.Bounds); afterExit = true; }
                        else if (afterExit) settings = Union(settings, item.Bounds);
                        else list = Union(list, item.Bounds);
                    }
                }
                FillCard(e.Graphics, list);
                FillCard(e.Graphics, settings);
            }

            private static Rectangle? Union(Rectangle? acc, Rectangle b)
            {
                return acc.HasValue ? Rectangle.Union(acc.Value, b) : b;
            }

            private void FillCard(Graphics g, Rectangle? bounds)
            {
                if (!bounds.HasValue) return;
                Rectangle b = bounds.Value;
                using (GraphicsPath path = RoundedRect(new RectangleF(b.X, b.Top - 2f, b.Width, b.Height + 4f), 12f))
                using (var brush = new SolidBrush(pal.Card))
                    g.FillPath(brush, path);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

            // 图像全部由渲染器画：基类会给 Checked 项在图像区画淡蓝色选中底版（与设计不符）
            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                if (e.Image == null) return;
                Rectangle r = e.ImageRectangle;
                e.Graphics.DrawImage(e.Image,
                    r.X + (r.Width - e.Image.Width) / 2,
                    r.Y + (r.Height - e.Image.Height) / 2,
                    e.Image.Width, e.Image.Height);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // 雅黑行盒含 descent，CJK 字形视觉偏上，下移 4px 补偿使其在行格内 optical 居中
                Rectangle tr = e.TextRectangle;
                tr.Offset(0, 4);
                TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, tr, e.Item.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Enabled) return;

                if (e.Item.Selected)
                {
                    Rectangle rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                    using (GraphicsPath path = RoundedRect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 8f))
                    using (SolidBrush hover = new SolidBrush(pal.Hover))
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        e.Graphics.FillPath(hover, path);
                    }
                }

                // 选中对勾（Segoe MDL2 图标）画在右侧：当前计划 / 开机自动启动
                ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
                if (menuItem != null && menuItem.Checked)
                    TextRenderer.DrawText(e.Graphics, "\uE73E", MenuCheckFont,
                        new Rectangle(e.Item.Width - 26, 4, 22, e.Item.Height),
                        pal.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                if (e.Item is CardGap) return; // 卡片之间的空隙不画线
                int y = e.Item.Height / 2;
                using (Pen pen = new Pen(pal.Separator))
                    e.Graphics.DrawLine(pen, 36, y, e.Item.Width - 12, y); // 从文字左缘起笔
            }
        }

        // 卡片间隙（不可见）与行间缩进发丝线
        private sealed class CardGap : ToolStripSeparator { }
        private sealed class InsetSeparator : ToolStripSeparator { }

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
