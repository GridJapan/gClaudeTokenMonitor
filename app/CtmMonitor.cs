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

    static long limitsSize = -1;
    static List<Sample> limitsCache = new List<Sample>();

    /// <summary>最新の 1 巡分（窓ごとに最後のサンプル）。
    /// 5 分に 1 回しか増えないファイルなので、サイズが変わったときだけ読み直す。</summary>
    public static List<Sample> Latest()
    {
        try
        {
            var fi = new FileInfo(LimitsPath(DateTime.Now));
            long sz = fi.Exists ? fi.Length : 0;
            if (sz == limitsSize) return limitsCache;
            limitsSize = sz;
        }
        catch { }
        var all = LoadSamples(DateTime.Now);
        if (all.Count == 0) all = LoadSamples(DateTime.Now.AddDays(-1));
        var byKey = new Dictionary<string, Sample>();
        foreach (var s in all) byKey[s.Key] = s;
        var order = new[] { "session", "weekly_all" };
        var outp = new List<Sample>();
        foreach (var k in order) if (byKey.ContainsKey(k)) { outp.Add(byKey[k]); byKey.Remove(k); }
        outp.AddRange(byKey.Values);
        limitsCache = outp;
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

    // ---- 200ms ポーリング用の増分読み --------------------------------
    // 全量パース（数 MB）を 5 回/秒やると CPU を無駄に食うので、
    // 前回読んだバイト位置を覚えて追記分だけ集計する。
    static long liveOff;
    static DayTotal liveTot = new DayTotal();
    static string liveDay = "";

    public static DayTotal TodayLive()
    {
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        string path = EventsPath(DateTime.Now);
        if (day != liveDay) { liveDay = day; liveOff = 0; liveTot = new DayTotal(); }
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return liveTot;
            if (fi.Length < liveOff) { liveOff = 0; liveTot = new DayTotal(); }  // 作り直された
            if (fi.Length == liveOff) return liveTot;

            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(liveOff, SeekOrigin.Begin);
                buf = new byte[fs.Length - liveOff];
                int off = 0;
                while (off < buf.Length)
                {
                    int n = fs.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
            }
            // 最後の改行までだけ処理する。書きかけの行は次回に持ち越す
            int lastNL = -1;
            for (int i = buf.Length - 1; i >= 0; i--)
                if (buf[i] == (byte)'\n') { lastNL = i; break; }
            if (lastNL < 0) return liveTot;

            var text = Encoding.ASCII.GetString(buf, 0, lastNL + 1);
            foreach (var line in text.Split('\n'))
            {
                if (line.Length < 3) continue;
                liveTot.Messages++;
                liveTot.Tokens += (long)Num(line, "total");
                liveTot.Cost += Num(line, "cost_usd");
                var sid = Str(line, "session");
                if (sid.Length > 0) liveTot.Sessions.Add(sid);
            }
            liveOff += lastNL + 1;
        }
        catch { }
        return liveTot;
    }

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

    public static void SetUnit(Unit u)
    {
        TokenUnit = u;
        SaveSettings();
    }

    public static void ToggleLayout()
    {
        LayoutMode = LayoutMode == Layout.Detail ? Layout.Big : Layout.Detail;
        SaveSettings();
    }

    // --- UI 設定の永続化 -------------------------------------------------
    // ctm 本体は触らないファイルに置く。壊れていても既定値で動く。
    static string SettingsPath { get { return Path.Combine(Root, "ui.json"); } }

    public static Point WindowPos = Point.Empty;

    /// <summary>コンパクト窓の見た目。クリックで切り替える。</summary>
    public enum Layout { Detail, Big }

    public static Layout LayoutMode = Layout.Detail;

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
            LayoutMode = Str(txt, "layout") == "Big" ? Layout.Big : Layout.Detail;
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
                "{{\"unit\":\"{0}\",\"x\":{1},\"y\":{2},\"layout\":\"{3}\"}}",
                UnitName, WindowPos.X, WindowPos.Y, LayoutMode));
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

        timer.Interval = 200;    // アーカイブ側も 200ms 周期。増分読みなので負荷は Stat 1 回分
        timer.Tick += delegate { Reload(); };
        timer.Start();

        // 60fps 級の再描画でもちらつかないように明示。Form.DoubleBuffered だけでは不足。
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer, true);
        fx.Interval = 33;
        fx.Tick += FxTick;
        fx.Start();

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
                Store.ToggleLayout();   // 単位は右クリックの「表示単位」から
                Invalidate();
            }
            dragging = false;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("過去ログを開く", null, delegate { OpenDetail(); });

        // 表示単位はチェック式のサブメニューで選ぶ。
        var unitMenu = new ToolStripMenuItem("表示単位");
        var units = new[] { Store.Unit.Auto, Store.Unit.K, Store.Unit.M, Store.Unit.Raw };
        var labels = new[] { "AUTO（自動）", "K（千）", "M（百万）", "RAW（実数）" };
        var items = new ToolStripMenuItem[units.Length];
        for (int i = 0; i < units.Length; i++)
        {
            var u = units[i];
            var mi = new ToolStripMenuItem(labels[i]);
            mi.Checked = Store.TokenUnit == u;
            mi.Click += delegate
            {
                Store.SetUnit(u);
                for (int k = 0; k < items.Length; k++) items[k].Checked = (units[k] == u);
                Invalidate();
            };
            items[i] = mi;
            unitMenu.DropDownItems.Add(mi);
        }
        menu.Items.Add(unitMenu);
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

    // ---- Big レイアウトの演出状態 --------------------------------------
    readonly Timer fx = new Timer();          // 33ms ≒ 30fps。Big 表示中だけ動かす
    double shown, target, vel;                // 表示値のスプリング補間
    bool primed;                              // 初回読み込み済みか（初回はスナップ）
    float punch;                              // スケールパンチ（減衰）
    int tier;                                 // 直近バーストの強度 1..4
    float shimmerT = -1f;                     // ハイライト帯の進行 0..1（-1 = 停止）
    double waterPhase;                        // 水面の位相

    // ---- 水の物理（ウィンドウを動かすと揺れる） ------------------------
    // 水面の傾き slosh は減衰バネ。ウィンドウの水平加速度が外力になり、
    // 手を離すと固有振動でちゃぷちゃぷ戻る。bob は縦揺れ、chop は撹拌量。
    // 実際の水槽と同じく、揺れを固有モードの重ね合わせで表す。
    //   m1: 基本モード cos(πx/W)  — 片側に寄る。周期が長く、減衰が浅い
    //   m2: 対称モード cos(2πx/W) — 中央が跳ねる。縦揺すりで励起（Faraday 波）
    //   m3: 高次モード cos(3πx/W) — 細かい返し波。速く、すぐ収まる
    // 高いモードほど固有振動数が高く減衰が速い、という実物の性質に合わせる。
    float m1, m1v, m2, m2v, m3, m3v;
    float bob, bobVel;                        // 水位全体の上下（慣性）
    float chop;                               // 波の荒れ 0..1（振幅と位相速度に乗る）
    Point lastLoc;                            // 前フレームのウィンドウ位置
    bool haveLastLoc;
    float winVX, winVY;                       // 前フレームのウィンドウ速度
    readonly List<float[]> parts = new List<float[]>();   // x,y,vx,vy,age,maxAge,size
    readonly List<object[]> floats = new List<object[]>(); // [text, life]
    readonly Random rng = new Random();

    // 30fps で毎フレーム new しないための共有フォント
    readonly Font fT8 = new Font("Yu Gothic UI", 8f);
    readonly Font fT9b = new Font("Yu Gothic UI", 9f, FontStyle.Bold);
    readonly Font fMid11 = new Font("Yu Gothic UI", 11f, FontStyle.Bold);
    readonly Font fHuge = new Font("Yu Gothic UI", 36f, FontStyle.Bold);
    Font fitFont;
    Font fitFontR;                            // 同サイズのレギュラー（単位 M/K 用）
    int fitLen;
    readonly Font fHugeR = new Font("Yu Gothic UI", 36f, FontStyle.Regular);
    float flash;                              // バースト直後に数字が金色に光る 0..1
    int glintFor;                             // 水面グリントを湧かせ続ける残りフレーム数
    readonly Font fF13 = new Font("Yu Gothic UI", 13f, FontStyle.Bold);

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
        var t = Store.TodayLive();
        long nt = t.Tokens;
        if (!primed)
        {
            primed = true;
            shown = target = nt;      // 起動直後の「今日すでに 2 億」は演出しない
        }
        else if (nt > target)
        {
            long delta = nt - (long)target;
            target = nt;
            if (Store.LayoutMode == Store.Layout.Big) TriggerFx(delta);
            else shown = nt;          // Detail 表示中は静かに追従
        }
        else if (nt < target)
        {
            target = nt;              // 日付が変わって今日の合計が減った
            shown = nt;
        }
        today = t;
        Invalidate();
    }

    /// <summary>増分の大きさで演出の強度を決める。5 秒ごとに必ず起きるので
    /// 小さい増分は控えめに、大きいバーストだけ派手にする。</summary>
    void TriggerFx(long delta)
    {
        tier = delta < 50000 ? 1 : delta < 200000 ? 2 : delta < 1000000 ? 3 : 4;
        punch = 0.03f + 0.02f * tier;
        flash = 0.5f + 0.125f * tier;                    // 数字が一瞬金色に光る
        if (tier >= 2) shimmerT = 0f;

        // キラ星は数字の縁のリングから外向きに弾ける。数字の真上に湧かせると
        // 白地に白で見えないので、外周に出してから減速させる
        int pn = tier == 1 ? 5 : tier == 2 ? 12 : tier == 3 ? 22 : 34;
        float cx0 = Width / 2f, cy0 = Height / 2f - 8;
        for (int i = 0; i < pn; i++)
        {
            double ang = rng.NextDouble() * Math.PI * 2;
            double rad = 34 + rng.NextDouble() * 30;
            float spd = (float)(1.6 + rng.NextDouble() * 2.6);
            parts.Add(new float[] {
                cx0 + (float)(Math.Cos(ang) * rad),
                cy0 + (float)(Math.Sin(ang) * rad * 0.55),
                (float)Math.Cos(ang) * spd,
                (float)(Math.Sin(ang) * spd * 0.7) - 0.4f,
                0f, (float)(34 + rng.NextDouble() * 26),
                (float)(2.6 + rng.NextDouble() * 2.6), 0f });
        }
        floats.Add(new object[] { "+" + Store.Tokens(delta), 1f });
        if (floats.Count > 4) floats.RemoveAt(0);

        // +N が昇っている間、水面が光の反射できらめき続ける
        glintFor = 45 + tier * 15;

        // 水も一緒に祝う: 中央がぼよんと跳ね、細波が立ち、水位が一瞬持ち上がる
        chop = Math.Min(1.6f, chop + 0.10f + 0.08f * tier);
        m2v -= 0.9f * tier;
        bobVel -= 0.30f * tier;

        // 水中から泡のキラキラが昇る（トークンが水に変わって注がれたイメージ）
        int bn = 4 + tier * 5;
        float wl = WaterLevel();
        float depth = Math.Max(8, Height - wl - 14);
        for (int i = 0; i < bn && parts.Count < 140; i++)
            parts.Add(new float[] {
                (float)(rng.NextDouble() * Width),
                wl + 6 + (float)(rng.NextDouble() * depth),
                0f,
                (float)(-(0.4 + rng.NextDouble() * 0.9)),
                0f, (float)(55 + rng.NextDouble() * 45),
                (float)(1.6 + rng.NextDouble() * 1.9),
                2f });                                   // type 2 = 泡キラ
    }

    /// <summary>水面に短命の光点を置く。y は毎フレーム波の高さから計算するので
    /// グリントは波に乗って上下する = 水のきらめきに見える。</summary>
    void SpawnGlint(int n)
    {
        for (int i = 0; i < n && parts.Count < 140; i++)
            parts.Add(new float[] {
                (float)(rng.NextDouble() * Width),          // x
                -(float)(rng.NextDouble() * 2.5),           // 水面からの浮き（y ではない）
                (float)((rng.NextDouble() - 0.5) * 0.5),    // ゆっくり横に流れる
                0f,
                0f, (float)(14 + rng.NextDouble() * 16),    // 短命: 0.5〜1 秒
                (float)(1.6 + rng.NextDouble() * 2.2),
                3f });                                       // type 3 = 水面グリント
    }

    void FxTick(object o, EventArgs e)
    {
        // ウィンドウの動きは常に追跡する（Big 以外で動かした分が
        // 切り替え直後に巨大な衝撃として乗らないように）。
        if (!haveLastLoc) { lastLoc = Location; haveLastLoc = true; }
        float dx = Location.X - lastLoc.X;
        float dy = Location.Y - lastLoc.Y;
        lastLoc = Location;

        if (Store.LayoutMode != Store.Layout.Big || !Visible
            || WindowState == FormWindowState.Minimized)
        {
            winVX = winVY = 0;
            return;
        }

        // 1 フレームの移動量を制限（モニタ間ジャンプ等の異常値を吸収）
        if (dx > 40) dx = 40; else if (dx < -40) dx = -40;
        if (dy > 40) dy = 40; else if (dy < -40) dy = -40;
        float ax = dx - winVX, ay = dy - winVY;   // 加速度
        winVX = dx; winVY = dy;

        // --- 水のモード物理 -------------------------------------------
        // 各モードは減衰振動子: v = v*減衰 - x*ω² + 外力。
        // ω²: m1 0.040 < m2 0.095 < m3 0.170（高いモードほど速い）
        // 減衰: m1 0.955 > m2 0.925 > m3 0.885（高いモードほど早く静まる）

        // 基本モード: 水平加速度で励起。右に加速→慣性で水が左に寄る
        m1v = m1v * 0.955f - m1 * 0.040f - ax * 0.85f;
        m1 += m1v;
        if (m1 > 30f) { m1 = 30f; m1v *= -0.35f; }      // 壁に当たって跳ね返る
        if (m1 < -30f) { m1 = -30f; m1v *= -0.35f; }

        // 返し波: 同じ外力で逆向きに少し立ち、速く収まる。
        // 基本モードだけだと「板が傾く」ように見えるのを崩す
        m3v = m3v * 0.885f - m3 * 0.170f + ax * 0.30f;
        m3 += m3v;
        if (m3 > 12f) m3 = 12f; else if (m3 < -12f) m3 = -12f;

        // 対称モード: 縦の加速度で励起（上下に揺すると中央がぼよんと跳ねる）
        m2v = m2v * 0.925f - m2 * 0.095f - ay * 0.55f - dy * 0.10f;
        m2 += m2v;
        if (m2 > 18f) { m2 = 18f; m2v *= -0.35f; }
        if (m2 < -18f) { m2 = -18f; m2v *= -0.35f; }

        // 水位全体の慣性: 窓を下げると水が取り残されて相対的に持ち上がる
        bobVel = bobVel * 0.90f - bob * 0.14f - dy * 0.22f - ay * 0.06f;
        bob += bobVel;
        if (bob > 16f) bob = 16f; else if (bob < -16f) bob = -16f;

        // 撹拌: 動かした勢いで細波が荒れ、止めると静まる
        chop += Math.Min(0.6f, (Math.Abs(dx) + Math.Abs(dy)) * 0.02f);
        chop *= 0.95f;
        if (chop > 1.6f) chop = 1.6f;

        // 水面のグリント（光の反射）。バースト中は湧き続け、
        // 揺すって波が荒れているときも自然に光る
        if (glintFor > 0)
        {
            glintFor--;
            SpawnGlint(tier >= 3 ? 2 : 1);
        }
        else if (chop > 0.3f && rng.NextDouble() < chop * 0.10)
        {
            SpawnGlint(1);
        }

        // 強い縦ジャークでしぶきが跳ねる。水面の山から上向きに飛び、重力で戻る
        float jolt = Math.Abs(ay) + Math.Abs(dy) * 0.5f;
        if (jolt > 6f && parts.Count < 140)   // 揺すり続けても粒子を溜め込みすぎない
        {
            float wl = WaterLevel() + bob;
            int n = Math.Min(12, (int)(jolt * 0.6f));
            for (int i = 0; i < n; i++)
            {
                float sx = (float)(rng.NextDouble() * Width);
                parts.Add(new float[] {
                    sx, wl - 2f,
                    (float)(rng.NextDouble() * 2.4 - 1.2) + winVX * 0.12f,
                    (float)(-(1.2 + rng.NextDouble() * 2.8)) - Math.Max(0, -dy) * 0.10f,
                    0f, (float)(40 + rng.NextDouble() * 25),
                    (float)(1.2 + rng.NextDouble() * 1.6),
                    1f });                                   // type 1 = しぶき
            }
        }

        waterPhase += 0.05 + punch * 0.5 + chop * 0.25;

        // スプリング補間: くるくる回りながら現在値に吸い付く
        vel = vel * 0.87 + (target - shown) * 0.095;   // 少し長く回してカウントアップを見せる
        shown += vel;
        // カウンタは絶対に逆走させない。バネが行き過ぎたら目標で止める
        // （行き過ぎ → 戻り は数字が「増えて減る」ように見えて気持ち悪い）
        if (shown > target) { shown = target; vel = 0; }
        if (Math.Abs(target - shown) < 0.6 && Math.Abs(vel) < 0.6) { shown = target; vel = 0; }

        if (punch > 0.001f) punch *= 0.88f; else punch = 0f;
        if (flash > 0.01f) flash *= 0.90f; else flash = 0f;
        if (shimmerT >= 0f) { shimmerT += 0.04f; if (shimmerT > 1f) shimmerT = -1f; }

        float surface = WaterLevel() + bob;
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var pt = parts[i];
            pt[0] += pt[2]; pt[1] += pt[3]; pt[4] += 1f;
            float ptype = pt.Length > 7 ? pt[7] : 0f;
            bool droplet = ptype == 1f;
            bool glint = ptype >= 3f;
            bool bubble = !glint && ptype >= 2f;
            if (glint) { pt[0] += pt[2]; pt[4] += 0f; }   // 波に乗るだけ（y は描画時に計算）
            else if (droplet) pt[3] += 0.22f;     // しぶきは重力で放物線を描く
            else if (bubble)
            {
                // 泡: 浮力で加速しつつ左右にゆらゆら
                pt[3] = Math.Max(pt[3] - 0.045f, -2.0f);
                pt[0] += (float)Math.Sin(pt[4] * 0.22 + i) * 0.4f;
            }
            else { pt[2] *= 0.93f; pt[3] = pt[3] * 0.93f - 0.035f; }  // キラ星は減速して浮き上がる
            bool dead = pt[4] >= pt[5];
            if (droplet && pt[3] > 0f && pt[1] > surface + 3f)
            {
                dead = true;                      // 着水。ごく小さく波を立てる
                chop = Math.Min(1.6f, chop + 0.02f);
            }
            if (bubble && pt[1] < surface + 2f)
            {
                dead = true;                      // 水面に到達して弾ける
                chop = Math.Min(1.6f, chop + 0.012f);
            }
            if (dead) parts.RemoveAt(i);
        }
        for (int i = floats.Count - 1; i >= 0; i--)
        {
            var f = floats[i];
            f[1] = (float)f[1] - 0.015f;
            if ((float)f[1] <= 0f) floats.RemoveAt(i);
        }
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // OnPaint が全面を塗るので既定の消去は不要。消すとちらつきの元になる。
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

    /// <summary>水槽の背景。5 時間制限の使用率が水位になり、水面が揺れる。</summary>
    float LevelFor(double pct)
    {
        float level = (float)(Height * (1.0 - Math.Min(pct, 100) / 100.0));
        return Math.Max(16, Math.Min(Height - 10, level));
    }

    /// <summary>手前の水面（5h）。泡・しぶきの基準はこちら。</summary>
    float WaterLevel()
    {
        double pct = 0;
        foreach (var x in samples) if (x.Key == "session") pct = x.Percent;
        return LevelFor(pct);
    }

    // 水槽は 2 層:
    //   奥  = 週間・全モデル（紫）… 長い窓なのでゆっくり・控えめに揺れる
    //   手前 = 5 時間制限（青）  … 今の窓。物理の主役
    void PaintWater(Graphics g)
    {
        double ses = 0, wk = 0;
        foreach (var x in samples)
        {
            if (x.Key == "session") ses = x.Percent;
            if (x.Key == "weekly_all") wk = x.Percent;
        }
        float lvW = LevelFor(wk);
        float lvS = LevelFor(ses);

        // ---- 奥: 週（紫）----
        DrawWaveFill(g, lvW, 3.4f, 0.034f, waterPhase * 0.6 + 1.3,
            Color.FromArgb(70, 150, 115, 235), Color.FromArgb(90, 55, 38, 130));
        DrawSurfaceLine(g, lvW, 3.4f, 0.034f, waterPhase * 0.6 + 1.3,
            Color.FromArgb(90, 200, 170, 255));

        // ---- 手前: 5h（青・二重塗りで深さを出す）----
        DrawWaveFill(g, lvS - 2, 4.0f, 0.040f, waterPhase * 0.7 + 2.1,
            Color.FromArgb(50, 96, 140, 235), Color.FromArgb(65, 30, 45, 110));
        DrawWaveFill(g, lvS, 3.2f, 0.045f, waterPhase,
            Color.FromArgb(95, 96, 155, 245), Color.FromArgb(120, 30, 52, 135));
        DrawSurfaceLine(g, lvS, 3.2f, 0.045f, waterPhase,
            Color.FromArgb(130, 170, 210, 255));
    }

    /// <summary>リセットまでの残り時間の割合 0..1。リセット直後 = 1、リセット時 = 0。</summary>
    static double ResetFrac(string resetsAt, double windowHours)
    {
        DateTime t;
        if (!DateTime.TryParse(resetsAt, null, DateTimeStyles.RoundtripKind, out t)) return -1;
        double remain = (t.ToLocalTime() - DateTime.Now).TotalHours;
        if (remain <= 0) return 0;
        return Math.Min(1.0, remain / windowHours);
    }

    /// <summary>画面の縁に沿う細いプログレスバー。残りぶんだけ左から塗る。</summary>
    void DrawEdgeBar(Graphics g, float y, double frac, Color c)
    {
        if (frac < 0) return;
        using (var track = new SolidBrush(Color.FromArgb(55, c)))
            g.FillRectangle(track, 1, y, Width - 2, 3);
        float w = (float)((Width - 2) * frac);
        if (w >= 1)
            using (var fill = new SolidBrush(Color.FromArgb(230, c)))
                g.FillRectangle(fill, 1, y, w, 3);
    }

    void DrawSurfaceLine(Graphics g, float level, float amp, float k, double phase, Color c)
    {
        using (var pen = new Pen(c, 1.2f))
        {
            var prev2 = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float px = 0, py = SurfaceY(level, 0, amp, k, phase);
            for (int x2 = 4; x2 <= Width; x2 += 4)
            {
                float y2 = SurfaceY(level, x2, amp, k, phase);
                g.DrawLine(pen, px, py, x2, y2);
                px = x2; py = y2;
            }
            g.SmoothingMode = prev2;
        }
    }

    float SurfaceY(float level, float x, float amp, float k, double phase)
    {
        float u = x / Width;                       // 0..1
        float a = amp * (1f + chop * 1.8f);
        return level + bob
            + m1 * (float)Math.Cos(Math.PI * u)          // 片側に寄る（曲面）
            + m2 * (float)Math.Cos(2 * Math.PI * u)      // 中央が跳ねる
            + m3 * (float)Math.Cos(3 * Math.PI * u)      // 細かい返し波
            + a * (float)Math.Sin(x * k + phase)
            + a * 0.55f * (float)Math.Sin(x * k * 2.6 - phase * 1.6);
    }

    void DrawWaveFill(Graphics g, float level, float amp, float k, double phase,
        Color top, Color bottom)
    {
        var pts = new List<PointF>();
        for (int x = 0; x <= Width; x += 6)
            pts.Add(new PointF(x, SurfaceY(level, x, amp, k, phase)));
        pts.Add(new PointF(Width, SurfaceY(level, Width, amp, k, phase)));
        pts.Add(new PointF(Width, Height));
        pts.Add(new PointF(0, Height));
        float rise = amp * 3 + Math.Abs(m1) + Math.Abs(m2) + Math.Abs(m3) + Math.Abs(bob) + 6;
        var rect = new RectangleF(0, Math.Max(0, level - rise), Width,
            Math.Max(1, Height - level + rise));
        using (var lg = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical))
        {
            var prev2 = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPolygon(lg, pts.ToArray());
            g.SmoothingMode = prev2;
        }
    }

    /// <summary>Big レイアウト。今日のトークン数がスプリングでカウントアップし、
    /// 増分に応じてパンチ・シマー・粒子・+N フロートが乗る。背景は水槽。</summary>
    void PaintBig(Graphics g)
    {
        double sesPct = 0, wkPct = 0, maxPct = 0;
        foreach (var x in samples)
        {
            if (x.Percent > maxPct) maxPct = x.Percent;
            if (x.Key == "session") sesPct = x.Percent;
            if (x.Key == "weekly_all") wkPct = x.Percent;
        }
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        using (var b = new SolidBrush(Theme.Mut))
        {
            var t = "TODAY  " + Store.UnitName;
            var sz = g.MeasureString(t, fT8);
            g.DrawString(t, fT8, b, (Width - sz.Width) / 2, 14);
        }

        long shownL = (long)Math.Round(shown);
        var tok = Store.Tokens(shownL);
        if (fitFont == null || fitLen != tok.Length)
        {
            if (fitFont != null && fitFont != fHuge) fitFont.Dispose();
            if (fitFontR != null && fitFontR != fHugeR) fitFontR.Dispose();
            var m = g.MeasureString(tok, fHuge);
            if (m.Width <= Width - 28)
            {
                fitFont = fHuge;
                fitFontR = fHugeR;
            }
            else
            {
                float size = fHuge.Size * (Width - 28) / m.Width;
                fitFont = new Font("Yu Gothic UI", size, FontStyle.Bold);
                fitFontR = new Font("Yu Gothic UI", size, FontStyle.Regular);
            }
            fitLen = tok.Length;
        }
        var tsz = g.MeasureString(tok, fitFont);
        float cx = Width / 2f, cy = Height / 2f - 8;

        // 数字だけ太字、単位 (K/M/G) はレギュラーで軽く見せる
        int sufAt = tok.Length;
        while (sufAt > 0 && char.IsLetter(tok[sufAt - 1])) sufAt--;
        string numPart = tok.Substring(0, sufAt);
        string sufPart = tok.Substring(sufAt);
        float wNum = g.MeasureString(numPart, fitFont).Width;
        float kern = fitFont.Size * 0.34f;    // MeasureString の余白ぶん詰める
        float wSuf = sufPart.Length > 0 ? g.MeasureString(sufPart, fitFontR).Width - kern : 0;
        float x0 = cx - (wNum + wSuf) / 2;
        float yTop = cy - tsz.Height / 2;

        var st = g.Save();
        float sc = 1f + punch;
        float shx = (tier >= 4 && punch > 0.02f)
            ? (float)(rng.NextDouble() * 3 - 1.5) : 0f;
        g.TranslateTransform(cx + shx, cy);
        g.ScaleTransform(sc, sc);
        g.TranslateTransform(-cx, -cy);

        // 本体（うっすら影を落として水と分離する）
        using (var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
        {
            g.DrawString(numPart, fitFont, b, x0 + 1.5f, yTop + 1.5f);
            if (sufPart.Length > 0)
                g.DrawString(sufPart, fitFontR, b, x0 + wNum - kern + 1.5f, yTop + 1.5f);
        }
        // 数字の色は状態で変わる:
        //   平常 = 白 / 使用率 70% 以上 = 橙 / 90% 以上 = 赤（逼迫が一目で分かる）
        //   カウントアップ中 = ミント緑がかる（増えている最中だと分かる）
        //   バースト直後 = 金色に光って戻る
        Color numCol = maxPct >= 90 ? Theme.Bad : maxPct >= 70 ? Theme.Warn : Theme.Fg;
        float counting = (float)Math.Min(1.0, Math.Abs(target - shown) / 4000.0);
        numCol = Lerp(numCol, Color.FromArgb(150, 235, 170), counting * 0.8f);
        numCol = Lerp(numCol, Color.FromArgb(255, 224, 120), flash);
        using (var b = new SolidBrush(numCol))
        {
            g.DrawString(numPart, fitFont, b, x0, yTop);
            if (sufPart.Length > 0)
                g.DrawString(sufPart, fitFontR, b, x0 + wNum - kern, yTop);
        }

        // シマー: ハイライト帯が数字の上を走る
        if (shimmerT >= 0f)
        {
            float bandW = Math.Max(24, tsz.Width * 0.55f);
            float bx = cx - tsz.Width / 2 - bandW + shimmerT * (tsz.Width + bandW * 2);
            var band = new RectangleF(bx, cy - tsz.Height / 2, bandW, tsz.Height);
            int a = 28 + tier * 16;
            var clip = g.Clip;
            g.SetClip(band, CombineMode.Intersect);
            using (var lg = new LinearGradientBrush(band,
                       Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                       LinearGradientMode.Horizontal))
            {
                var cb = new ColorBlend(3);
                cb.Colors = new[] { Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(a, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                cb.Positions = new[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = cb;
                g.DrawString(numPart, fitFont, lg, x0, yTop);
                if (sufPart.Length > 0)
                    g.DrawString(sufPart, fitFontR, lg, x0 + wNum - kern, yTop);
            }
            g.Clip = clip;
        }

        using (var b = new SolidBrush(Theme.Mut))
        {
            var t = "tokens";
            var sz = g.MeasureString(t, fT8);
            g.DrawString(t, fT8, b, (Width - sz.Width) / 2, cy + tsz.Height / 2 - 6);
        }

        // +N フロート: 1UP 風。出た瞬間が速く、減速しながらすーっと昇って消える
        foreach (var f in floats)
        {
            float life = (float)f[1];
            var t = (string)f[0];
            float rise = (float)(1 - Math.Pow(life, 0.6)) * 58f;   // ease-out で 58px 上昇
            float fy = cy - tsz.Height / 2 - 16 - rise;
            int a = (int)(255 * Math.Min(1f, life * 3f));          // 最後の 1/3 でフェード
            var sz = g.MeasureString(t, fF13);
            float fx2 = cx - sz.Width / 2;
            using (var b = new SolidBrush(Color.FromArgb(a * 2 / 3, 0, 0, 0)))
                g.DrawString(t, fF13, b, fx2 + 1.5f, fy + 1.5f);   // 影
            using (var b = new SolidBrush(Color.FromArgb(a, 150, 235, 170)))
                g.DrawString(t, fF13, b, fx2, fy);                 // 明るい緑
        }

        // 粒子（白・アクセント・金の 3 色でまたたく）
        float lvGlint = WaterLevel();
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < parts.Count; i++)
        {
            var pt = parts[i];
            float life = 1f - pt[4] / pt[5];
            float ptype = pt.Length > 7 ? pt[7] : 0f;
            if (ptype >= 3f)                       // 水面グリント: 波に乗る横長の光
            {
                float prog = pt[4] / pt[5];
                int ga = (int)(240 * Math.Sin(Math.PI * Math.Min(1f, prog)));
                if (ga < 8) continue;
                float gy = SurfaceY(lvGlint, pt[0], 3.2f, 0.045f, waterPhase) + pt[1];
                float gs = pt[6];
                // 水の反射は横に伸びる。縦は短く
                using (var pen = new Pen(Color.FromArgb(ga, 225, 245, 255), 1.3f))
                    g.DrawLine(pen, pt[0] - gs * 2.2f, gy, pt[0] + gs * 2.2f, gy);
                using (var pen = new Pen(Color.FromArgb(ga / 2, 225, 245, 255), 1f))
                    g.DrawLine(pen, pt[0], gy - gs * 0.7f, pt[0], gy + gs * 0.7f);
                using (var core = new SolidBrush(Color.FromArgb(Math.Min(255, ga + 15), 255, 255, 255)))
                    g.FillEllipse(core, pt[0] - gs * 0.4f, gy - gs * 0.4f, gs * 0.8f, gs * 0.8f);
                continue;
            }
            if (ptype == 1f)                       // しぶき
            {
                int da = (int)(255 * life);
                float dsz = pt[6] * (0.6f + 0.4f * life);
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, da)), 185, 220, 255)))
                    g.FillEllipse(b, pt[0] - dsz / 2, pt[1] - dsz / 2, dsz, dsz);
                continue;
            }
            if (ptype >= 2f)                       // 泡キラ: 水中で明滅しながら昇る
            {
                float btw = 0.5f + 0.5f * (float)Math.Sin(pt[4] * 0.5 + i * 1.7);
                int ba = (int)(220 * life * btw);
                if (ba < 6) continue;
                float bsz = pt[6];
                using (var glow2 = new SolidBrush(Color.FromArgb(ba / 4, 140, 225, 255)))
                    g.FillEllipse(glow2, pt[0] - bsz * 1.6f, pt[1] - bsz * 1.6f, bsz * 3.2f, bsz * 3.2f);
                using (var b = new SolidBrush(Color.FromArgb(ba, 160, 230, 255)))
                    g.FillEllipse(b, pt[0] - bsz / 2, pt[1] - bsz / 2, bsz, bsz);
                using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, ba + 30), 255, 255, 255)))
                    g.FillEllipse(b, pt[0] - bsz * 0.2f, pt[1] - bsz * 0.2f, bsz * 0.4f, bsz * 0.4f);
                continue;
            }
            // キラ星: 強くまたたく十字 + 中心のコア + うっすらグロー
            float tw = 0.45f + 0.55f * (float)Math.Sin(pt[4] * 1.1 + i);
            int a = (int)(255 * life * Math.Abs(tw));
            if (a < 6) continue;
            Color c = i % 3 == 0 ? Color.White
                : i % 3 == 1 ? Color.FromArgb(170, 190, 255) : Color.FromArgb(255, 215, 130);
            float r2 = pt[6] * (0.5f + 0.5f * life) * (0.8f + 0.4f * Math.Abs(tw));
            using (var glow = new SolidBrush(Color.FromArgb(a / 5, c)))
                g.FillEllipse(glow, pt[0] - r2 * 1.8f, pt[1] - r2 * 1.8f, r2 * 3.6f, r2 * 3.6f);
            using (var pen = new Pen(Color.FromArgb(a, c), 1.4f))
            {
                g.DrawLine(pen, pt[0] - r2 * 1.7f, pt[1], pt[0] + r2 * 1.7f, pt[1]);
                g.DrawLine(pen, pt[0], pt[1] - r2 * 1.7f, pt[0], pt[1] + r2 * 1.7f);
            }
            using (var core = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillEllipse(core, pt[0] - r2 * 0.55f, pt[1] - r2 * 0.55f, r2 * 1.1f, r2 * 1.1f);
        }
        g.SmoothingMode = prev;
        g.Restore(st);

        // 下段: コストと使用率（変換の外 = 揺らさない）
        DrawShadowed(g, Store.Money(today.Cost), fMid11, Theme.Fg, Height - 54);
        // 凡例は水の色と対応させる: ● 5h = 青、● 週 = 紫
        {
            string t1 = string.Format(CultureInfo.InvariantCulture, "● 5h {0:0}%", sesPct);
            string t2 = string.Format(CultureInfo.InvariantCulture, "● week {0:0}%", wkPct);
            var s1 = g.MeasureString(t1, fT8);
            var s2 = g.MeasureString(t2, fT8);
            float total = s1.Width + 10 + s2.Width;
            float lx = (Width - total) / 2;
            float ly = Height - 32;
            using (var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            {
                g.DrawString(t1, fT8, sh, lx + 1, ly + 1);
                g.DrawString(t2, fT8, sh, lx + s1.Width + 10 + 1, ly + 1);
            }
            using (var b = new SolidBrush(Color.FromArgb(235, 140, 190, 255)))
                g.DrawString(t1, fT8, b, lx, ly);
            using (var b = new SolidBrush(Color.FromArgb(235, 190, 160, 255)))
                g.DrawString(t2, fT8, b, lx + s1.Width + 10, ly);
        }

        if (!Store.RecorderAlive())
            using (var b = new SolidBrush(Theme.Bad))
                g.DrawString("● 記録停止中", fT8, b, 10, 8);

        // 縁のバー: 上端 = 5h リセットまでの残り（青）、下端 = 週（紫）。
        // 水の色と同じ系統。リセット直後は満タン、時間経過で左から縮む。
        double fS = -1, fW = -1;
        foreach (var x in samples)
        {
            if (x.Key == "session") fS = ResetFrac(x.ResetsAt, 5.0);
            if (x.Key == "weekly_all") fW = ResetFrac(x.ResetsAt, 168.0);
        }
        DrawEdgeBar(g, 2, fS, Color.FromArgb(120, 180, 255));
        DrawEdgeBar(g, Height - 5, fW, Color.FromArgb(190, 160, 255));
    }

    static Color Lerp(Color a, Color b, float t)
    {
        if (t <= 0f) return a;
        if (t > 1f) t = 1f;
        return Color.FromArgb(255,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>水面の上でも読めるよう、黒い影を敷いて中央揃えで描く。</summary>
    void DrawShadowed(Graphics g, string t, Font f, Color c, float y)
    {
        var sz = g.MeasureString(t, f);
        float x = (Width - sz.Width) / 2;
        using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            g.DrawString(t, f, b, x + 1, y + 1);
        using (var b = new SolidBrush(c))
            g.DrawString(t, f, b, x, y);
    }

    void DrawBorder(Graphics g)
    {
        // 1px の白い縁取り。角のドットが欠けないよう AA を切って描く。
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var p = new Pen(Theme.Border, 1f))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        g.SmoothingMode = prev;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

        if (Store.LayoutMode == Store.Layout.Big)
        {
            PaintWater(g);   // 背景の水槽（水位 = 5 時間制限）
            PaintBig(g);     // 数字と演出
            DrawBorder(g);   // 枠は最後。水に揺らされない
            return;
        }
        DrawBorder(g);

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
            g.DrawString("クリックで表示切替 / ドラッグで移動", f0, b0, 12, Height - 60);

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
