using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace NetSpeedTray;

sealed class TrayApp : ApplicationContext
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Settings st = Settings.Load();
    readonly History hist = History.Load();
    readonly NotifyIcon ni = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Dictionary<string, (long rx, long tx)> last = new();
    NetworkInterface[] nics = Array.Empty<NetworkInterface>();
    DateTime lastNicRefresh = DateTime.MinValue, lastTick = DateTime.UtcNow, lastSave = DateTime.UtcNow;
    double up, down;
    string lastKey = "";
    Icon curIcon;
    HistoryForm form;
    AppTracker tracker;
    bool trackingOn;

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public TrayApp()
    {
        if (IsAdmin())
        {
            tracker = new AppTracker(hist);
            trackingOn = tracker.Start();
        }

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        ni.ContextMenuStrip = menu;
        ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowHistory(); };
        ni.Text = "Network speed";
        ni.Visible = true;

        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - lastTick).TotalSeconds;
        lastTick = now;
        if (dt < 0.1) return;

        if ((now - lastNicRefresh).TotalSeconds > 10)
        {
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToArray();
            }
            catch { }
            lastNicRefresh = now;
        }

        long dRx = 0, dTx = 0;
        foreach (var n in nics)
        {
            try
            {
                var s = n.GetIPv4Statistics();
                long rx = s.BytesReceived, tx = s.BytesSent;
                if (last.TryGetValue(n.Id, out var p))
                {
                    if (rx >= p.rx) dRx += rx - p.rx;
                    if (tx >= p.tx) dTx += tx - p.tx;
                }
                last[n.Id] = (rx, tx);
            }
            catch { }
        }
        down = dRx / dt; up = dTx / dt;
        hist.Add(History.TotalKey, dRx, dTx);
        UpdateIcon();

        if ((now - lastSave).TotalSeconds > 60) { hist.Save(); lastSave = now; }
    }

    void UpdateIcon()
    {
        string u = Fmt.Short(up, st) + "↑";
        string d = Fmt.Short(down, st) + "↓";
        string l1 = st.UploadOnTop ? u : d, l2 = st.UploadOnTop ? d : u;
        string key = l1 + "|" + l2;
        if (key == lastKey) return;
        lastKey = key;

        var old = curIcon;
        curIcon = Render(l1, l2);
        ni.Icon = curIcon;
        old?.Dispose();
        ni.Text = $"Upload: {Fmt.Long(up, st)}\nDownload: {Fmt.Long(down, st)}";
    }

    Icon Render(string a, string b)
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var sf = StringFormat.GenericTypographic;
            Font f = null;
            for (float size = 15; size >= 7; size -= 0.5f)
            {
                f?.Dispose();
                f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                float w = Math.Max(g.MeasureString(a, f, 64, sf).Width, g.MeasureString(b, f, 64, sf).Width);
                if (w <= 32) break;
            }
            using var brush = new SolidBrush(Color.FromArgb(st.TextColorArgb));
            g.DrawString(a, f, brush, 0, -1, sf);
            g.DrawString(b, f, brush, 0, 15, sf);
            f.Dispose();
        }
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); }
    }

    void BuildMenu(ContextMenuStrip m)
    {
        m.Items.Clear();
        m.Items.Add("Open history", null, (_, _) => ShowHistory());
        m.Items.Add("Text color...", null, (_, _) => PickColor());

        var unit = new ToolStripMenuItem("Unit");
        void AddUnit(string text, string val) =>
            unit.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { st.Unit = val; Changed(); }) { Checked = st.Unit == val });
        AddUnit("Auto (KB \u2194 MB)", "Auto");
        AddUnit("Always KB / kbps", "K");
        AddUnit("Always MB / Mbps", "M");
        m.Items.Add(unit);

        m.Items.Add(new ToolStripMenuItem("Use bits (kbps / Mbps)", null, (_, _) => { st.Bits = !st.Bits; Changed(); }) { Checked = st.Bits });
        m.Items.Add(new ToolStripMenuItem("Upload on top (off = download on top)", null, (_, _) => { st.UploadOnTop = !st.UploadOnTop; Changed(); }) { Checked = st.UploadOnTop });
        m.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() });
        m.Items.Add(new ToolStripSeparator());

        if (trackingOn) m.Items.Add(new ToolStripMenuItem("Per-app tracking: ON") { Enabled = false });
        else m.Items.Add("Enable per-app tracking (restart as admin)", null, (_, _) => RestartAsAdmin());

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => Quit());
    }

    void Changed() { st.Save(); lastKey = ""; UpdateIcon(); }

    void PickColor()
    {
        using var dlg = new ColorDialog { Color = Color.FromArgb(st.TextColorArgb), FullOpen = true };
        if (dlg.ShowDialog() == DialogResult.OK) { st.TextColorArgb = dlg.Color.ToArgb(); Changed(); }
    }

    void ShowHistory()
    {
        if (form != null && !form.IsDisposed) { form.Activate(); return; }
        form = new HistoryForm(hist, () => trackingOn);
        form.FormClosed += (_, _) => form = null;
        form.Show();
    }

    static bool IsAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("NetSpeedTray") != null; }
        catch { return false; }
    }

    static void ToggleAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (k.GetValue("NetSpeedTray") != null) k.DeleteValue("NetSpeedTray");
            else k.SetValue("NetSpeedTray", "\"" + Environment.ProcessPath + "\"");
        }
        catch { }
    }

    void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath)
            { UseShellExecute = true, Verb = "runas", Arguments = "--elevated" });
            Quit();
        }
        catch (Win32Exception) { /* user cancelled the UAC prompt */ }
    }

    void Quit()
    {
        timer.Stop();
        tracker?.Dispose();
        hist.Save();
        st.Save();
        ni.Visible = false;
        ni.Dispose();
        curIcon?.Dispose();
        ExitThread();
    }
}
