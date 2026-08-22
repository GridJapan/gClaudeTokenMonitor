// CtmMonitor — ctm のトレイ常駐 UI。
//
// タスクトレイのアイコンをクリックすると正方形のコンパクト窓が出る。
// その窓をクリックすると詳細窓が開き、過去ログを閲覧できる。
// アプリを終了すると ctm の常駐レコーダーも停止する。
//
// データは ctm が書いた ~/.ctm/ を読むだけ。API は叩かない。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 23, 28);
    public static readonly Color Card = Color.FromArgb(33, 32, 39);
    public static readonly Color Fg = Color.FromArgb(236, 234, 240);
    public static readonly Color Mut = Color.FromArgb(150, 146, 160);
    public static readonly Color Line = Color.FromArgb(52, 50, 60);
    public static readonly Color Ok = Color.FromArgb(95, 168, 138);
    public static readonly Color Warn = Color.FromArgb(214, 139, 107);
    public static readonly Color Bad = Color.FromArgb(201, 107, 143);
    public static readonly Color Accent = Color.FromArgb(107, 123, 214);
    public static readonly Color Border = Color.White;

    public static Color ForPct(double p)
    {
        if (p >= 90) return Bad;
        if (p >= 70) return Warn;
        return Ok;
    }
}

/// <summary>ctm が書いた 1 サンプル（使用率＋同じ窓の実測消費）。</summary>
class Sample
{
    public DateTime Ts;
    public string Key = "";
    public string Label = "";
    public double Percent;
    public string ResetsAt = "";
    public int Messages;
    public long Tokens;
    public double Cost;
}

