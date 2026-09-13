// PowerPlanTray —— Windows 系统托盘电源计划快速切换工具
// 左键点击托盘图标弹出菜单，点击电源计划名即可切换；
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
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace PowerPlanTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
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

    // 计划菜单项：故意不用 Checked 属性（下拉菜单会给 Checked 项自绘选中底框，
    // 与自定义渲染冲突），激活状态直接体现在色点图标上（点+同色圆环）

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

            // 定期检查电源计划是否有外部变化（比如在控制面板里改了）
            refreshTimer = new Timer { Interval = cfg.RefreshMs };
            refreshTimer.Tick += delegate
            {
                RefreshIfChanged();
                TrimMemory(); // 周期性修剪，常驻内存保持在个位数 MB
            };
            refreshTimer.Start();

            FixAutoRunPath();

            tray.BalloonTipTitle = "PowerPlanTray 已在托盘运行";
            tray.BalloonTipText = "左键点击托盘图标即可快速切换电源计划。";
            tray.ShowBalloonTip(3000);
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

        private ContextMenuStrip BuildMenu(List<PlanInfo> plans)
        {
            MenuPalette pal = GetMenuPalette(IsDarkMode());
            var strip = new ContextMenuStrip
            {
                Renderer = new ModernMenuRenderer(pal),
                Font = MenuFont,
                Padding = new Padding(6),
                DropShadowEnabled = false
            };

            foreach (PlanInfo p in plans)
            {
                Guid guid = p.Guid; // 闭包必须用循环内的局部副本
                var item = new ToolStripMenuItem(p.Name)
                {
                    Image = GetDotImage(ModeAccent(p.Name), p.IsActive),
                    ImageScaling = ToolStripItemImageScaling.None,
                    ForeColor = pal.Text
                };
                item.Click += delegate { ActivatePlan(guid); };
                strip.Items.Add(item);
            }

            strip.Items.Add(new ToolStripSeparator { AutoSize = false, Height = 10 });

            var autoRun = new ToolStripMenuItem("开机自动启动") { Checked = IsAutoRun(), ForeColor = pal.Text };
            autoRun.Click += delegate
            {
                SetAutoRun(!IsAutoRun());
                autoRun.Checked = IsAutoRun();
            };
            strip.Items.Add(autoRun);

            var refreshItem = new ToolStripMenuItem("刷新电源计划列表") { ForeColor = pal.Text };
            refreshItem.Click += delegate { lastSignature = ""; };
            strip.Items.Add(refreshItem);

            var openPanel = new ToolStripMenuItem("打开控制面板电源选项") { ForeColor = pal.Text };
            openPanel.Click += delegate
            {
                try { Process.Start("control.exe", "/name Microsoft.PowerOptions"); }
                catch { }
            };
            strip.Items.Add(openPanel);

            strip.Items.Add(new ToolStripSeparator { AutoSize = false, Height = 10 });

            var exitItem = new ToolStripMenuItem("退出") { ForeColor = pal.Text };
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

            strip.Opened += delegate
            {
                menuOpen = true;
                ApplyRoundedRegion(strip, 10);
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

        // ---- 图标：渐变圆角方块 + 白色电池（内含闪电），颜色随模式变化 ----

        // 图标颜色随模式变化（iOS 风格）：绿=电影模式（安静），蓝=AI模式，红=游戏（高性能）
        private void ApplyIconForActive(List<PlanInfo> plans)
        {
            PlanInfo active = null;
            foreach (PlanInfo p in plans) { if (p.IsActive) { active = p; break; } }
            int rank = active != null ? PlanRank(active.Name) : 1;

            Color top, mid, bottom, inner;
            ModePalette(rank, out top, out mid, out bottom, out inner);

            Icon newIcon = CreateIcon(top, mid, bottom, inner);
            Icon old = currentIcon;
            tray.Icon = newIcon;
            currentIcon = newIcon;
            if (old != null) old.Dispose();
        }

        // 各档位主题色：图标渐变与菜单色点共用
        private static void ModePalette(int rank, out Color top, out Color mid, out Color bottom, out Color inner)
        {
            if (rank == 0)          // 绿：电影模式
            {
                top = Color.FromArgb(255, 123, 223, 159);
                mid = Color.FromArgb(255, 60, 190, 121);
                bottom = Color.FromArgb(255, 35, 164, 92);
                inner = Color.FromArgb(255, 28, 150, 80);
            }
            else if (rank == 2)     // 红：游戏模式
            {
                top = Color.FromArgb(255, 255, 123, 114);
                mid = Color.FromArgb(255, 239, 77, 77);
                bottom = Color.FromArgb(255, 198, 40, 40);
                inner = Color.FromArgb(255, 200, 55, 55);
            }
            else                    // 蓝：AI模式
            {
                top = Color.FromArgb(255, 90, 167, 255);
                mid = Color.FromArgb(255, 47, 142, 240);
                bottom = Color.FromArgb(255, 21, 101, 192);
                inner = Color.FromArgb(255, 23, 116, 200);
            }
        }

        private Color ModeAccent(string planName)
        {
            Color top, mid, bottom, inner;
            ModePalette(PlanRank(planName), out top, out mid, out bottom, out inner);
            return mid;
        }

        private static Icon CreateIcon(Color top, Color mid, Color bottom, Color inner)
        {
            // 按“托盘真实显示尺寸”出图：先在 128px（2x）画布上超采样绘制，
            // 再用高质量双三次插值缩到目标尺寸，系统零缩放显示，边缘才清晰。
            // 目标尺寸按真实 DPI 计算（16 基准 × 缩放系数，100%=16px、125%=20px、200%=32px），
            // PerMonitorV2 清单生效后 FromHwnd 返回主屏真实 DPI。
            float dpi;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            int target = (int)Math.Round(16f * dpi / 96f);
            if (target < 16) target = 16;
            if (target > 64) target = 64;

            using (var design = new Bitmap(128, 128))
            using (Graphics gd = Graphics.FromImage(design))
            {
                gd.SmoothingMode = SmoothingMode.AntiAlias;
                gd.ScaleTransform(2f, 2f); // 绘制代码仍使用 64 空间坐标

                using (GraphicsPath tile = Squircle(new RectangleF(0.5f, 0.5f, 63f, 63f)))
                using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), top, bottom, 90f))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { top, mid, bottom };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    gradient.InterpolationColors = blend;
                    gd.FillPath(gradient, tile);
                }

                // 电池轻投影
                using (GraphicsPath bodySh = RoundedRect(new RectangleF(11f, 21.8f, 33f, 24f), 6f))
                using (GraphicsPath capSh = RoundedRect(new RectangleF(46.5f, 27.8f, 6.5f, 12f), 3f))
                using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
                {
                    gd.FillPath(shadow, bodySh);
                    gd.FillPath(shadow, capSh);
                }

                // 白色电池：大圆角机身 + 正极帽，居中大留白
                using (var white = new SolidBrush(Color.White))
                {
                    using (GraphicsPath body = RoundedRect(new RectangleF(11f, 20f, 33f, 24f), 6f))
                        gd.FillPath(white, body);
                    using (GraphicsPath cap = RoundedRect(new RectangleF(46.5f, 26f, 6.5f, 12f), 3f))
                        gd.FillPath(white, cap);
                }

                // 机身内闪电（镂空，圆角拐点）
                using (GraphicsPath bolt = new GraphicsPath())
                {
                    PointF[] pts =
                    {
                        new PointF(31.25f, 24.5f), new PointF(20.75f, 35.5f), new PointF(26.25f, 35.5f),
                        new PointF(24.25f, 40f), new PointF(34.25f, 29.5f), new PointF(28.75f, 29.5f)
                    };
                    bolt.AddPolygon(pts);
                    using (var fill = new SolidBrush(inner))
                        gd.FillPath(fill, bolt);
                    using (var rounder = new Pen(inner, 2.2f) { LineJoin = LineJoin.Round })
                        gd.DrawPath(rounder, bolt);
                }

                using (var bmp = new Bitmap(target, target))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(design, new Rectangle(0, 0, target, target), new Rectangle(0, 0, 128, 128), GraphicsUnit.Pixel);

                    IntPtr hIcon = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
                    finally { DestroyIcon(hIcon); }
                }
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

        // ---- 2026 风格菜单：圆角窗口、亮/暗自适应、模式色点、圆角悬停高亮 ----

        private static readonly Font MenuFont = new Font("Microsoft YaHei UI", 9.5F);
        private static readonly Font MenuCheckFont = new Font("Segoe MDL2 Assets", 10F);
        private static readonly Dictionary<string, Bitmap> DotImageCache = new Dictionary<string, Bitmap>();

        // 模式图标（缓存）：色点 + 同色圆环画在同一张图、同一圆心，保证绝对同心
        private static Bitmap GetDotImage(Color accent, bool active)
        {
            string key = accent.ToArgb() + "_" + (active ? "1" : "0");
            Bitmap bmp;
            if (!DotImageCache.TryGetValue(key, out bmp))
            {
                bmp = new Bitmap(18, 18);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (active)
                        using (Pen ring = new Pen(accent, 1.5f))
                            g.DrawEllipse(ring, 2f, 2f, 14f, 14f);   // 外圈：直径14（仅当前计划）
                    using (SolidBrush brush = new SolidBrush(accent))
                        g.FillEllipse(brush, 5f, 5f, 8f, 8f);        // 内点：直径8，圆心(9,9)
                }
                DotImageCache[key] = bmp;
            }
            return bmp;
        }

        private sealed class MenuPalette
        {
            public Color Bg, Text, Hover, Separator;
        }

        private static MenuPalette GetMenuPalette(bool dark)
        {
            MenuPalette p = new MenuPalette();
            if (dark)
            {
                p.Bg = Color.FromArgb(43, 43, 43);
                p.Text = Color.FromArgb(242, 242, 242);
                p.Hover = Color.FromArgb(32, 255, 255, 255);
                p.Separator = Color.FromArgb(64, 64, 64);
            }
            else
            {
                p.Bg = Color.FromArgb(251, 251, 251);
                p.Text = Color.FromArgb(26, 26, 26);
                p.Hover = Color.FromArgb(16, 0, 0, 0);
                p.Separator = Color.FromArgb(230, 230, 230);
            }
            return p;
        }

        // 跟随系统"应用模式"亮暗设置（Win10/11）
        private static bool IsDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object v = key.GetValue("AppsUseLightTheme");
                    return (v is int) && ((int)v == 0);
                }
            }
            catch { return false; }
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
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Enabled) return;

                if (e.Item.Selected)
                {
                    Rectangle rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                    using (GraphicsPath path = RoundedRect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 7f))
                    using (SolidBrush hover = new SolidBrush(pal.Hover))
                        e.Graphics.FillPath(hover, path);
                }

                float cy = e.Item.Height / 2f;

                // 开机自动启动：右侧对勾（Segoe MDL2 图标）；
                // 计划项的选中态已画在 Image 里（点+圆环），这里不再处理
                ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
                if (menuItem != null && menuItem.Checked)
                    TextRenderer.DrawText(e.Graphics, "\uE73E", MenuCheckFont,
                        new Rectangle(e.Item.Width - 26, 0, 22, e.Item.Height),
                        pal.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (Pen pen = new Pen(pal.Separator))
                    e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
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