/// <summary>~/.ctm 配下の読み取り。ctm 本体には触らない。</summary>
static class Store
{
    public static string Root
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ctm");
        }
    }

    public static string CtmExe
    {
        get
        {
            string local = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath) ?? ".", "ctm.exe");
            // 同じフォルダの ctm.exe を使う。無ければ PATH に任せる。
            return File.Exists(local) ? local : "ctm.exe";
        }
    }

    // 依存を増やさないための最小 JSON 読み取り。ctm の出力は純 ASCII の 1 行 1 レコード。
    static string Str(string line, string key)
    {
        string k = "\"" + key + "\":\"";
        int i = line.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return "";
        i += k.Length;
        int j = line.IndexOf('"', i);
        if (j < 0) return "";
        return Unescape(line.Substring(i, j - i));
    }

    static double Num(string line, string key)
    {
        string k = "\"" + key + "\":";
        int i = line.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += k.Length;
        int j = i;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' ||
                                   line[j] == '-' || line[j] == '+' || line[j] == 'e' || line[j] == 'E')) j++;
        double v;
        return double.TryParse(line.Substring(i, j - i), NumberStyles.Float,
            CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    static string Unescape(string s)
    {
        if (s.IndexOf("\\u", StringComparison.Ordinal) < 0) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u')
            {
                int code;
                if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out code))
                {
                    sb.Append((char)code);
                    i += 5;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) yield break;
        // ctm が追記中でも読めるように共有で開く。
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
                if (line.Length > 2) yield return line;
        }
    }

    /// <summary>ctm が書き込み中でも読めるように共有で開く。</summary>
    public static string ReadAllTextShared(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    public static string LimitsPath(DateTime d)
    {
        return Path.Combine(Root, "limits", d.ToString("yyyy-MM-dd") + ".ndjson");
    }

    public static string EventsPath(DateTime d)
    {
        return Path.Combine(Root, "events", d.ToString("yyyy-MM-dd") + ".ndjson");
    }

    public static List<Sample> LoadSamples(DateTime day)
    {
        var list = new List<Sample>();
        foreach (var line in ReadLines(LimitsPath(day)))
        {
            DateTime ts;
            DateTime.TryParse(Str(line, "ts"), null, DateTimeStyles.RoundtripKind, out ts);
            list.Add(new Sample
            {
                Ts = ts,
                Key = Str(line, "key"),
                Label = Str(line, "label"),
                Percent = Num(line, "percent"),
                ResetsAt = Str(line, "resets_at"),
                Messages = (int)Num(line, "messages"),
                Tokens = (long)Num(line, "tokens"),
                Cost = Num(line, "cost_usd"),
            });
        }
        return list;
    }

    /// <summary>最新の 1 巡分（窓ごとに最後のサンプル）。</summary>
    public static List<Sample> Latest()
    {
        var all = LoadSamples(DateTime.Now);
        if (all.Count == 0) all = LoadSamples(DateTime.Now.AddDays(-1));
        var byKey = new Dictionary<string, Sample>();
        foreach (var s in all) byKey[s.Key] = s;
        var order = new[] { "session", "weekly_all" };
        var outp = new List<Sample>();
        foreach (var k in order) if (byKey.ContainsKey(k)) { outp.Add(byKey[k]); byKey.Remove(k); }
        outp.AddRange(byKey.Values);
        return outp;
    }

    public class DayTotal
    {
        public int Messages;
        public long Tokens;
        public double Cost;
        public HashSet<string> Sessions = new HashSet<string>();
    }

    public static DayTotal Today() { return Totals(DateTime.Now); }

    public static DayTotal Totals(DateTime day)
    {
        var t = new DayTotal();
        foreach (var line in ReadLines(EventsPath(day)))
        {
            t.Messages++;
            t.Tokens += (long)Num(line, "total");
            t.Cost += Num(line, "cost_usd");
            var s = Str(line, "session");
            if (s.Length > 0) t.Sessions.Add(s);
        }
        return t;
    }

    public static bool RecorderAlive()
    {
        string p = Path.Combine(Root, "record.lock");
        if (!File.Exists(p)) return false;
        try
        {
            string txt = ReadAllTextShared(p);
            if (txt.Length == 0) return false;
            DateTime hb;
            if (!DateTime.TryParse(Str(txt.Replace("\n", "").Replace(" ", ""), "heartbeat"),
                    null, DateTimeStyles.RoundtripKind, out hb)) return false;
            return (DateTime.Now - hb.ToLocalTime()).TotalMinutes < 3;
        }
        catch { return false; }
    }

    public static void Run(string args, bool wait)
    {
        try
        {
            var psi = new ProcessStartInfo(CtmExe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var pr = Process.Start(psi);
            if (wait && pr != null) pr.WaitForExit(20000);
        }
        catch { }
    }

    /// <summary>トークンの表示単位。ウィンドウのクリックで切り替える。</summary>
    public enum Unit { Auto, K, M, Raw }

    public static Unit TokenUnit = Unit.Auto;

    public static string UnitName
    {
        get
        {
            switch (TokenUnit)
            {
                case Unit.K: return "K";
                case Unit.M: return "M";
                case Unit.Raw: return "RAW";
                default: return "AUTO";
            }
        }
    }

    public static void CycleUnit()
    {
        TokenUnit = TokenUnit == Unit.Auto ? Unit.K
                  : TokenUnit == Unit.K ? Unit.M
                  : TokenUnit == Unit.M ? Unit.Raw
                  : Unit.Auto;
        SaveSettings();
    }

    // --- UI 設定の永続化 -------------------------------------------------
    // ctm 本体は触らないファイルに置く。壊れていても既定値で動く。
    static string SettingsPath { get { return Path.Combine(Root, "ui.json"); } }

    public static Point WindowPos = Point.Empty;

    public static void LoadSettings()
    {
        try
        {
            var txt = ReadAllTextShared(SettingsPath);
            if (txt.Length == 0) return;
            var u = Str(txt, "unit");
            if (u == "K") TokenUnit = Unit.K;
            else if (u == "M") TokenUnit = Unit.M;
            else if (u == "RAW") TokenUnit = Unit.Raw;
            else TokenUnit = Unit.Auto;
            int x = (int)Num(txt, "x"), y = (int)Num(txt, "y");
            if (x != 0 || y != 0) WindowPos = new Point(x, y);
        }
        catch { }
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SettingsPath, string.Format(
                CultureInfo.InvariantCulture,
                "{{\"unit\":\"{0}\",\"x\":{1},\"y\":{2}}}",
                UnitName, WindowPos.X, WindowPos.Y));
        }
        catch { }
    }

    public static string Tokens(long n)
    {
        var ic = CultureInfo.InvariantCulture;
        switch (TokenUnit)
        {
            case Unit.K:
                return (n / 1e3).ToString("#,0.0", ic) + "K";
            case Unit.M:
                return (n / 1e6).ToString("#,0.00", ic) + "M";
            case Unit.Raw:
                return n.ToString("#,0", ic);
            default:
                if (n >= 1000000000) return (n / 1e9).ToString("0.00", ic) + "G";
                if (n >= 1000000) return (n / 1e6).ToString("0.00", ic) + "M";
                if (n >= 1000) return (n / 1e3).ToString("0.0", ic) + "K";
                return n.ToString(ic);
        }
    }

    public static string Money(double v)
    {
        return "$" + v.ToString(v >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture);
    }

    public static string Left(string resetsAt)
    {
        DateTime t;
        if (!DateTime.TryParse(resetsAt, null, DateTimeStyles.RoundtripKind, out t)) return "-";
        var d = t.ToLocalTime() - DateTime.Now;
        if (d.TotalSeconds <= 0) return "まもなく";
        if (d.TotalHours >= 24) return ((int)d.TotalDays) + "d" + d.Hours + "h";
        if (d.TotalHours >= 1) return ((int)d.TotalHours) + "h" + d.Minutes + "m";
        return d.Minutes + "m";
    }
}

/// <summary>トレイのアイコンをクリックすると出る正方形の小窓。</summary>
class CompactForm : Form
{
    readonly Timer timer = new Timer();
    List<Sample> samples = new List<Sample>();
    Store.DayTotal today = new Store.DayTotal();

    Point dragStart;
    bool dragging;
    bool moved;
    public Action OnQuit;

    public CompactForm()
    {
        // タスクバーに並ぶ本体ウィンドウ。枠は無いがアプリとして常駐する。
        Text = "Claude Token Monitor";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(240, 240);          // 正方形
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Icon = TrayApp.BuildIcon(Theme.Accent);
        MinimizeBox = true;

        timer.Interval = 2000;   // Go の取り込みが 5 秒粒度なので、これで十分追随する
        timer.Tick += delegate { Reload(); };
        timer.Start();

        // クリックで詳細、ドラッグで移動。両者は移動量で判別する。
        MouseDown += delegate (object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true; moved = false;
            dragStart = e.Location;
        };
        MouseMove += delegate (object o, MouseEventArgs e)
        {
            if (!dragging) return;
            int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
            if (!moved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            moved = true;
            Location = new Point(Location.X + dx, Location.Y + dy);
            Store.WindowPos = Location;
        };
        MouseUp += delegate (object o, MouseEventArgs e)
        {
            if (moved) Store.SaveSettings();
            if (e.Button == MouseButtons.Left && dragging && !moved)
            {
                Store.CycleUnit();      // 詳細は右クリックメニューから開く
                Invalidate();
            }
            dragging = false;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("過去ログを開く", null, delegate { OpenDetail(); });
        var top = new ToolStripMenuItem("最前面に固定");
        top.Click += delegate { TopMost = !TopMost; top.Checked = TopMost; };
        menu.Items.Add(top);
        menu.Items.Add("記録フォルダを開く", null, delegate
        {
            try { Process.Start("explorer.exe", Store.Root); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("最小化", null, delegate { WindowState = FormWindowState.Minimized; });
        menu.Items.Add("終了（記録も停止）", null, delegate
        {
            if (OnQuit != null) OnQuit();
        });
        ContextMenuStrip = menu;

        KeyPreview = true;
        KeyDown += delegate (object o, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) WindowState = FormWindowState.Minimized;
        };

        FormClosing += delegate (object o, FormClosingEventArgs e)
        {
            // 閉じる = アプリ終了。記録も一緒に止める。
            if (e.CloseReason == CloseReason.UserClosing && OnQuit != null)
            {
                e.Cancel = true;
                OnQuit();
            }
        };
    }

    DetailForm detail;

    // モニタ窓は出したまま、詳細窓を横に開く。既に開いていれば前面に出すだけ。
    void OpenDetail()
    {
        if (detail == null || detail.IsDisposed)
        {
            detail = new DetailForm();
            detail.FormClosed += delegate { detail = null; };
            detail.Show();
        }
        if (detail.WindowState == FormWindowState.Minimized)
            detail.WindowState = FormWindowState.Normal;
        detail.Activate();
        detail.BringToFront();
    }

    public void Reload()
    {
        samples = Store.Latest();
        today = Store.Today();
        Invalidate();
    }

    // FormBorderStyle.None のままだと WS_MINIMIZEBOX が付かず、タスクバーの
    // ボタンを押しても最小化されない。スタイルを直接足して有効にする。
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_MINIMIZEBOX = 0x00020000;
            const int CS_DBLCLKS = 0x0008;
            var cp = base.CreateParams;
            cp.Style |= WS_MINIMIZEBOX;
            cp.ClassStyle |= CS_DBLCLKS;
            return cp;
        }
    }

    /// <summary>表示されていれば隠し、隠れていれば出す。</summary>
    public void Toggle()
    {
        bool shown = Visible && WindowState != FormWindowState.Minimized;
        if (shown) Conceal();
        else Surface();
    }

    public void Conceal()
    {
        WindowState = FormWindowState.Minimized;
    }

    public void PlaceBottomRight()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        // 前回ドラッグした位置を覚えている場合はそこへ。画面外なら右下に戻す。
        if (Store.WindowPos != Point.Empty
            && Store.WindowPos.X > wa.Left - Width + 40 && Store.WindowPos.X < wa.Right - 40
            && Store.WindowPos.Y > wa.Top - 10 && Store.WindowPos.Y < wa.Bottom - 40)
        {
            Location = Store.WindowPos;
            return;
        }
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    /// <summary>タスクバー/トレイから呼ばれる。最小化されていれば元に戻して前面へ。</summary>
    public void Surface()
    {
        Reload();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
        // 1px の白い縁取り。角のドットが欠けないよう Pixel オフセットで描く。
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var p = new Pen(Theme.Border, 1f))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        g.SmoothingMode = prev;

        var fSmall = new Font("Yu Gothic UI", 8f);
        var fPct = new Font("Yu Gothic UI", 15f, FontStyle.Bold);
        var fMid = new Font("Yu Gothic UI", 9f, FontStyle.Bold);

        int y = 10;
        using (var b = new SolidBrush(Theme.Mut))
            g.DrawString("CLAUDE TOKEN MONITOR", fSmall, b, 12, y);
        using (var b = new SolidBrush(Theme.Accent))
        {
            var u = Store.UnitName;
            var sz = g.MeasureString(u, fSmall);
            g.DrawString(u, fSmall, b, Width - 12 - sz.Width, y);
        }
        y += 20;

        if (samples.Count == 0)
        {
            using (var b = new SolidBrush(Theme.Mut))
                g.DrawString("記録待ち…\n(ctm record が\n 使用率を取るまで)", fSmall, b, 12, y + 20);
        }

        foreach (var s in samples)
        {
            if (y > Height - 66) break;
            var col = Theme.ForPct(s.Percent);
            using (var b = new SolidBrush(Theme.Fg))
                g.DrawString(s.Label, fSmall, b, 12, y);
            using (var b = new SolidBrush(Theme.Mut))
            {
                var sz = g.MeasureString(Store.Left(s.ResetsAt), fSmall);
                g.DrawString(Store.Left(s.ResetsAt), fSmall, b, Width - 12 - sz.Width, y);
            }
            y += 17;
            using (var b = new SolidBrush(col))
                g.DrawString(s.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", fPct, b, 10, y - 4);
            // バー
            int bx = 62, bw = Width - bx - 12;
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, bx, y + 6, bw, 7);
            using (var b = new SolidBrush(col))
                g.FillRectangle(b, bx, y + 6, (int)(bw * Math.Min(s.Percent, 100) / 100.0), 7);
            using (var b = new SolidBrush(Theme.Mut))
                g.DrawString(Store.Tokens(s.Tokens) + "  " + Store.Money(s.Cost),
                    fSmall, b, bx, y + 15);
            y += 38;
        }

        // 今日の実測
        using (var p = new Pen(Theme.Line)) g.DrawLine(p, 12, Height - 46, Width - 12, Height - 46);
        using (var b = new SolidBrush(Theme.Mut))
            g.DrawString("TODAY", fSmall, b, 12, Height - 40);
        using (var b = new SolidBrush(Theme.Fg))
            g.DrawString(Store.Tokens(today.Tokens) + " tok   " + Store.Money(today.Cost),
                fMid, b, 12, Height - 25);
        using (var b = new SolidBrush(Theme.Mut))
        {
            string s2 = today.Sessions.Count + " session";
            var sz = g.MeasureString(s2, fSmall);
            g.DrawString(s2, fSmall, b, Width - 12 - sz.Width, Height - 22);
        }

        using (var b = new SolidBrush(Theme.Line))
            g.DrawString("クリックで詳細 / ドラッグで移動", new Font("Yu Gothic UI", 7f), b, 12, Height - 60);

        using (var b0 = new SolidBrush(Theme.Line))
        using (var f0 = new Font("Yu Gothic UI", 7f))
            g.DrawString("クリックで単位切替 / ドラッグで移動", f0, b0, 12, Height - 60);

        if (!Store.RecorderAlive())
            using (var b = new SolidBrush(Theme.Bad))
                g.DrawString("● 記録停止中", fSmall, b, Width - 90, Height - 40);

        fSmall.Dispose(); fPct.Dispose(); fMid.Dispose();
    }
}

/// <summary>過去ログの閲覧窓。</summary>
class DetailForm : Form
{
    readonly ComboBox dayBox = new ComboBox();
    readonly ListView limitsView = new ListView();
    readonly ListView eventsView = new ListView();
    readonly Label summary = new Label();

    public DetailForm()
    {
        Text = "Claude Token Monitor — 過去ログ";
        Size = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = new Font("Yu Gothic UI", 9f);

        var top = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.Bg };
        dayBox.DropDownStyle = ComboBoxStyle.DropDownList;
        dayBox.Location = new Point(12, 12);
        dayBox.Width = 160;
        dayBox.BackColor = Theme.Card;
        dayBox.ForeColor = Theme.Fg;
        dayBox.FlatStyle = FlatStyle.Flat;
        dayBox.SelectedIndexChanged += delegate { LoadDay(); };

        summary.Location = new Point(190, 14);
        summary.AutoSize = true;
        summary.ForeColor = Theme.Mut;

        var open = new Button
        {
            Text = "フォルダを開く",
            Location = new Point(12, 42),
            Width = 160,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Card,
            ForeColor = Theme.Fg,
        };
        open.FlatAppearance.BorderColor = Theme.Line;
        open.Click += delegate
        {
            try { Process.Start("explorer.exe", Store.Root); } catch { }
        };

        top.Controls.Add(dayBox);
        top.Controls.Add(summary);
        top.Controls.Add(open);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var t1 = new TabPage("プラン使用制限の推移") { BackColor = Theme.Bg };
        var t2 = new TabPage("メッセージ明細") { BackColor = Theme.Bg };

        Setup(limitsView, new[] { "時刻", "窓", "使用率", "リセットまで", "実測メッセージ", "実測トークン", "実測コスト" },
            new[] { 90, 150, 80, 110, 120, 140, 110 });
        Setup(eventsView, new[] { "時刻", "セッション", "作業ディレクトリ", "モデル", "cache-read", "output", "合計", "コスト" },
            new[] { 90, 90, 190, 150, 110, 90, 110, 100 });

        t1.Controls.Add(limitsView);
        t2.Controls.Add(eventsView);
        tabs.TabPages.Add(t1);
        tabs.TabPages.Add(t2);

        Controls.Add(tabs);
        Controls.Add(top);

        FillDays();
    }

    static void Setup(ListView v, string[] cols, int[] widths)
    {
        v.Dock = DockStyle.Fill;
        v.View = View.Details;
        v.FullRowSelect = true;
        v.GridLines = false;
        v.BackColor = Theme.Card;
        v.ForeColor = Theme.Fg;
        v.BorderStyle = BorderStyle.None;
        for (int i = 0; i < cols.Length; i++) v.Columns.Add(cols[i], widths[i]);
    }

    void FillDays()
    {
        var dir = Path.Combine(Store.Root, "events");
        var days = new List<string>();
        if (Directory.Exists(dir))
            days = Directory.GetFiles(dir, "*.ndjson")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderByDescending(x => x, StringComparer.Ordinal)
                .ToList();
        foreach (var d in days) dayBox.Items.Add(d);
        if (dayBox.Items.Count > 0) dayBox.SelectedIndex = 0;
    }

    void LoadDay()
    {
        try { LoadDayCore(); }
        catch (Exception ex)
        {
            summary.Text = "読み込みに失敗: " + ex.Message;
        }
    }

    void LoadDayCore()
    {
        DateTime day;
        if (!DateTime.TryParse(dayBox.SelectedItem as string, out day)) return;

        limitsView.BeginUpdate();
        limitsView.Items.Clear();
        foreach (var s in Store.LoadSamples(day))
        {
            var it = new ListViewItem(s.Ts.ToString("HH:mm:ss"));
            it.SubItems.Add(s.Label);
            it.SubItems.Add(s.Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%");
            it.SubItems.Add(Store.Left(s.ResetsAt));
            it.SubItems.Add(s.Messages.ToString("N0"));
            it.SubItems.Add(s.Tokens.ToString("N0"));
            it.SubItems.Add(Store.Money(s.Cost));
            it.ForeColor = Theme.ForPct(s.Percent);
            limitsView.Items.Add(it);
        }
        limitsView.EndUpdate();

        eventsView.BeginUpdate();
        eventsView.Items.Clear();
        var t = Store.Totals(day);
        int n = 0;
        foreach (var line in Store.ReadLines(Store.EventsPath(day)))
        {
            if (line.Length < 3) continue;
            n++;
            var it = new ListViewItem(Field(line, "ts", 11, 8));
            it.SubItems.Add(Cut(FieldStr(line, "session"), 8));
            it.SubItems.Add(FieldStr(line, "cwd_name"));
            it.SubItems.Add(FieldStr(line, "model"));
            it.SubItems.Add(((long)FieldNum(line, "cache_read")).ToString("N0"));
            it.SubItems.Add(((long)FieldNum(line, "output")).ToString("N0"));
            it.SubItems.Add(((long)FieldNum(line, "total")).ToString("N0"));
            it.SubItems.Add(Store.Money(FieldNum(line, "cost_usd")));
            eventsView.Items.Add(it);
        }
        eventsView.EndUpdate();

        summary.Text = string.Format("{0}   {1} メッセージ / {2} トークン / {3} / {4} セッション",
            day.ToString("yyyy-MM-dd"), t.Messages.ToString("N0"),
            Store.Tokens(t.Tokens), Store.Money(t.Cost), t.Sessions.Count);
    }

    static string Cut(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

    // Store の private ヘルパを再利用しないための最小実装。
    static string FieldStr(string line, string key)
    {
        string k = "\"" + key + "\":\"";
        int i = line.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return "";
        i += k.Length;
        int j = line.IndexOf('"', i);
        return j < 0 ? "" : Decode(line.Substring(i, j - i));
    }

    static string Decode(string s)
    {
        if (s.IndexOf("\\u", StringComparison.Ordinal) < 0) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u')
            {
                int code;
                if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out code))
                {
                    sb.Append((char)code); i += 5; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static double FieldNum(string line, string key)
    {
        string k = "\"" + key + "\":";
        int i = line.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += k.Length;
        int j = i;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' || line[j] == '-')) j++;
        double v;
        return double.TryParse(line.Substring(i, j - i), NumberStyles.Float,
            CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    static string Field(string line, string key, int off, int len)
    {
        var s = FieldStr(line, key);
        return s.Length >= off + len ? s.Substring(off, len) : s;
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon icon = new NotifyIcon();
    readonly CompactForm compact = new CompactForm();
    readonly Timer tip = new Timer();

    public TrayApp()
    {
        icon.Icon = BuildIcon(Theme.Accent);
        icon.Text = "Claude Token Monitor";
        icon.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("表示", null, delegate { compact.Surface(); });
        menu.Items.Add("過去ログ", null, delegate { new DetailForm().Show(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("記録フォルダを開く", null, delegate
        {
            try { Process.Start("explorer.exe", Store.Root); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了（記録も停止）", null, delegate { Quit(); });
        icon.ContextMenuStrip = menu;

        icon.MouseClick += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Toggle();
        };

        // タスクバーに並ぶよう、本体ウィンドウを最初から表示しておく。
        compact.OnQuit = Quit;
        compact.PlaceBottomRight();
        compact.Show();

        // 記録が止まっていれば起こす。
        if (!Store.RecorderAlive()) Store.Run("record -quiet", false);

        tip.Interval = 30000;
        tip.Tick += delegate { UpdateTip(); };
        tip.Start();
        UpdateTip();
    }

    void Toggle() { compact.Toggle(); }

    void UpdateTip()
    {
        try
        {
            var s = Store.Latest();
            var t = Store.Today();
            var sb = new StringBuilder();
            foreach (var x in s)
                sb.Append(x.Label).Append(' ')
                  .Append(x.Percent.ToString("0", CultureInfo.InvariantCulture)).Append("%  ");
            sb.Append('\n').Append(Store.Tokens(t.Tokens)).Append(" tok  ").Append(Store.Money(t.Cost));
            var txt = sb.ToString();
            icon.Text = txt.Length > 62 ? txt.Substring(0, 62) : txt;   // NotifyIcon.Text の上限

            double max = 0;
            foreach (var x in s) if (x.Percent > max) max = x.Percent;
            icon.Icon = BuildIcon(Store.RecorderAlive() ? Theme.ForPct(max) : Theme.Bad);
        }
        catch { }
    }

    // アイコンファイルを持たずに済ませる。使用率の色で塗った丸を描く。
    public static Icon BuildIcon(Color c)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 3, 3, 26, 26);
                using (var b = new SolidBrush(Color.FromArgb(24, 23, 28)))
                    g.FillEllipse(b, 10, 10, 12, 12);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    void Quit()
    {
        tip.Stop();
        icon.Visible = false;
        compact.OnQuit = null;   // FormClosing の再入を防ぐ
        compact.Hide();
        // アプリ終了と一緒に Go の常駐も止める。
        Store.Run("stop", true);
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { icon.Dispose(); compact.Dispose(); }
        base.Dispose(disposing);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // UI を出さずに読み取り経路だけ検証する（ctm が書き込み中でも通るか）。
        if (args.Length > 0 && args[0] == "--selftest")
        {
            var day = DateTime.Now;
            int limits = Store.LoadSamples(day).Count;
            var t = Store.Totals(day);
            int ev = 0;
            foreach (var l in Store.ReadLines(Store.EventsPath(day))) ev++;
            MessageBox.Show(string.Format(
                "selftest OK{0}limits {1} 件 / events {2} 件{0}{3} tok / {4}{0}recorder={5}",
                Environment.NewLine, limits, ev,
                Store.Tokens(t.Tokens), Store.Money(t.Cost), Store.RecorderAlive()),
                "CtmMonitor selftest");
            return;
        }

        bool created;
        using (new System.Threading.Mutex(true, "CtmMonitorSingleInstance", out created))
        {
            if (!created)
            {
                MessageBox.Show("Claude Token Monitor は既に起動しています。",
                    "CtmMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s2, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "CtmMonitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s2, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                MessageBox.Show(ex == null ? "不明なエラー" : ex.Message, "CtmMonitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            Store.LoadSettings();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }
}
