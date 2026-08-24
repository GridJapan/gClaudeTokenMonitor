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
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// csc はこの属性から Win32 バージョンリソースを合成する。タスクバーや
// タスクマネージャに出る表示名は AssemblyTitle (= FileDescription)。
// 無いと exe ファイル名「CtmMonitor」がそのまま表示されてしまう。
[assembly: System.Reflection.AssemblyTitle("gClaudeTokenMonitor")]
[assembly: System.Reflection.AssemblyProduct("gClaudeTokenMonitor")]
[assembly: System.Reflection.AssemblyDescription("Claude Code token usage monitor")]
[assembly: System.Reflection.AssemblyCompany("GridJapan")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 GridJapan")]
[assembly: System.Reflection.AssemblyVersion("0.2.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.2.3.0")]

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
            // zip 配布は「人間が起動する CtmMonitor.exe だけを直下に置き、
            // CLI の ctm.exe は bin\ に隔離する」構成（誤ダブルクリック防止）。
            // ソースビルドは両方 bin\ に並ぶ。同じフォルダ → bin\ → PATH の順に探す。
            string dir = Path.GetDirectoryName(Application.ExecutablePath) ?? ".";
            string local = Path.Combine(dir, "ctm.exe");
            if (File.Exists(local)) return local;
            string sub = Path.Combine(dir, "bin", "ctm.exe");
            return File.Exists(sub) ? sub : "ctm.exe";
        }
    }

    /// <summary>Claude Code の構成ディレクトリ（CLAUDE_CONFIG_DIR を尊重）。</summary>
    public static string ClaudeDir
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(v)) return v;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }
    }

    static int envState = -1;
    static DateTime envAt;

    /// <summary>Claude Code (CLI) の導入状態。0=OK / 1=projects が無い（CLI 未導入。
    /// Claude デスクトップアプリだけの PC など）/ 2=projects はあるが credentials が
    /// 無い（未ログイン＝使用率だけ取れない）。窓の警告表示用。10 秒キャッシュ。</summary>
    public static int EnvState
    {
        get
        {
            if (envState >= 0 && (DateTime.Now - envAt).TotalSeconds < 10) return envState;
            envAt = DateTime.Now;
            try
            {
                if (!Directory.Exists(Path.Combine(ClaudeDir, "projects"))) envState = 1;
                else if (!File.Exists(Path.Combine(ClaudeDir, ".credentials.json"))) envState = 2;
                else envState = 0;
            }
            catch { envState = 0; }
            return envState;
        }
    }

    static string acctBadge;
    static DateTime acctBadgeAt;

    /// <summary>明示選択中の使用率アカウントの表示名（auto のときは ""）。
    /// コンパクト窓のヘッダに出す。10 秒キャッシュ。</summary>
    public static string AcctBadge
    {
        get
        {
            if (acctBadge != null && (DateTime.Now - acctBadgeAt).TotalSeconds < 10)
                return acctBadge;
            acctBadgeAt = DateTime.Now;
            acctBadge = "";
            try
            {
                var sel = Str(ReadAllTextShared(Path.Combine(Root, "account.json")), "selected");
                if (sel.Length > 0 && sel != "auto")
                {
                    var l = Str(ReadAllTextShared(
                        Path.Combine(Root, "accounts", sel + ".json")), "label");
                    acctBadge = l.Length > 24 ? l.Substring(0, 23) + "…" : l;
                }
            }
            catch { }
            return acctBadge;
        }
    }

    /// <summary>アカウント切替直後にキャッシュを捨てて即反映させる。</summary>
    public static void PokeAcctBadge() { acctBadgeAt = DateTime.MinValue; }

    // 依存を増やさないための最小 JSON 読み取り。ctm の出力は純 ASCII の 1 行 1 レコード。
    public static string Str(string line, string key)
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

    /// <summary>選択中の期間の窓開始。Today は null。5h / week は公式リセット時刻
    /// （limits サンプル）から逆算する。サンプル未取得なら null（= 今日扱い）。</summary>
    // リセット直後は API が resets_at を空で返す期間がある。最後に見えた
    // 有効なリセット時刻を覚えておき、周期で現在の窓まで繰り上げて補完する。
    static DateTime lastReset5h = DateTime.MinValue;
    static DateTime lastResetWeek = DateTime.MinValue;

    public static DateTime PeriodStart(List<Sample> samples)
    {
        bool week = PeriodMode == Period.Week;
        string key = week ? "weekly_all" : "session";
        double hours = week ? 168 : 5;
        foreach (var x in samples)
        {
            if (x.Key != key) continue;
            DateTime t;
            if (DateTime.TryParse(x.ResetsAt, null, DateTimeStyles.RoundtripKind, out t))
            {
                var loc = t.ToLocalTime();
                if (week) lastResetWeek = loc; else lastReset5h = loc;
            }
        }
        var reset = week ? lastResetWeek : lastReset5h;
        if (reset == DateTime.MinValue)
            return DateTime.Now.AddHours(-hours);   // 一度も取れていない間の近似
        // 覚えているリセット時刻を、現在を含む窓の終端まで周期で合わせる
        while (reset <= DateTime.Now) reset = reset.AddHours(hours);
        while (reset.AddHours(-hours) > DateTime.Now) reset = reset.AddHours(-hours);
        return reset.AddHours(-hours);
    }

    // 過去日の行（ts, tok, cost）。日付をまたぐ窓の合計に使う。過去日のファイルは
    // もう増えないので一度読めば十分（サイズ変化で再読）。
    class DayRows { public long Size; public List<object[]> Rows = new List<object[]>(); }
    static readonly Dictionary<string, DayRows> dayCache = new Dictionary<string, DayRows>();

    static List<object[]> RowsFor(DateTime day)
    {
        string k = day.ToString("yyyy-MM-dd");
        string path = EventsPath(day);
        long size = 0;
        try { var fi = new FileInfo(path); size = fi.Exists ? fi.Length : 0; } catch { }
        DayRows c;
        if (dayCache.TryGetValue(k, out c) && c.Size == size) return c.Rows;
        c = new DayRows { Size = size };
        foreach (var line in ReadLines(path))
        {
            DateTime ts;
            if (!DateTime.TryParse(Str(line, "ts"), null, DateTimeStyles.RoundtripKind, out ts))
                continue;
            c.Rows.Add(new object[] { ts.ToLocalTime(), (long)Num(line, "total"), Num(line, "cost_usd") });
        }
        dayCache[k] = c;
        return c.Rows;
    }

    /// <summary>start 以降の合計トークン / コスト。start の日から今日までを走る。</summary>
    public static void SumSince(DateTime start, out long tok, out double cost)
    {
        tok = 0; cost = 0;
        for (var day = start.Date; day <= DateTime.Now.Date; day = day.AddDays(1))
        {
            foreach (var r in RowsFor(day))
            {
                if ((DateTime)r[0] < start) continue;
                tok += (long)r[1];
                cost += (double)r[2];
            }
        }
    }

    // ---- 200ms ポーリング用の増分読み --------------------------------
    // 全量パース（数 MB）を 5 回/秒やると CPU を無駄に食うので、
    // 前回読んだバイト位置を覚えて追記分だけ集計する。
    static long liveOff;
    static List<string> lastSources = new List<string>();

    /// <summary>直近の増分を出した作業ディレクトリ（1 回読むとクリア）。</summary>
    public static string TakeLastSources()
    {
        if (lastSources.Count == 0) return "";
        string src = lastSources[0];
        if (lastSources.Count > 1) src += " ほか" + (lastSources.Count - 1);
        lastSources = new List<string>();
        return src;
    }
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
            var srcs = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length < 3) continue;
                liveTot.Messages++;
                liveTot.Tokens += (long)Num(line, "total");
                liveTot.Cost += Num(line, "cost_usd");
                var sid = Str(line, "session");
                if (sid.Length > 0) liveTot.Sessions.Add(sid);
                // 増分の発生源 = 作業ディレクトリのみ（セッション ID は人が読めないので出さない）
                var cw = Str(line, "cwd_name");
                if (cw.Length > 0 && !srcs.Contains(cw)) srcs.Add(cw);
            }
            if (srcs.Count > 0) lastSources = srcs;
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

    static bool aliveCached;
    static DateTime aliveCheckedAt;
    static DateTime aliveLastTrue;

    /// <summary>常駐レコーダーの生存判定。30fps の描画から呼ばれるため 1 秒に
    /// 1 回しか実チェックせず、ロック書き換え中の一瞬の読み失敗では落とさない
    /// （8 秒のグレース）。本当に止まったときだけ「記録停止中」が点く。</summary>
    public static bool RecorderAlive()
    {
        var now = DateTime.Now;
        if ((now - aliveCheckedAt).TotalMilliseconds < 1000) return aliveCached;
        aliveCheckedAt = now;
        if (RecorderAliveRaw()) aliveLastTrue = now;
        aliveCached = (now - aliveLastTrue).TotalSeconds < 8;
        return aliveCached;
    }

    static DateTime lastRestartTry = DateTime.MinValue;

    /// <summary>起動直後の再起動抑止。レコーダーを起こした側が呼ぶ。</summary>
    public static void NoteRestart() { lastRestartTry = DateTime.Now; }

    /// <summary>クラッシュ監視。判定は必ず生の状態（キャッシュ無し）で行う —
    /// 1 秒キャッシュのせいで、起こした直後の健康なレコーダーを「死」と誤認して
    /// 殺す事故が起きるため。Kill はプロセス名が ctm のときだけ（PID 再利用対策）。</summary>
    public static void Supervise()
    {
        if (RecorderAliveRaw()) { aliveLastTrue = DateTime.Now; aliveCached = true; return; }
        if ((DateTime.Now - lastRestartTry).TotalSeconds < 30) return;   // 再試行は 30 秒間隔
        lastRestartTry = DateTime.Now;

        string lockp = Path.Combine(Root, "record.lock");
        if (!File.Exists(lockp))
        {
            // ロックが無い = 正常停止 or 初回起動失敗。アプリが生きている以上、
            // 記録は動いているべきなので起こし直す（完全に止めるにはアプリを終了）
            LogCrash("detect: レコーダー停止（ロック無し）— 起動する");
            Run("record -quiet", false);
            return;
        }

        string flat = ReadAllTextShared(lockp).Replace(" ", "");
        int pid = (int)Num(flat, "pid");
        LogCrash(string.Format("detect: heartbeat 応答なし (pid={0})", pid));
        try
        {
            var pr = Process.GetProcessById(pid);
            if (!pr.HasExited)
            {
                // PID は OS が再利用する。ctm 以外なら絶対に殺さない
                if (pr.ProcessName.Equals("ctm", StringComparison.OrdinalIgnoreCase))
                {
                    pr.Kill();
                    pr.WaitForExit(5000);   // 掴んでいるロックが解放されるのを待つ
                    LogCrash("kill: ハング中の ctm (pid " + pid + ") を強制終了した");
                }
                else
                {
                    LogCrash("skip: pid " + pid + " は " + pr.ProcessName + "（PID 再利用）— 殺さずロックの失効を待つ");
                    return;
                }
            }
        }
        catch { /* プロセス消滅 = クラッシュ。そのまま再起動へ */ }
        Run("record -quiet", false);
        LogCrash("restart: ctm record を再起動した");
    }

    public static void LogCrash(string msg)
    {
        try
        {
            Directory.CreateDirectory(Root);   // 初回起動の失敗時はまだ無い。証跡を失わない
            File.AppendAllText(Path.Combine(Root, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [UI] " + msg + "\r\n");
        }
        catch { }
    }

    static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    static bool RecorderAliveRaw()
    {
        string p = Path.Combine(Root, "record.lock");
        if (!File.Exists(p)) return false;
        try
        {
            string txt = ReadAllTextShared(p);
            if (txt.Length == 0) return false;
            DateTime hb;
            var flat = txt.Replace("\n", "").Replace(" ", "");
            if (!DateTime.TryParse(Str(flat, "heartbeat"),
                    null, DateTimeStyles.RoundtripKind, out hb)) return false;
            if ((DateTime.Now - hb.ToLocalTime()).TotalMinutes >= 3) return false;
            // heartbeat が新しくてもプロセスが消えていればクラッシュ。
            // これを見ないと落ちてから 3 分間「稼働中」と誤認して復旧が遅れる
            return PidAlive((int)Num(flat, "pid"));
        }
        catch { return false; }
    }

    /// <summary>直近の ctm 起動失敗の理由。成功すれば空に戻る。</summary>
    public static string StartError = "";

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
            StartError = "";
        }
        catch (Exception ex)
        {
            // Smart App Control / SmartScreen のブロックや exe 不在はここに来る。
            // 黙殺するとレコーダー無しのまま動き続けるので、証跡と表示用に残す
            StartError = ex.Message;
            LogCrash("start: " + CtmExe + " " + args + " — " + ex.Message);
        }
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

    /// <summary>ウィンドウ倍率。1.0 = 大 (240px)、0.5 = 小 (120px)。
    /// 描画は常に論理 240x240 で行い、ScaleTransform で縮尺するので、
    /// ここに任意の倍率を足すだけで全ロジックが追従する。</summary>
    public static float WinScale = 1f;

    /// <summary>Big レイアウトの数字が何の合計か。5h / week は公式リセット時刻の
    /// 窓に一致するので、リセットの瞬間に数字も 0 から数え直しになる。</summary>
    public enum Period { FiveH, Week }

    public static Period PeriodMode = Period.FiveH;

    /// <summary>ATOMS3R サブモニタ（USB・表示専用）へ送信するか。</summary>
    public static bool AtomEnabled;

    public static string PeriodLabel
    {
        get { return PeriodMode == Period.Week ? "WEEK" : "5H"; }
    }

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
            WinScale = Str(txt, "size") == "S" ? 0.5f : 1f;
            PeriodMode = Str(txt, "period") == "week" ? Period.Week : Period.FiveH;
            AtomEnabled = Str(txt, "atom") == "on";
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
                "{{\"unit\":\"{0}\",\"x\":{1},\"y\":{2},\"layout\":\"{3}\",\"size\":\"{4}\",\"period\":\"{5}\",\"atom\":\"{6}\"}}",
                UnitName, WindowPos.X, WindowPos.Y, LayoutMode,
                WinScale < 0.75f ? "S" : "L",
                PeriodMode == Period.Week ? "week" : "5h",
                AtomEnabled ? "on" : "off"));
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

/// <summary>exe に同梱する ATOM ファームウェア一式（bin\atom-fw\）。
/// build.ps1 が atom のビルド出力と fw.json（バージョン）をここへ詰める。
/// 無ければ自動更新は静かに無効になるだけで、他機能に影響しない。</summary>
static class AtomFw
{
    public static string Dir
    {
        get
        {
            return Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath) ?? ".", "atom-fw");
        }
    }

    public static string App        { get { return Path.Combine(Dir, "firmware.bin"); } }
    public static string Bootloader { get { return Path.Combine(Dir, "bootloader.bin"); } }
    public static string Partitions { get { return Path.Combine(Dir, "partitions.bin"); } }
    public static string BootApp0   { get { return Path.Combine(Dir, "boot_app0.bin"); } }

    /// <summary>fw.json の "ver"。読めなければ null（= 同梱なし）。</summary>
    public static string Ver
    {
        get
        {
            try
            {
                string j = File.ReadAllText(Path.Combine(Dir, "fw.json"));
                int i = j.IndexOf("\"ver\"", StringComparison.Ordinal);
                if (i < 0) return null;
                i = j.IndexOf('"', j.IndexOf(':', i) + 1);
                int e = j.IndexOf('"', i + 1);
                return e > i ? j.Substring(i + 1, e - i - 1) : null;
            }
            catch { return null; }
        }
    }

    public static bool Available { get { return Ver != null && File.Exists(App); } }

    /// <summary>"0.2.1" 形式の比較。a &lt; b なら負。</summary>
    public static int Compare(string a, string b)
    {
        var pa = (a ?? "").Split('.');
        var pb = (b ?? "").Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int na, nb;
            int.TryParse(i < pa.Length ? pa[i] : "0", out na);
            int.TryParse(i < pb.Length ? pb[i] : "0", out nb);
            if (na != nb) return na - nb;
        }
        return 0;
    }
}

/// <summary>ESP32-S3 の ROM シリアルブートローダーを直接話す最小クライアント。
/// esptool を同梱しないための自前実装（依存ゼロ維持）。SLIP フレーミングで
/// SYNC → SPI_ATTACH → FLASH_BEGIN/DATA/END を送り、SPI_FLASH_MD5 で検証する。
/// スタブは使わない（platformio.ini の --no-stub と同じ・実機実績のある経路）。
/// USB-Serial/JTAG では DTR/RTS がチップの BOOT/EN 操作にエミュレートされる。</summary>
class EspRom : IDisposable
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CommTimeouts { public uint RI, RM, RC, WM, WC; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommTimeouts(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, ref CommTimeouts t);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EscapeCommFunction(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, uint fn);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, IntPtr overlapped);

    const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;

    Microsoft.Win32.SafeHandles.SafeFileHandle h;
    FileStream fs;

    /// <summary>ブロック中の Win32 I/O を強制解除して以後の操作を失敗させる。
    /// デバイスの USB がウェッジすると WriteFile がタイムアウトを無視して
    /// 無期限ブロックすることがある（実機で 74% 停止を再現）。その脱出口。</summary>
    public void Abort()
    {
        try { CancelIoEx(h, IntPtr.Zero); } catch { }
        try { fs.Dispose(); } catch { }
    }

    public static EspRom Open(string port)
    {
        var h = CreateFile("\\\\.\\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var to = new CommTimeouts { RI = 10, RM = 0, RC = 100, WM = 0, WC = 1000 };
        SetCommTimeouts(h, ref to);
        return new EspRom { h = h, fs = new FileStream(h, FileAccess.ReadWrite, 4096, false) };
    }

    public void Dispose()
    {
        try { fs.Dispose(); } catch { }
    }

    void Line(uint fn) { try { EscapeCommFunction(h, fn); } catch { } }

    bool dtrState;

    void SetDtr(bool on) { dtrState = on; Line(on ? SETDTR : CLRDTR); }

    /// <summary>RTS を変えたあと、DTR を同じ値で叩き直す。Windows の usbser.sys は
    /// RTS 単独ではラインステート要求を送らず、DTR の変化に便乗させないと
    /// デバイスに届かない（esptool の _setRTS と同じ回避策）。これを省くと
    /// リセットが一切効かず、チップが ROM モードに残って無反応になる。</summary>
    void SetRts(bool on)
    {
        Line(on ? SETRTS : CLRRTS);
        Line(dtrState ? SETDTR : CLRDTR);
    }

    /// <summary>チップをリセットしてアプリを起動させる（書き込み完了後に呼ぶ）。
    /// USB-Serial/JTAG のエミュレーションでは、リセットは DTR=0 の組でしか掛からず、
    /// DTR=0 のまま EN を解除すると boot:0x10 (DOWNLOAD) に落ちる（実機で確認）。
    /// 解除の直前に DTR=1 へ上げて GPIO0 を通常起動側に倒すのが正解
    /// （この手順でアプリが起動し hello が返ることを実機で検証済み）。
    /// リセットで USB ごと再列挙されるため、このハンドルは以後使えない。</summary>
    public void HardReset()
    {
        SetDtr(false);
        SetRts(true);                        // EN -> LOW（リセット保持）
        System.Threading.Thread.Sleep(150);
        SetDtr(true);                        // GPIO0 -> High（通常起動側）
        SetRts(false);                       // EN 解除 -> アプリ起動
        System.Threading.Thread.Sleep(200);
        SetDtr(false);                       // Idle へ戻す
    }

    /// <summary>USB-Serial/JTAG 経由で ROM ダウンロードモードへ入れる
    /// （esptool の USBJTAGSerialReset と同一手順）。</summary>
    public void EnterBootloader()
    {
        SetRts(false);
        SetDtr(false);                       // Idle
        System.Threading.Thread.Sleep(100);
        SetDtr(true);                        // IO0 を設定
        SetRts(false);
        System.Threading.Thread.Sleep(100);
        SetRts(true);                        // リセット。(0,0) を避けて (1,1) を通す
        SetDtr(false);
        SetRts(true);                        // Windows は RTS 設定時にだけ DTR を伝える
        System.Threading.Thread.Sleep(100);
        SetDtr(false);
        SetRts(false);                       // リセット解除
    }

    void WriteSlip(byte[] p)
    {
        var ms = new MemoryStream(p.Length + 16);
        ms.WriteByte(0xC0);
        foreach (var b in p)
        {
            if (b == 0xC0) { ms.WriteByte(0xDB); ms.WriteByte(0xDC); }
            else if (b == 0xDB) { ms.WriteByte(0xDB); ms.WriteByte(0xDD); }
            else ms.WriteByte(b);
        }
        ms.WriteByte(0xC0);
        var a = ms.ToArray();
        fs.Write(a, 0, a.Length);
        fs.Flush();
    }

    readonly byte[] rxChunk = new byte[512];
    int rxHave, rxPos;

    // .NET Framework 4.x に TickCount64 は無いので UTC ミリ秒で代用
    static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    int NextByte()
    {
        if (rxPos >= rxHave)
        {
            rxHave = fs.Read(rxChunk, 0, rxChunk.Length);   // COMMTIMEOUTS で ~100ms 上限
            rxPos = 0;
            if (rxHave <= 0) return -1;
        }
        return rxChunk[rxPos++];
    }

    byte[] ReadFrame(int timeoutMs)
    {
        long deadline = NowMs() + timeoutMs;
        var frame = new List<byte>(1200);
        bool inFrame = false, esc = false;
        while (NowMs() < deadline)
        {
            int c = NextByte();
            if (c < 0) continue;
            byte b = (byte)c;
            if (!inFrame)
            {
                if (b == 0xC0) { inFrame = true; frame.Clear(); }
                continue;
            }
            if (esc)
            {
                frame.Add(b == 0xDC ? (byte)0xC0 : b == 0xDD ? (byte)0xDB : b);
                esc = false;
                continue;
            }
            if (b == 0xDB) { esc = true; continue; }
            if (b == 0xC0)
            {
                if (frame.Count == 0) continue;   // 連続デリミタは読み流す
                return frame.ToArray();
            }
            frame.Add(b);
        }
        return null;
    }

    public void Drain(int ms)
    {
        long deadline = NowMs() + ms;
        while (NowMs() < deadline) if (NextByte() < 0) break;
        rxHave = rxPos = 0;
    }

    byte[] Command(byte op, byte[] payload, uint chk, int timeoutMs)
    {
        var req = new byte[8 + payload.Length];
        req[1] = op;
        req[2] = (byte)(payload.Length & 0xFF);
        req[3] = (byte)(payload.Length >> 8);
        BitConverter.GetBytes(chk).CopyTo(req, 4);
        payload.CopyTo(req, 8);
        WriteSlip(req);
        long deadline = NowMs() + timeoutMs;
        while (NowMs() < deadline)
        {
            var f = ReadFrame((int)Math.Max(1, deadline - NowMs()));
            if (f == null) return null;
            if (f.Length < 10 || f[0] != 0x01 || f[1] != op) continue;   // 無関係フレームは捨てる
            var data = new byte[f.Length - 8];
            Array.Copy(f, 8, data, 0, data.Length);
            return data;
        }
        return null;
    }

    static bool Ok(byte[] data)
    {
        // 末尾がステータス（S3 ROM は 4 バイト、先頭 0 = 成功）
        return data != null && data.Length >= 4 && data[data.Length - 4] == 0;
    }

    public bool Sync()
    {
        var p = new byte[36];
        p[0] = 0x07; p[1] = 0x07; p[2] = 0x12; p[3] = 0x20;
        for (int i = 4; i < 36; i++) p[i] = 0x55;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var d = Command(0x08, p, 0, 500);
            if (d != null) { Drain(300); return true; }
            System.Threading.Thread.Sleep(100);
        }
        return false;
    }

    public bool SpiAttach()
    {
        return Ok(Command(0x0D, new byte[8], 0, 3000));
    }

    bool FlashBegin(uint size, uint blocks, uint blockSize, uint offset, int timeoutMs)
    {
        var p = new byte[20];   // S3 ROM は 5 引数目に encrypted=0
        BitConverter.GetBytes(size).CopyTo(p, 0);
        BitConverter.GetBytes(blocks).CopyTo(p, 4);
        BitConverter.GetBytes(blockSize).CopyTo(p, 8);
        BitConverter.GetBytes(offset).CopyTo(p, 12);
        return Ok(Command(0x02, p, 0, timeoutMs));
    }

    bool FlashData(byte[] block, uint seq)
    {
        var p = new byte[16 + block.Length];
        BitConverter.GetBytes((uint)block.Length).CopyTo(p, 0);
        BitConverter.GetBytes(seq).CopyTo(p, 4);
        block.CopyTo(p, 16);
        uint chk = 0xEF;
        foreach (var b in block) chk ^= b;
        return Ok(Command(0x03, p, chk, 6000));
    }

    string FlashMd5(uint addr, uint size, int timeoutMs)
    {
        var p = new byte[16];
        BitConverter.GetBytes(addr).CopyTo(p, 0);
        BitConverter.GetBytes(size).CopyTo(p, 4);
        var d = Command(0x13, p, 0, timeoutMs);
        if (!Ok(d)) return null;
        int n = d.Length - 4;
        if (n >= 32) return Encoding.ASCII.GetString(d, 0, 32).ToLowerInvariant();
        if (n >= 16)
        {
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(d[i].ToString("x2"));
            return sb.ToString();
        }
        return null;
    }

    /// <summary>image を offset へ書き、MD5 で検証する。erase 込み。
    /// 失敗理由は err に入る（crash.log に出して原因究明に使う）。
    /// 進捗が 75 秒止まったらウォッチドッグが Abort して抜ける（74% 停止の再発防止。
    /// コマンド応答のタイムアウトでは、送信側の WriteFile ブロックを救えないため）。</summary>
    public bool WriteRegion(byte[] image, uint offset, Action<int> progress, out string err)
    {
        err = "";
        const uint BS = 0x400;   // ROM ローダーのブロックサイズ
        uint blocks = (uint)((image.Length + BS - 1) / BS);

        int beat = 0;
        bool watchdogStop = false;
        var wd = new System.Threading.Thread(delegate ()
        {
            int last = -1;
            long lastChange = NowMs();
            while (!watchdogStop)
            {
                System.Threading.Thread.Sleep(1000);
                if (beat != last) { last = beat; lastChange = NowMs(); }
                else if (NowMs() - lastChange > 75000) { Abort(); return; }
            }
        });
        wd.IsBackground = true;
        wd.Start();

        try
        {
            // FLASH_BEGIN は領域消去を同期で行うので応答が遅い（サイズ比例）
            if (!FlashBegin((uint)image.Length, blocks, BS, offset, 60000)) { err = "flash_begin 失敗"; return false; }
            beat++;
            for (uint i = 0; i < blocks; i++)
            {
                var b = new byte[BS];
                int off = (int)(i * BS);
                int n = Math.Min((int)BS, image.Length - off);
                Array.Copy(image, off, b, 0, n);
                for (int k = n; k < (int)BS; k++) b[k] = 0xFF;
                if (!FlashData(b, i)) { err = "flash_data seq=" + i + " 失敗"; return false; }
                beat++;
                if (progress != null && (i % 32 == 0 || i == blocks - 1))
                    progress((int)((i + 1) * 100 / blocks));
            }
            string want;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var sb = new StringBuilder(32);
                foreach (var x in md5.ComputeHash(image)) sb.Append(x.ToString("x2"));
                want = sb.ToString();
            }
            // FLASH_END (0x04) は送らない。esptool も送っておらず（トレースで確認）、
            // ROM ローダーに投げると失敗が返る。MD5 が一致した時点で書き込みは完了。
            var got = FlashMd5(offset, (uint)image.Length, 30000);
            if (got != want) { err = "md5 不一致 got=" + (got ?? "null") + " want=" + want; return false; }
            return true;
        }
        catch (Exception ex)
        {
            err = "I/O 中断: " + ex.Message;   // ウォッチドッグの Abort もここに出る
            return false;
        }
        finally { watchdogStop = true; }
    }
}

/// <summary>ATOMS3R サブモニタ（USB CDC・表示専用）への送信。
/// 200ms ごとの Reload から状態 1 行 (NDJSON) を書くだけで、描画・演出・
/// 回転はデバイス側 (atom/) が 30fps で行う。接続はバックグラウンドで探す:
/// レジストリの Espressif (VID_303A) エントリから COM 番号を集め、
/// ping に "ctm-atom" と答えたポートだけを採用する（他の機器を荒らさない）。
///
/// .NET SerialPort は使わない。ESP32-S3 の USB-Serial/JTAG は open 時の
/// 制御線変化で**チップごとリセットされ**（USB の再列挙はしない）、その際の
/// 中断イベントで SerialPort は内部的に壊れて以後 "port is closed" を吐き
/// 続ける（実機で確認）。素の Win32 ハンドル + COMMTIMEOUTS なら無事。
/// 接続手順: 1 回目の open（リセットを誘発）→ 閉じて 3 秒待つ（再起動完了）
/// → 2 回目の open（制御線が同じ値なのでリセットされない）→ ping/hello 確認。</summary>
static class SubMon
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CommTimeouts { public uint RI, RM, RC, WM, WC; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommTimeouts(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, ref CommTimeouts t);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EscapeCommFunction(
        Microsoft.Win32.SafeHandles.SafeFileHandle h, uint fn);

    const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;

    static FileStream stream;                 // 接続中のポート（null = 未接続）
    static readonly object gate = new object();
    static volatile bool connecting;
    static DateTime lastAttempt = DateTime.MinValue;
    static string pendingSrc = "";

    public static string Status = "未接続";

    /// <summary>hello が申告したデバイス側ファームのバージョン。</summary>
    public static string DeviceFw = "";

    /// <summary>接続中のポート名。メニューからの手動更新で使う。</summary>
    static string connectedPort = "";

    public static bool Connected
    {
        get { lock (gate) return stream != null; }
    }

    /// <summary>接続中で、同梱ファームがデバイスより新しい（＝更新できる）か。
    /// メニューの「ATOM ファームを更新」を有効にするかの判定に使う。</summary>
    public static bool UpdateAvailable
    {
        get
        {
            lock (gate) if (stream == null) return false;
            return AtomFw.Available && DeviceFw.Length > 0
                && AtomFw.Compare(DeviceFw, AtomFw.Ver) < 0;
        }
    }

    /// <summary>直近の探索で「ポートは見えるのに ctm-atom が応答しなかった」ポート名。
    /// 新品・別ファーム・起動不能のどれか＝手動書き込みの正当な対象。</summary>
    static volatile string blankPort = "";

    /// <summary>検知済みの書き込み対象ポート名（無ければ ""）。確認ダイアログの表示用。</summary>
    public static string BlankPort { get { return blankPort; } }

    /// <summary>「ATOM にファームを書き込む」を有効にするか。デバイスは Windows に
    /// 見えているのに本アプリのファームが応答しない、と探索が検知したときだけ true。
    /// 接続中（＝アプリ入り。更新は UpdateNow の担当）や未接続時は false。</summary>
    public static bool FlashTarget
    {
        get
        {
            if (Connected || flashing || updating || resetting) return false;
            string p = blankPort;
            if (p.Length == 0) return false;
            try
            {
                foreach (var n in SerialPort.GetPortNames())
                    if (n == p) return true;   // 検知した個体がまだ挿さっている
            }
            catch { }
            return false;
        }
    }

    /// <summary>バルーン通知。TrayApp が UI スレッドへマーシャリングして差し込む。</summary>
    public static Action<string, string, bool> Notify = delegate { };

    /// <summary>書き込みモーダルの制御。TrayApp が実装を差す。
    /// Begin(タイトル) → Progress(0..100,文言) 多数 → End(成功か)。
    /// 書き込み中は「ケーブルを抜かないでください」を出し、UI 操作を塞ぐ。</summary>
    public static Action<string> FlashBegin = delegate { };
    public static Action<int, string> FlashProgress = delegate { };
    public static Action<bool, string> FlashEnd = delegate { };

    /// <summary>バースト時の発生源ディレクトリ。次の 1 行にだけ載せる。</summary>
    public static void NoteBurst(string src)
    {
        if (!string.IsNullOrEmpty(src)) pendingSrc = src;
    }

    public static void Tick(string per, long tok, double pct5, double pctw,
        double f5, double fw, double cost, bool rec)
    {
        if (!Store.AtomEnabled) { Shutdown(); return; }
        // 書き込み・更新・USB リセット中は、勝手に再接続してポートを掴まない
        // （掴むと ROM 書き込み側が Open できず「書き込みモードに入れず」になる）。
        if (flashing || updating || resetting) return;
        FileStream s;
        lock (gate) s = stream;
        if (s == null)
        {
            if (!connecting && (DateTime.Now - lastAttempt).TotalSeconds > 3)
            {
                connecting = true;
                lastAttempt = DateTime.Now;
                var th = new System.Threading.Thread(Discover);
                th.IsBackground = true;
                th.Start();
            }
            return;
        }
        string src = pendingSrc;
        pendingSrc = "";
        var ic = CultureInfo.InvariantCulture;
        string line = string.Format(ic,
            "{{\"per\":\"{0}\",\"tok\":{1},\"pct5\":{2:0.0},\"pctw\":{3:0.0}," +
            "\"f5\":{4:0.000},\"fw\":{5:0.000},\"cost\":{6:0.00},\"rec\":{7}{8}}}\n",
            per, tok, pct5, pctw, f5, fw, cost, rec ? 1 : 0,
            src.Length > 0 ? ",\"src\":\"" + Esc(src) + "\"" : "");
        try
        {
            // src にディレクトリ名の日本語が入り得るので UTF-8（ATOM 側の
            // 日本語フォントがそのまま描ける）
            var b = Encoding.UTF8.GetBytes(line);
            s.Write(b, 0, b.Length);
            s.Flush();
        }
        catch
        {
            // 抜かれた / スリープ復帰などで壊れたら捨てて再探索に戻る
            bool wasConnected;
            lock (gate)
            {
                try { s.Dispose(); } catch { }
                wasConnected = stream == s;
                if (wasConnected) stream = null;
            }
            if (wasConnected) Store.LogCrash("atom: 切断（再接続待ち）");
            Status = "切断（再接続待ち）";
        }
    }

    static string Esc(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>Espressif の USB エントリ（VID_303A）に紐付く COM 番号。</summary>
    static List<string> CandidatePorts()
    {
        var found = new List<string>();
        try
        {
            using (var usb = Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Enum\USB"))
            {
                if (usb == null) return found;
                foreach (var dev in usb.GetSubKeyNames())
                {
                    if (!dev.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    using (var dk = usb.OpenSubKey(dev))
                    {
                        if (dk == null) continue;
                        foreach (var inst in dk.GetSubKeyNames())
                        {
                            using (var pk = dk.OpenSubKey(inst + @"\Device Parameters"))
                            {
                                var pn = pk == null ? null : pk.GetValue("PortName") as string;
                                if (!string.IsNullOrEmpty(pn) && !found.Contains(pn))
                                    found.Add(pn);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return found;
    }

    static Microsoft.Win32.SafeHandles.SafeFileHandle OpenPort(string name)
    {
        // GENERIC_READ|WRITE, 排他, OPEN_EXISTING
        return CreateFile("\\\\.\\" + name, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
    }

    /// <summary>ping を送って hello を待つ。resetFirst=true のときだけ 2 段階 open で
    /// デバイスを再起動させてから試す。成功なら開いたままの stream を返す。</summary>
    static FileStream TryPing(string name, bool resetFirst)
    {
        try
        {
            if (resetFirst)
            {
                using (var h1 = OpenPort(name))
                {
                    if (h1.IsInvalid) return null;
                    System.Threading.Thread.Sleep(100);
                }   // close でリセットが走り、デバイスは再起動する
                System.Threading.Thread.Sleep(2200);   // 起動待ち
            }

            var h = OpenPort(name);
            if (h.IsInvalid) return null;
            // 走行状態の制御線に固定してリセットを避ける（RTS=0=EN High, DTR=0）。
            // これで「既にアプリで走っているデバイス」を再起動せずに ping できる。
            EscapeCommFunction(h, CLRRTS);
            EscapeCommFunction(h, CLRDTR);
            var to = new CommTimeouts { RI = 50, RM = 0, RC = 300, WM = 0, WC = 300 };
            SetCommTimeouts(h, ref to);
            var fs = new FileStream(h, FileAccess.ReadWrite, 256, false);
            try
            {
                var ping = Encoding.ASCII.GetBytes("{\"ping\":1}\n");
                fs.Write(ping, 0, ping.Length);
                fs.Flush();
                var buf = new byte[512];
                var sb = new StringBuilder();
                int t0 = Environment.TickCount;
                int budget = resetFirst ? 2500 : 1500;   // やさしい試行は短く切る
                while (Environment.TickCount - t0 < budget)
                {
                    int n = fs.Read(buf, 0, buf.Length);   // COMMTIMEOUTS で 300ms 上限
                    if (n <= 0) continue;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("ctm-atom"))
                    {
                        // hello の "fw":"x.y.z" を控える（更新可否の判定に使う）
                        var t = sb.ToString();
                        int i = t.IndexOf("\"fw\":\"", StringComparison.Ordinal);
                        if (i >= 0)
                        {
                            int e = t.IndexOf('"', i + 6);
                            if (e > i) DeviceFw = t.Substring(i + 6, e - i - 6);
                        }
                        return fs;
                    }
                }
                fs.Dispose();
            }
            catch
            {
                try { fs.Dispose(); } catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>接続を確立する。まずリセットせずに ping（既に走っているデバイスは
    /// 再起動させない＝画面が保たれ、頻繁な挿し直しにも強い）。応答が無ければ
    /// 2 段階リセットで起こしてから再試行（初回接続・ハング復帰用）。</summary>
    static FileStream Handshake(string name)
    {
        var fs = TryPing(name, false);   // やさしい接続（無リセット）
        if (fs != null) return fs;
        return TryPing(name, true);      // だめならリセットして起こす
    }

    static void Discover()
    {
        try
        {
            var live = new HashSet<string>(SerialPort.GetPortNames());
            string mute = "";   // 見えているのに応答しなかったポート
            foreach (var name in CandidatePorts())
            {
                if (!live.Contains(name)) continue;   // 抜かれた後の残骸エントリ
                var fs = Handshake(name);
                if (fs != null)
                {
                    // 接続するだけ。ファーム更新は勝手に行わず、メニューからの
                    // 明示操作 (UpdateNow) でだけ書き込む。
                    lock (gate) { stream = fs; connectedPort = name; }
                    blankPort = "";
                    Status = "接続中 " + name + (DeviceFw.Length > 0 ? " (fw " + DeviceFw + ")" : "");
                    if (UpdateAvailable)
                        Status += "  ※更新あり v" + AtomFw.Ver;
                    Store.LogCrash("atom: 接続 " + name + " (fw " + DeviceFw + ")");
                    return;
                }
                if (mute.Length == 0) mute = name;
            }
            blankPort = mute;
            Status = mute.Length > 0 ? "応答なし " + mute + "（要書き込み）" : "未接続";
        }
        finally { connecting = false; }
    }

    static volatile bool resetting;

    /// <summary>ATOM が Windows から見えなくなった（USB スタックのウェッジ）ときの復旧。
    /// VID_303A の登録（present / ghost）を pnputil で削除し、USB を再スキャンさせる。
    /// これで次の接続時にドライバが入れ直され、ポートが正常に列挙し直される。
    /// pnputil は管理者権限が要るので、昇格した powershell を 1 つ起動する（UAC が出る）。
    /// 触るのは VID_303A（ATOM）だけ。他社デバイスには一切触れない。</summary>
    public static void ResetConnection()
    {
        if (resetting) return;
        resetting = true;
        try
        {
            Shutdown();   // 自分のハンドルを手放してから

            try { Directory.CreateDirectory(Store.Root); } catch { }
            string logPath = Path.Combine(Store.Root, "atom-reset.log");
            string scriptPath = Path.Combine(Store.Root, "atom-reset.ps1");

            // 昇格側で実行するスクリプト。-Command のインライン埋め込みはクォートが
            // 競合して壊れるため、必ず .ps1 に書き出して -File で渡す。
            // VID_303A（ATOM）だけを全削除 → USB 再スキャン → 結果をログへ。
            var sb = new StringBuilder();
            sb.AppendLine("$log = '" + logPath.Replace("'", "''") + "'");
            sb.AppendLine("\"=== atom usb reset $(Get-Date -Format o) ===\" | Set-Content $log");
            sb.AppendLine("$ds = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_303A*' }");
            sb.AppendLine("foreach ($d in $ds) {");
            sb.AppendLine("  \"remove: $($d.InstanceId)\" | Add-Content $log");
            sb.AppendLine("  & pnputil /remove-device $d.InstanceId *>&1 | Add-Content $log");
            sb.AppendLine("}");
            sb.AppendLine("& pnputil /scan-devices *>&1 | Add-Content $log");
            sb.AppendLine("$rest = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_303A*' }");
            sb.AppendLine("if ($rest) { \"残: $($rest.Count)\" | Add-Content $log } else { 'clean' | Add-Content $log }");
            File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"")
            {
                UseShellExecute = true,   // Verb=runas には ShellExecute が必要
                Verb = "runas",           // ここで UAC が出る
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                var p = Process.Start(psi);
                Store.LogCrash("atom: USB リセットを昇格実行（UAC 承認済み）");
                Notify("ATOM 接続リセット",
                    "USB 登録を削除して再スキャンしました。ATOM を挿し直すと、" +
                    "ドライバが入れ直されて認識し直されます。", false);
                if (p != null) { p.WaitForExit(20000); }
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // 1223 = ユーザーが UAC をキャンセル
                if (wex.NativeErrorCode == 1223)
                {
                    Store.LogCrash("atom: USB リセットは UAC で中止された");
                    Notify("ATOM 接続リセット", "管理者の許可（UAC）がキャンセルされました。", true);
                }
                else
                {
                    Store.LogCrash("atom: USB リセット失敗 " + wex.Message);
                    Notify("ATOM 接続リセット", "失敗しました: " + wex.Message, true);
                }
            }
        }
        catch (Exception ex)
        {
            Store.LogCrash("atom: USB リセット例外 " + ex.Message);
        }
        finally { resetting = false; }
    }

    static volatile bool updating;

    /// <summary>右クリックメニューの「ATOM ファームを更新」から呼ばれる。
    /// アプリ領域 (0x10000) だけを同梱ファームで書き換える手動更新。勝手には
    /// 動かない。書き込み全期間、UI に「ケーブルを抜かないでください」モーダルを
    /// 出す。ROM への入りは esptool と同じ JTAG リセット。</summary>
    public static void UpdateNow()
    {
        if (updating || flashing) return;
        updating = true;
        var th = new System.Threading.Thread(delegate ()
        {
            string port;
            lock (gate) port = connectedPort;
            string fromV = DeviceFw;
            try
            {
                // 送信用の接続を手放す（ROM モードに入れるため）
                lock (gate) { if (stream != null) { try { stream.Dispose(); } catch { } stream = null; } }
                System.Threading.Thread.Sleep(400);
                if (string.IsNullOrEmpty(port))
                {
                    var live = new HashSet<string>(SerialPort.GetPortNames());
                    foreach (var n in CandidatePorts()) if (live.Contains(n)) { port = n; break; }
                }
                if (string.IsNullOrEmpty(port))
                {
                    Notify("ATOM 更新", "デバイスのポートが見つかりません。", true);
                    return;
                }
                Store.LogCrash("atom: 手動更新 v" + fromV + " -> v" + AtomFw.Ver + " (" + port + ")");
                FlashBegin("ATOMS3R サブモニタ  ファーム更新  v" + fromV + " → v" + AtomFw.Ver);
                bool ok = FlashAppRegion(port);
                Store.LogCrash("atom: 手動更新 " + (ok ? "成功" : "失敗"));
                if (ok)
                {
                    var fs2 = Handshake(port);
                    if (fs2 != null)
                    {
                        lock (gate) { stream = fs2; connectedPort = port; }
                        Status = "接続中 " + port + " (fw " + DeviceFw + ")";
                        Store.LogCrash("atom: 接続 " + port + " (fw " + DeviceFw + ")");
                        FlashEnd(true, "v" + DeviceFw + " に更新しました");
                        Notify("ATOM 更新完了", "v" + DeviceFw + " で再接続しました。", false);
                    }
                    else
                    {
                        Status = "更新後の再接続待ち";
                        FlashEnd(true, "v" + AtomFw.Ver + " を書き込みました（再接続待ち）");
                    }
                }
                else
                {
                    FlashEnd(false, "書き込みに失敗しました");
                    Notify("ATOM 更新失敗",
                        "本体のリセットボタンを約 2 秒長押しして緑 LED（ダウンロードモード）にしてから、" +
                        "右クリック→「ATOM を更新」で再試行してください。", true);
                }
            }
            catch (Exception ex)
            {
                // ポートが書き込み中に死ぬ（USB 再列挙など）とここに来る。
                // スレッドを例外死させるとモーダルが固まったままになるので必ず畳む
                Store.LogCrash("atom: 更新スレッド例外 " + ex.Message);
                FlashEnd(false, "エラー: " + ex.Message);
                Status = "更新失敗";
            }
            finally { updating = false; }
        });
        th.IsBackground = true;
        th.Start();
    }

    /// <summary>アプリ領域 (0x10000) だけを同梱 firmware.bin で書く。ブートローダー/
    /// パーティションは触らない（文鎮化リスクを最小化）。失敗理由は crash.log へ。</summary>
    static bool FlashAppRegion(string port)
    {
        byte[] app;
        try { app = File.ReadAllBytes(AtomFw.App); }
        catch { Store.LogCrash("atom: firmware.bin を読めない"); return false; }

        try
        {
            using (var rom = EnterRom(port))
            {
                if (rom == null) { Store.LogCrash("atom: ROM 書き込みモードに入れず"); return false; }
                Store.LogCrash("atom: ROM 同期 OK");
                if (!rom.SpiAttach()) { Store.LogCrash("atom: SPI_ATTACH 失敗"); return false; }
                Store.LogCrash("atom: 書き込み開始 " + app.Length + " bytes");
                FlashProgress(0, "アプリ領域を書き込み中");
                string werr;
                bool ok = rom.WriteRegion(app, 0x10000, p =>
                {
                    Status = "FW 書き込み " + p + "%";
                    FlashProgress(p, "アプリ領域を書き込み中");
                }, out werr);
                if (!ok) { Store.LogCrash("atom: 書き込み失敗 " + werr); return false; }
                Store.LogCrash("atom: 書き込み+検証 OK, リセット");
                FlashProgress(100, "検証 OK・再起動中");
                rom.HardReset();
            }
        }
        catch (Exception ex) { Store.LogCrash("atom: 書き込み例外 " + ex.Message); return false; }
        System.Threading.Thread.Sleep(3000);   // 新ファーム起動待ち
        return true;
    }

    /// <summary>ROM ブートローダーと同期する。USB-Serial/JTAG の制御線ダンスで
    /// 確実にダウンロードモードへ入れてから SYNC する（esptool と同じ経路）。</summary>
    static EspRom EnterRom(string port)
    {
        // 壊れたアプリでブートループしている個体は USB ごと再列挙を繰り返すため、
        // 途中でポートが死んで例外になり得る。試行単位で握って次を待つ。
        for (int attempt = 0; attempt < 6; attempt++)
        {
            EspRom rom = null;
            try
            {
                rom = EspRom.Open(port);
                if (rom == null) { System.Threading.Thread.Sleep(1200); continue; }
                rom.EnterBootloader();              // JTAG リセット → ROM ダウンロードモード
                System.Threading.Thread.Sleep(500);
                if (rom.Sync()) return rom;
            }
            catch { }
            if (rom != null) { try { rom.Dispose(); } catch { } }
            System.Threading.Thread.Sleep(1000);
        }
        return null;
    }

    static volatile bool flashing;

    /// <summary>メニューからの手動書き込み（新品/復旧用のフルフラッシュ）。
    /// 未知のデバイスを勝手に書かないため、これはユーザー操作でだけ動く。</summary>
    public static void FlashManual()
    {
        if (flashing || updating) return;   // 更新スレッドとの同時書き込みも防ぐ
        flashing = true;
        var th = new System.Threading.Thread(delegate ()
        {
            try
            {
                Shutdown();
                Status = "手動書き込み中…";
                var live = new HashSet<string>(SerialPort.GetPortNames());
                // 探索が「応答なし」と検知した個体を最優先。無ければ従来どおり
                // 最初に見つかった VID_303A ポート（復旧などメニュー外の経路用）
                string port = live.Contains(blankPort) ? blankPort : null;
                foreach (var name in CandidatePorts())
                {
                    if (port != null) break;
                    if (live.Contains(name)) port = name;
                }
                if (port == null)
                {
                    Notify("ATOM 書き込み", "ESP32-S3 (VID_303A) のポートが見つかりません。", true);
                    Status = "未接続";
                    return;
                }
                Store.LogCrash("atom: 手動フルフラッシュ開始 (" + port + ", v" + AtomFw.Ver + ")");
                FlashBegin("ファーム書き込み  v" + AtomFw.Ver);
                bool ok = false;
                using (var rom = EnterRom(port))
                {
                    if (rom != null && rom.SpiAttach())
                    {
                        ok = true;
                        // パーティション表とアプリだけ。ブートローダー (0x0) は書かない:
                        // 実機検証では 0x10000 だけで起動でき、0x0 を触ると失敗時に
                        // 復旧不能になり得るため、触らないのが最も安全。
                        var plan = new List<KeyValuePair<uint, string>>
                        {
                            new KeyValuePair<uint, string>(0x8000,  AtomFw.Partitions),
                            new KeyValuePair<uint, string>(0xE000,  AtomFw.BootApp0),
                            new KeyValuePair<uint, string>(0x10000, AtomFw.App),
                        };
                        int step = 0;
                        foreach (var kv in plan)
                        {
                            if (!File.Exists(kv.Value)) { step++; continue; }   // boot_app0 は任意
                            var img = File.ReadAllBytes(kv.Value);
                            string what = Path.GetFileName(kv.Value);
                            int baseP = step * 33;
                            Status = "書き込み " + what;
                            string werr;
                            if (!rom.WriteRegion(img, kv.Key, p =>
                                {
                                    Status = what + " " + p + "%";
                                    FlashProgress(Math.Min(99, baseP + p / 3), what + " を書き込み中");
                                }, out werr))
                            {
                                Store.LogCrash("atom: " + what + " " + werr);
                                ok = false;
                                break;
                            }
                            step++;
                        }
                        if (ok) { FlashProgress(100, "検証 OK・再起動中"); rom.HardReset(); }
                    }
                }
                Store.LogCrash("atom: 手動フルフラッシュ " + (ok ? "成功" : "失敗"));
                if (ok)
                {
                    blankPort = "";   // もう空ではない。次の探索が接続する
                    FlashEnd(true, "v" + AtomFw.Ver + " を書き込みました");
                    Notify("ATOM 書き込み完了", "v" + AtomFw.Ver + " を書き込みました。数秒で表示が始まります。", false);
                    Status = "再接続待ち";
                }
                else
                {
                    FlashEnd(false, "書き込みに失敗しました");
                    Notify("ATOM 書き込み失敗",
                        "本体のリセットボタンを約 2 秒長押しして緑 LED（ダウンロードモード）にしてから、もう一度実行してください。", true);
                    Status = "書き込み失敗";
                }
            }
            catch (Exception ex)
            {
                // ポートが書き込み中に死ぬ（USB 再列挙など）とここに来る。
                // スレッドを例外死させるとモーダルが固まったままになるので必ず畳む
                Store.LogCrash("atom: 書き込みスレッド例外 " + ex.Message);
                FlashEnd(false, "エラー: " + ex.Message);
                Status = "書き込み失敗";
            }
            finally { flashing = false; }
        });
        th.IsBackground = true;
        th.Start();
    }

    public static void Shutdown()
    {
        lock (gate)
        {
            if (stream != null)
            {
                try { stream.Dispose(); } catch { }
                stream = null;
            }
        }
        if (!Store.AtomEnabled) Status = "未接続";
    }
}

/// <summary>トレイのアイコンをクリックすると出る正方形の小窓。</summary>
class CompactForm : Form
{
    readonly Timer timer = new Timer();
    List<Sample> samples = new List<Sample>();
    Store.DayTotal today = new Store.DayTotal();

    public Action OnQuit;

    // ドラッグ移動は OS の移動ループ（タイトルバー相当）に委ねる。
    // 自前の MouseMove 追跡だと、タッチ入力（Surface 等）はジェスチャ認識に
    // 吸われて連続した移動イベントが来ず、窓が一切動かせない。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 2;

    public CompactForm()
    {
        // タスクバーに並ぶ本体ウィンドウ。枠は無いがアプリとして常駐する。
        Text = "gClaudeTokenMonitor";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ApplySize();                        // 正方形（大 240 / 小 120）
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Icon = TrayApp.AppIcon;
        MinimizeBox = true;
        MaximizeBox = false;

        timer.Interval = 200;    // アーカイブ側も 200ms 周期。増分読みなので負荷は Stat 1 回分
        timer.Tick += delegate { Reload(); };
        timer.Start();

        // 60fps 級の再描画でもちらつかないように明示。Form.DoubleBuffered だけでは不足。
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer, true);
        fx.Interval = 33;
        fx.Tick += FxTick;
        fx.Start();

        // クリックで表示切替、ドラッグで移動。押した瞬間に OS の移動ループへ
        // 委ねる（タイトルバーのドラッグと同じ扱い）ので、マウスでもタッチでも
        // ペンでも動かせる。ループから戻って位置が変わっていなければクリック。
        MouseDown += delegate (object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var before = Location;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            // ここに戻るのはドラッグ（またはタップ）が終わったあと
            if (Location == before)
            {
                Store.ToggleLayout();   // 単位は右クリックの「表示単位」から
                Invalidate();
            }
            else
            {
                Store.WindowPos = Location;
                Store.SaveSettings();
            }
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

        // ウィンドウサイズ: チェック式。大 = 240px、小 = ちょうど半分の 120px
        var sizeMenu = new ToolStripMenuItem("ウィンドウサイズ");
        var scales = new[] { 1f, 0.5f };
        var sNames = new[] { "大", "小" };
        var sItems = new ToolStripMenuItem[scales.Length];
        for (int i = 0; i < scales.Length; i++)
        {
            var sc2 = scales[i];
            var mi = new ToolStripMenuItem(sNames[i]);
            mi.Checked = Math.Abs(Store.WinScale - sc2) < 0.01f;
            mi.Click += delegate
            {
                Store.WinScale = sc2;
                Store.SaveSettings();
                for (int k = 0; k < sItems.Length; k++)
                    sItems[k].Checked = Math.Abs(scales[k] - sc2) < 0.01f;
                ApplySize();
            };
            sItems[i] = mi;
            sizeMenu.DropDownItems.Add(mi);
        }
        menu.Items.Add(sizeMenu);

        // 集計期間: 中央の数字が何の合計か。5h / week は公式リセットで 0 に戻る
        var perMenu = new ToolStripMenuItem("集計期間");
        var pers = new[] { Store.Period.FiveH, Store.Period.Week };
        var pNames = new[] { "5h（5 時間制限の窓）", "week（週次の窓）" };
        var pItems = new ToolStripMenuItem[pers.Length];
        for (int i = 0; i < pers.Length; i++)
        {
            var pv = pers[i];
            var mi = new ToolStripMenuItem(pNames[i]);
            mi.Checked = Store.PeriodMode == pv;
            mi.Click += delegate
            {
                Store.PeriodMode = pv;
                Store.SaveSettings();
                for (int k = 0; k < pItems.Length; k++) pItems[k].Checked = (pers[k] == pv);
                periodSnap = true;   // 切替時は数字をスナップ（演出しない）
                Reload();
            };
            pItems[i] = mi;
            perMenu.DropDownItems.Add(mi);
        }
        menu.Items.Add(perMenu);

        // 使用率アカウント: 複数アカウント運用（個人 MAX / 会社 Teams など）の
        // 取得元切替。ctm が控えた ~/.ctm/accounts/*.json から、開くたびに作り直す。
        var acctMenu = new ToolStripMenuItem("使用率アカウント");
        Action<string, string> acctSwitch = delegate (string key, string label)
        {
            try
            {
                File.WriteAllText(Path.Combine(Store.Root, "account.json"),
                    "{\"selected\":\"" + key + "\"}");
            }
            catch (Exception ex) { Store.LogCrash("acct: 切替失敗 " + ex.Message); return; }
            Store.LogCrash("acct: " + key + " へ切替");
            Store.PokeAcctBadge();
            Invalidate();
            SubMon.Notify("使用率アカウント", label + " に切り替えました。取得中…", false);
            try
            {
                // ctm limits は取得と記録への追記までやる → 窓は数秒で新しい数字になる
                var psi = new ProcessStartInfo(Store.CtmExe, "limits");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex) { Store.LogCrash("acct: limits 起動失敗 " + ex.Message); }
        };
        Action acctRebuild = delegate
        {
            acctMenu.DropDownItems.Clear();
            string sel = Store.Str(Store.ReadAllTextShared(
                Path.Combine(Store.Root, "account.json")), "selected");
            if (sel.Length == 0) sel = "auto";
            var auto = new ToolStripMenuItem("自動（Claude Code のログインに追従）");
            auto.Checked = sel == "auto";
            auto.Click += delegate { acctSwitch("auto", "自動"); };
            acctMenu.DropDownItems.Add(auto);
            int n = 0;
            try
            {
                string ad = Path.Combine(Store.Root, "accounts");
                if (Directory.Exists(ad))
                    foreach (var f in Directory.GetFiles(ad, "*.json"))
                    {
                        var txt = Store.ReadAllTextShared(f);
                        string key = Store.Str(txt, "key");
                        if (key.Length == 0) continue;
                        string label = Store.Str(txt, "label");
                        if (label.Length == 0) label = key;
                        var it = new ToolStripMenuItem(label);
                        it.Checked = sel == key;
                        string k2 = key, l2 = label;
                        it.Click += delegate { acctSwitch(k2, l2); };
                        acctMenu.DropDownItems.Add(it);
                        n++;
                    }
            }
            catch { }
            if (n < 2)
            {
                var hint = new ToolStripMenuItem("別アカウントは claude の /login で一度ログインすると登録される");
                hint.Enabled = false;
                acctMenu.DropDownItems.Add(hint);
            }
        };
        menu.Items.Add(acctMenu);

        // ATOMS3R サブモニタ（USB 接続の 128x128 表示専用デバイス）
        var atomItem = new ToolStripMenuItem("サブモニタ (ATOM)");
        atomItem.Checked = Store.AtomEnabled;
        atomItem.Click += delegate
        {
            Store.AtomEnabled = !Store.AtomEnabled;
            atomItem.Checked = Store.AtomEnabled;
            Store.SaveSettings();
        };
        menu.Items.Add(atomItem);
        // 更新できるとき（接続中＆デバイスが同梱より古い）だけ出す。押すと書き込む。
        var atomUpdate = new ToolStripMenuItem("ATOM ファームを更新");
        atomUpdate.Click += delegate
        {
            var r = MessageBox.Show(
                "ATOMS3R サブモニタのファームを v" + SubMon.DeviceFw +
                " から v" + AtomFw.Ver + " に更新します。\n\n" +
                "書き込み中は USB ケーブルを抜かないでください（数十秒）。\n実行しますか？",
                "gClaudeTokenMonitor", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r == DialogResult.OK) SubMon.UpdateNow();
        };
        menu.Items.Add(atomUpdate);
        var atomFlash = new ToolStripMenuItem("ATOM にファームを書き込む");
        atomFlash.Click += delegate
        {
            if (!AtomFw.Available)
            {
                MessageBox.Show(
                    "同梱ファームが見つかりません（bin\\atom-fw\\）。\n" +
                    "pio run -d atom でビルドしてから build.ps1 を実行してください。",
                    "gClaudeTokenMonitor");
                return;
            }
            var r = MessageBox.Show(
                "検知した ESP32-S3 (ATOMS3R" +
                (SubMon.BlankPort.Length > 0 ? " / " + SubMon.BlankPort : "") +
                ") にファーム v" + AtomFw.Ver + " を書き込みます。\n" +
                "本体のボタン操作は不要です。\n\n" +
                "書き込み中は USB ケーブルを抜かないでください（数十秒）。\n実行しますか？",
                "gClaudeTokenMonitor", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.OK) SubMon.FlashManual();
        };
        menu.Items.Add(atomFlash);
        // ATOM が Windows から見えなくなった（USB ウェッジ）ときの復旧。管理者権限が要る。
        var atomReset = new ToolStripMenuItem("ATOM の接続をリセット（認識しない時）");
        atomReset.Click += delegate
        {
            var r = MessageBox.Show(
                "ATOM が反応しない・ポートが出ないときに使います。\n\n" +
                "USB の登録をいったん削除し、ドライバを入れ直させます" +
                "（管理者の許可＝UAC が出ます）。\n" +
                "実行したら、ATOM をいったん抜いて挿し直してください。\n\n" +
                "実行しますか？",
                "gClaudeTokenMonitor", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.OK) SubMon.ResetConnection();
        };
        menu.Items.Add(atomReset);
        menu.Opening += delegate
        {
            acctRebuild();
            atomItem.Text = "サブモニタ (ATOM)  ―  " + SubMon.Status;
            // 常に見せる。更新できるときだけ有効、最新ならグレーアウト
            bool can = SubMon.UpdateAvailable;
            atomUpdate.Enabled = can;
            if (can)
                atomUpdate.Text = "ATOM ファームを更新  (v" + SubMon.DeviceFw + " → v" + AtomFw.Ver + ")";
            else if (SubMon.Connected && AtomFw.Available)
                atomUpdate.Text = "ATOM ファームを更新  (v" + SubMon.DeviceFw + " = 最新)";
            else
                atomUpdate.Text = "ATOM ファームを更新";
            // 書き込みは「デバイスは見えるのに本アプリが応答しない」個体を検知した
            // ときだけ有効（新品・別ファーム・起動不能）。通常はグレーのまま。
            bool blank = AtomFw.Available && SubMon.FlashTarget;
            atomFlash.Enabled = blank;
            if (blank)
                atomFlash.Text = "ATOM にファームを書き込む";
            else if (!AtomFw.Available)
                atomFlash.Text = "ATOM にファームを書き込む（同梱ファームなし）";
            else if (SubMon.Connected)
                atomFlash.Text = "ATOM にファームを書き込む（導入済み）";
            else
                atomFlash.Text = "ATOM にファームを書き込む（対象なし）";
        };

        // Windows 起動時に自動開始（このマシンでの自分の絶対パスで登録するので、
        // どこに clone しても動く。レコーダーはアプリが起動時に起こす）
        var autorun = new ToolStripMenuItem("Windows 起動時に開始");
        autorun.Checked = File.Exists(StartupScriptPath);
        autorun.Click += delegate
        {
            try
            {
                if (File.Exists(StartupScriptPath)) File.Delete(StartupScriptPath);
                else
                {
                    // VBS: CreateObject("WScript.Shell").Run """<exe>""", 0, False
                    string q3 = new string('\"', 3);
                    string vbs = "' CtmMonitor 自動起動（このファイルを消せば解除）\r\n"
                        + "CreateObject(\"WScript.Shell\").Run " + q3
                        + Application.ExecutablePath + q3 + ", 0, False\r\n";
                    // WSH は ANSI で読む。UTF-8 だと日本語ユーザー名のパスが壊れる
                    File.WriteAllText(StartupScriptPath, vbs, Encoding.Default);
                }
                autorun.Checked = File.Exists(StartupScriptPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("登録に失敗: " + ex.Message, "CtmMonitor");
            }
        };
        menu.Items.Add(autorun);
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

    // 描画は常にこの論理サイズで行い、OnPaint 冒頭の ScaleTransform で
    // 実ウィンドウサイズへ縮尺する。サイズ追加は Store.WinScale だけでよい。
    const int LW = 240;
    const int LH = 240;

    static string StartupScriptPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "CtmMonitor-autostart.vbs");
        }
    }

    Size IntendedSize
    {
        get { return new Size((int)(LW * Store.WinScale), (int)(LH * Store.WinScale)); }
    }

    public void ApplySize()
    {
        Size = IntendedSize;
        Invalidate();
    }

    // この窓は論理 240x240 の固定レイアウトで、他のサイズに意味がない。
    // タブレットモードの自動最大化や FancyZones 等は、最大化ボタンが無くても
    // SetWindowPos で強制リサイズしてくる（実機で全画面の黒窓になった）。
    // 外から何が来てもサイズを取り戻す。
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (WindowState == FormWindowState.Normal)
        {
            var s = IntendedSize;
            width = s.Width; height = s.Height;
        }
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        if (WindowState == FormWindowState.Normal && Size != IntendedSize) Size = IntendedSize;
        base.OnSizeChanged(e);
    }

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
    float sweepT = -1f;                       // 縁バーを走る光 0..1（-1 = 待機）
    int sweepWait;                            // 次の光までのフレーム数
    readonly Font fF13 = new Font("Yu Gothic UI", 13f, FontStyle.Bold);
    readonly Font fT7 = new Font("Yu Gothic UI", 7f);

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

    double periodCost;      // Big の下段に出す、選択期間のコスト
    bool periodSnap;        // 期間を切り替えた直後は演出せずスナップ

    public void Reload()
    {
        samples = Store.Latest();
        var t = Store.TodayLive();

        // 選択期間（5h / week の窓）の合計。窓は公式リセット時刻に一致する
        long nt; double nc;
        Store.SumSince(Store.PeriodStart(samples), out nt, out nc);
        periodCost = nc;
        if (periodSnap) { periodSnap = false; primed = true; shown = target = nt; }
        if (!primed)
        {
            primed = true;
            shown = target = nt;      // 起動直後の「今日すでに 2 億」は演出しない
        }
        else if (nt > target)
        {
            long delta = nt - (long)target;
            target = nt;
            string bsrc = Store.TakeLastSources();
            if (Store.LayoutMode == Store.Layout.Big) TriggerFx(delta, bsrc);
            else shown = nt;          // Detail 表示中は静かに追従
            SubMon.NoteBurst(bsrc);   // サブモニタの +N フロートにも発生源を出す
        }
        else if (nt < target)
        {
            target = nt;              // 日付が変わって今日の合計が減った
            shown = nt;
        }
        today = t;
        Store.Supervise();   // クラッシュしていれば証跡を残して自動再起動

        // サブモニタ (ATOM) へ現在の状態を送る（有効時のみ・表示専用）
        {
            double sp = 0, wp = 0, f5 = -1, fw = -1;
            foreach (var x in samples)
            {
                if (x.Key == "session") { sp = x.Percent; f5 = ResetFrac(x.ResetsAt, 5.0); }
                if (x.Key == "weekly_all") { wp = x.Percent; fw = ResetFrac(x.ResetsAt, 168.0); }
            }
            SubMon.Tick(Store.PeriodLabel, (long)target, sp, wp, f5, fw,
                periodCost, Store.RecorderAlive());
        }
        Invalidate();
    }

    /// <summary>増分の大きさで演出の強度を決める。5 秒ごとに必ず起きるので
    /// 小さい増分は控えめに、大きいバーストだけ派手にする。</summary>
    void TriggerFx(long delta, string src)
    {
        tier = delta < 50000 ? 1 : delta < 200000 ? 2 : delta < 1000000 ? 3 : 4;
        punch = 0.03f + 0.02f * tier;
        flash = 0.5f + 0.125f * tier;                    // 数字が一瞬金色に光る
        if (tier >= 2) shimmerT = 0f;

        // キラ星は数字の縁のリングから外向きに弾ける。数字の真上に湧かせると
        // 白地に白で見えないので、外周に出してから減速させる
        int pn = tier == 1 ? 5 : tier == 2 ? 12 : tier == 3 ? 22 : 34;
        float cx0 = LW / 2f, cy0 = LH / 2f - 8;
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
        floats.Add(new object[] { "+" + Store.Tokens(delta), 1f, src });
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
        float depth = Math.Max(8, LH - wl - 14);
        for (int i = 0; i < bn && parts.Count < 140; i++)
            parts.Add(new float[] {
                (float)(rng.NextDouble() * LW),
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
                (float)(rng.NextDouble() * LW),          // x
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
                float sx = (float)(rng.NextDouble() * LW);
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

        // 縁バーの進行感: 約 5 秒に 1 回、塗られた部分を光の帯が走る
        if (sweepT >= 0f)
        {
            sweepT += 0.05f;
            if (sweepT > 1f) { sweepT = -1f; sweepWait = 0; }
        }
        else if (++sweepWait >= 150)   // 30fps × 5 秒
        {
            sweepT = 0f;
        }

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
        // 0% = 底（空）、100% = 上端マージン。0% で底に水を残さない。
        float level = (float)(LH * (1.0 - Math.Min(pct, 100) / 100.0));
        return Math.Max(14, level);
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
        // ---- 奥: 週（紫）---- ほぼ 0% なら描かない（底に薄く残さない）
        if (wk >= 0.5)
        {
            float lvW = LevelFor(wk);
            DrawWaveFill(g, lvW, 3.4f, 0.034f, waterPhase * 0.6 + 1.3,
                Color.FromArgb(70, 150, 115, 235), Color.FromArgb(90, 55, 38, 130));
            DrawSurfaceLine(g, lvW, 3.4f, 0.034f, waterPhase * 0.6 + 1.3,
                Color.FromArgb(90, 200, 170, 255));
        }

        // ---- 手前: 5h（青・二重塗りで深さを出す）---- ほぼ 0% なら描かない
        if (ses >= 0.5)
        {
            float lvS = LevelFor(ses);
            DrawWaveFill(g, lvS - 2, 4.0f, 0.040f, waterPhase * 0.7 + 2.1,
                Color.FromArgb(50, 96, 140, 235), Color.FromArgb(65, 30, 45, 110));
            DrawWaveFill(g, lvS, 3.2f, 0.045f, waterPhase,
                Color.FromArgb(95, 96, 155, 245), Color.FromArgb(120, 30, 52, 135));
            DrawSurfaceLine(g, lvS, 3.2f, 0.045f, waterPhase,
                Color.FromArgb(130, 170, 210, 255));
        }
    }

    /// <summary>窓の経過割合 0..1。リセット直後 = 0 から始まり、時間が進むほど
    /// 伸びて、リセット時刻で満タン = 1 になる（残り 51 分の 5h 窓なら約 0.83）。</summary>
    static double ResetFrac(string resetsAt, double windowHours)
    {
        DateTime t;
        if (!DateTime.TryParse(resetsAt, null, DateTimeStyles.RoundtripKind, out t)) return -1;
        double remain = (t.ToLocalTime() - DateTime.Now).TotalHours;
        if (remain <= 0) return 1;
        return Math.Max(0.0, Math.Min(1.0, 1.0 - remain / windowHours));
    }

    /// <summary>画面の縁に沿う細いプログレスバー。経過ぶんだけ左から塗り、
    /// 約 5 秒に 1 回、塗られた部分を光の帯が走って「進んでいる」ことを見せる。</summary>
    void DrawEdgeBar(Graphics g, float y, double frac, Color c)
    {
        if (frac < 0) return;
        using (var track = new SolidBrush(Color.FromArgb(55, c)))
            g.FillRectangle(track, 1, y, LW - 2, 3);
        float w = (float)((LW - 2) * frac);
        if (w < 1) return;
        using (var fill = new SolidBrush(Color.FromArgb(230, c)))
            g.FillRectangle(fill, 1, y, w, 3);

        if (sweepT >= 0f && w >= 8)
        {
            float bandW = Math.Max(14f, Math.Min(30f, w * 0.5f));
            float bx = 1 + (w + bandW) * sweepT - bandW;
            var clip = g.Clip;
            g.SetClip(new RectangleF(1, y, w, 3), CombineMode.Intersect);
            var rect = new RectangleF(bx, y, bandW, 3);
            using (var lg = new LinearGradientBrush(rect,
                       Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                       LinearGradientMode.Horizontal))
            {
                var cb = new ColorBlend(3);
                cb.Colors = new[] { Color.FromArgb(0, 255, 255, 255),
                    Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                cb.Positions = new[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, rect);
            }
            g.Clip = clip;
        }
    }

    void DrawSurfaceLine(Graphics g, float level, float amp, float k, double phase, Color c)
    {
        using (var pen = new Pen(c, 1.2f))
        {
            var prev2 = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float px = 0, py = SurfaceY(level, 0, amp, k, phase);
            for (int x2 = 4; x2 <= LW; x2 += 4)
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
        float u = x / LW;                       // 0..1
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
        for (int x = 0; x <= LW; x += 6)
            pts.Add(new PointF(x, SurfaceY(level, x, amp, k, phase)));
        pts.Add(new PointF(LW, SurfaceY(level, LW, amp, k, phase)));
        pts.Add(new PointF(LW, LH));
        pts.Add(new PointF(0, LH));
        float rise = amp * 3 + Math.Abs(m1) + Math.Abs(m2) + Math.Abs(m3) + Math.Abs(bob) + 6;
        var rect = new RectangleF(0, Math.Max(0, level - rise), LW,
            Math.Max(1, LH - level + rise));
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
            var t = Store.PeriodLabel + "  " + Store.UnitName;
            var sz = g.MeasureString(t, fT8);
            g.DrawString(t, fT8, b, (LW - sz.Width) / 2, 14);
        }

        long shownL = (long)Math.Round(shown);
        var tok = Store.Tokens(shownL);
        if (fitFont == null || fitLen != tok.Length)
        {
            if (fitFont != null && fitFont != fHuge) fitFont.Dispose();
            if (fitFontR != null && fitFontR != fHugeR) fitFontR.Dispose();
            var m = g.MeasureString(tok, fHuge);
            if (m.Width <= LW - 28)
            {
                fitFont = fHuge;
                fitFontR = fHugeR;
            }
            else
            {
                float size = fHuge.Size * (LW - 28) / m.Width;
                fitFont = new Font("Yu Gothic UI", size, FontStyle.Bold);
                fitFontR = new Font("Yu Gothic UI", size, FontStyle.Regular);
            }
            fitLen = tok.Length;
        }
        var tsz = g.MeasureString(tok, fitFont);
        float cx = LW / 2f, cy = LH / 2f - 8;

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
            g.DrawString(t, fT8, b, (LW - sz.Width) / 2, cy + tsz.Height / 2 - 6);
        }

        // +N フロート: 1UP 風。出た瞬間が速く、減速しながらすーっと昇って消える。
        // 下に発生源（どの作業ディレクトリの消費か）を小さく添える
        foreach (var f in floats)
        {
            float life = (float)f[1];
            var t = (string)f[0];
            string src = f.Length > 2 ? (string)f[2] : "";
            float rise = (float)(1 - Math.Pow(life, 0.6)) * 58f;   // ease-out で 58px 上昇
            float fy = cy - tsz.Height / 2 - 16 - rise;
            int a = (int)(255 * Math.Min(1f, life * 3f));          // 最後の 1/3 でフェード
            var sz = g.MeasureString(t, fF13);
            float fx2 = cx - sz.Width / 2;
            using (var b = new SolidBrush(Color.FromArgb(a * 2 / 3, 0, 0, 0)))
                g.DrawString(t, fF13, b, fx2 + 1.5f, fy + 1.5f);   // 影
            using (var b = new SolidBrush(Color.FromArgb(a, 150, 235, 170)))
                g.DrawString(t, fF13, b, fx2, fy);                 // 明るい緑
            if (src.Length > 0)
            {
                var ss = g.MeasureString(src, fT7);
                float sx2 = cx - ss.Width / 2;
                float sy2 = fy + sz.Height - 5;
                using (var b = new SolidBrush(Color.FromArgb(a * 3 / 5, 0, 0, 0)))
                    g.DrawString(src, fT7, b, sx2 + 1f, sy2 + 1f);
                using (var b = new SolidBrush(Color.FromArgb(a * 4 / 5, 205, 220, 235)))
                    g.DrawString(src, fT7, b, sx2, sy2);
            }
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
        DrawShadowed(g, Store.Money(periodCost), fMid11, Theme.Fg, LH - 54);
        // 凡例は水の色と対応させる: ● 5h = 青、● 週 = 紫
        {
            string t1 = string.Format(CultureInfo.InvariantCulture, "● 5h {0:0}%", sesPct);
            string t2 = string.Format(CultureInfo.InvariantCulture, "● week {0:0}%", wkPct);
            var s1 = g.MeasureString(t1, fT9b);
            var s2 = g.MeasureString(t2, fT9b);
            float total = s1.Width + 10 + s2.Width;
            float lx = (LW - total) / 2;
            float ly = LH - 32;
            using (var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            {
                g.DrawString(t1, fT9b, sh, lx + 1, ly + 1);
                g.DrawString(t2, fT9b, sh, lx + s1.Width + 10 + 1, ly + 1);
            }
            using (var b = new SolidBrush(Color.FromArgb(235, 140, 190, 255)))
                g.DrawString(t1, fT9b, b, lx, ly);
            using (var b = new SolidBrush(Color.FromArgb(235, 190, 160, 255)))
                g.DrawString(t2, fT9b, b, lx + s1.Width + 10, ly);
        }

        if (!Store.RecorderAlive())
            using (var b = new SolidBrush(Theme.Bad))
                g.DrawString("● 記録停止中", fT8, b, 10, 8);

        // 縁のバー: 上端 = 5h 窓の経過（青）、下端 = 週の経過（紫）。
        // リセット直後は空で、時間が進むほど右へ伸び、満タンでリセット。
        double fS = -1, fW = -1;
        foreach (var x in samples)
        {
            if (x.Key == "session") fS = ResetFrac(x.ResetsAt, 5.0);
            if (x.Key == "weekly_all") fW = ResetFrac(x.ResetsAt, 168.0);
        }
        DrawEdgeBar(g, 2, fS, Color.FromArgb(120, 180, 255));
        DrawEdgeBar(g, LH - 5, fW, Color.FromArgb(190, 160, 255));
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
        float x = (LW - sz.Width) / 2;
        using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            g.DrawString(t, f, b, x + 1, y + 1);
        using (var b = new SolidBrush(c))
            g.DrawString(t, f, b, x, y);
    }

    void DrawBorder(Graphics g)
    {
        // 1px の白い縁取りは縮尺の外で描く（0.5px になって欠けるのを防ぐ）。
        var st = g.Save();
        g.ResetTransform();
        var prev = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var p = new Pen(Theme.Border, 1f))
            g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        g.SmoothingMode = prev;
        g.Restore(st);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
        // 以降の描画はすべて論理 240x240。実サイズへはここで一括縮尺する
        g.ScaleTransform(Store.WinScale, Store.WinScale);

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
        {
            // アカウントを明示選択中はアプリ名の代わりにその名前（どの口座の
            // 数字を見ているか、窓だけで分かるように）
            var hd = Store.AcctBadge;
            g.DrawString(hd.Length > 0 ? hd : "gClaudeTokenMonitor", fSmall, b, 12, y);
        }
        using (var b = new SolidBrush(Theme.Accent))
        {
            var u = Store.UnitName;
            var sz = g.MeasureString(u, fSmall);
            g.DrawString(u, fSmall, b, LW - 12 - sz.Width, y);
        }
        y += 20;

        if (samples.Count == 0)
        {
            // データが無い理由を名指しで出す。「記録待ち…」だけでは、CLI 未導入の
            // PC（Claude デスクトップアプリのみ等）で永久に待たせてしまう。
            int env = Store.EnvState;
            if (env == 1)
                using (var b = new SolidBrush(Theme.ForPct(80)))
                    g.DrawString("Claude Code (CLI) が\nこの PC に見つかりません\n\nインストールして一度使うと\n計測が始まります", fSmall, b, 12, y + 14);
            else if (env == 2)
                using (var b = new SolidBrush(Theme.ForPct(80)))
                    g.DrawString("claude に未ログインのため\n使用率を取得できません\n\nログインすると表示されます", fSmall, b, 12, y + 14);
            else
                using (var b = new SolidBrush(Theme.Mut))
                    g.DrawString("記録待ち…\n(ctm record が\n 使用率を取るまで)", fSmall, b, 12, y + 20);
        }

        foreach (var s in samples)
        {
            if (y > LH - 66) break;
            var col = Theme.ForPct(s.Percent);
            using (var b = new SolidBrush(Theme.Fg))
                g.DrawString(s.Label, fSmall, b, 12, y);
            using (var b = new SolidBrush(Theme.Mut))
            {
                var sz = g.MeasureString(Store.Left(s.ResetsAt), fSmall);
                g.DrawString(Store.Left(s.ResetsAt), fSmall, b, LW - 12 - sz.Width, y);
            }
            y += 17;
            using (var b = new SolidBrush(col))
                g.DrawString(s.Percent.ToString("0", CultureInfo.InvariantCulture) + "%", fPct, b, 10, y - 4);
            // バー
            int bx = 62, bw = LW - bx - 12;
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, bx, y + 6, bw, 7);
            using (var b = new SolidBrush(col))
                g.FillRectangle(b, bx, y + 6, (int)(bw * Math.Min(s.Percent, 100) / 100.0), 7);
            using (var b = new SolidBrush(Theme.Mut))
                g.DrawString(Store.Tokens(s.Tokens) + "  " + Store.Money(s.Cost),
                    fSmall, b, bx, y + 15);
            y += 38;
        }

        // 選択中の集計期間（5h / week）の実測。Big と同じ窓・同じ数字
        using (var p = new Pen(Theme.Line)) g.DrawLine(p, 12, LH - 46, LW - 12, LH - 46);
        using (var b = new SolidBrush(Theme.Mut))
            g.DrawString(Store.PeriodLabel, fSmall, b, 12, LH - 40);
        using (var b = new SolidBrush(Theme.Fg))
            g.DrawString(Store.Tokens((long)target) + " tok   " + Store.Money(periodCost),
                fMid, b, 12, LH - 25);
        // 右側: この窓のリセットまでの残り
        {
            string key = Store.PeriodMode == Store.Period.Week ? "weekly_all" : "session";
            string left = "";
            foreach (var x in samples)
                if (x.Key == key) left = "reset " + Store.Left(x.ResetsAt);
            if (left.Length > 0)
                using (var b = new SolidBrush(Theme.Mut))
                {
                    var sz = g.MeasureString(left, fSmall);
                    g.DrawString(left, fSmall, b, LW - 12 - sz.Width, LH - 22);
                }
        }

        using (var b0 = new SolidBrush(Theme.Line))
            g.DrawString("クリックで表示切替 / ドラッグで移動", fT7, b0, 12, LH - 60);

        if (!Store.RecorderAlive())
            using (var b = new SolidBrush(Theme.Bad))
                g.DrawString("● 記録停止中", fSmall, b, LW - 90, LH - 40);

        fSmall.Dispose(); fPct.Dispose(); fMid.Dispose();
    }
}

/// <summary>過去ログの閲覧窓。</summary>
class DetailForm : Form
{
    readonly ComboBox dayBox = new ComboBox();
    readonly ListView instrView = new ListView();
    readonly ListView limitsView = new ListView();
    readonly ListView eventsView = new ListView();
    readonly Label summary = new Label();

    public DetailForm()
    {
        Text = "gClaudeTokenMonitor — 過去ログ";
        Icon = TrayApp.AppIcon;
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
        // メインは「指示ごと」: 1 行 = 人間の指示 1 つ。この指示にいくらかかったか。
        var t0 = new TabPage("指示ごと") { BackColor = Theme.Bg };
        // ドリルダウン用: 1 行 = 課金単位 1 応答（1 指示から数十行生まれる）
        var t1 = new TabPage("メッセージ（課金単位）") { BackColor = Theme.Bg };
        // 診断用: 公式使用率 % の 5 分毎スナップショット。いつ % が跳ねたかを突き合わせる
        var t2 = new TabPage("使用率の推移（5分毎）") { BackColor = Theme.Bg };

        Setup(instrView, new[] { "開始", "所要", "セッション", "作業ディレクトリ", "応答数", "トークン", "cache率", "コスト", "指示（先頭200字）", "ディレクトリ（フルパス）" },
            new[] { 70, 60, 80, 130, 60, 110, 60, 90, 420, 280 });
        // ダブルクリック（または Enter）で、その指示の応答内訳をモーダルで開く
        instrView.ItemActivate += delegate
        {
            if (instrView.SelectedItems.Count == 0) return;
            var tag = instrView.SelectedItems[0].Tag as object[];
            if (tag == null) return;
            using (var dlg = new InstrDetailForm(tag))
                dlg.ShowDialog(this);
        };
        Setup(eventsView, new[] { "時刻", "セッション", "作業ディレクトリ", "モデル", "cache-read", "output", "合計", "コスト", "指示（先頭200字）" },
            new[] { 80, 80, 140, 130, 100, 80, 100, 90, 380 });
        Setup(limitsView, new[] { "時刻", "窓", "使用率", "リセットまで", "実測メッセージ", "実測トークン", "実測コスト" },
            new[] { 90, 150, 80, 110, 120, 140, 110 });

        t0.Controls.Add(instrView);
        t1.Controls.Add(eventsView);
        t2.Controls.Add(limitsView);
        tabs.TabPages.Add(t0);
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
        if (!DateTime.TryParseExact(dayBox.SelectedItem as string, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return;

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
        var groups = new List<object[]>();   // [ses,cwd,prompt,t0,t1,n,tok,cost,rows,cwdFull]
        // セッションごとに現在のグループを追う。並行セッションの行はアーカイブ上で
        // 交互に並ぶため、直前行との比較だと同じ指示が何個にも分断されてしまう
        var curBy = new Dictionary<string, object[]>();
        foreach (var line in Store.ReadLines(Store.EventsPath(day)))
        {
            if (line.Length < 3) continue;
            n++;
            string ses = FieldStr(line, "session");
            string cwd = FieldStr(line, "cwd_name");
            string cwdFull = FieldStr(line, "cwd");
            string pr = FieldStr(line, "prompt");
            DateTime ts;
            DateTime.TryParse(FieldStr(line, "ts"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out ts);
            long tok = (long)FieldNum(line, "total");
            double cost = FieldNum(line, "cost_usd");
            string model = FieldStr(line, "model");
            long cread = (long)FieldNum(line, "cache_read");
            long outp = (long)FieldNum(line, "output");

            var it = new ListViewItem(Field(line, "ts", 11, 8));
            it.SubItems.Add(Cut(ses, 8));
            it.SubItems.Add(cwd);
            it.SubItems.Add(model);
            it.SubItems.Add(cread.ToString("N0"));
            it.SubItems.Add(outp.ToString("N0"));
            it.SubItems.Add(tok.ToString("N0"));
            it.SubItems.Add(Store.Money(cost));
            it.SubItems.Add(pr);
            eventsView.Items.Add(it);

            // 指示ごと: そのセッションの中で指示が変わったときだけ新グループ
            object[] cur;
            if (!curBy.TryGetValue(ses, out cur) || (string)cur[2] != pr)
            {
                cur = new object[] { ses, cwd, pr, ts, ts, 0, 0L, 0.0,
                    new List<object[]>(), cwdFull, 0L };
                curBy[ses] = cur;
                groups.Add(cur);
            }
            cur[4] = ts;
            cur[5] = (int)cur[5] + 1;
            cur[6] = (long)cur[6] + tok;
            cur[7] = (double)cur[7] + cost;
            cur[10] = (long)cur[10] + cread;   // cache率の分子
            // モーダル用: 応答 1 件ぶんの完全な内訳
            ((List<object[]>)cur[8]).Add(new object[] {
                ts, model,
                (long)FieldNum(line, "input"),
                (long)FieldNum(line, "cache_write_5m"),
                (long)FieldNum(line, "cache_write_1h"),
                cread, outp,
                (long)FieldNum(line, "thinking_tokens"),
                tok, cost });
        }
        eventsView.EndUpdate();

        instrView.BeginUpdate();
        instrView.Items.Clear();
        groups.Sort(delegate (object[] x, object[] y)   // 新しい指示を上に
        {
            return ((DateTime)y[3]).CompareTo((DateTime)x[3]);
        });
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var gr = groups[gi];
            var t0g = (DateTime)gr[3];
            var t1g = (DateTime)gr[4];
            var durS = (t1g - t0g).TotalSeconds;
            string dur = durS >= 60 ? string.Format("{0:0}m{1:00}s", (int)(durS / 60), (int)durS % 60)
                                    : string.Format("{0:0}s", durS);
            var it = new ListViewItem(t0g.ToString("HH:mm:ss"));
            it.SubItems.Add(dur);
            it.SubItems.Add(Cut((string)gr[0], 8));
            it.SubItems.Add((string)gr[1]);
            it.SubItems.Add(((int)gr[5]).ToString());
            it.SubItems.Add(((long)gr[6]).ToString("N0"));
            // cache率: この指示の総トークンに占めるキャッシュ読出の割合。
            // 96% のような値が普通（同じ文脈を応答のたびに読み直すため）。
            long gtok = (long)gr[6], gcr = (long)gr[10];
            it.SubItems.Add(gtok > 0 ? ((double)gcr * 100 / gtok).ToString("0") + "%" : "-");
            it.SubItems.Add(Store.Money((double)gr[7]));
            var pr2 = (string)gr[2];
            it.SubItems.Add(pr2.Length > 0 ? pr2 : "（指示の記録なし）");
            it.SubItems.Add((string)gr[9]);
            if (pr2.Length == 0) it.ForeColor = Theme.Mut;
            it.Tag = gr;
            instrView.Items.Add(it);
        }
        instrView.EndUpdate();

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
        // 値の中の \" を終端と間違えない（指示文には引用符が普通に入る）
        int j = i;
        while (j < line.Length)
        {
            if (line[j] == '"' && line[j - 1] != '\\') break;
            j++;
        }
        if (j >= line.Length) return "";
        return Decode(line.Substring(i, j - i)).Replace("\\\"", "\"");
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

/// <summary>指示 1 つの応答内訳を出すモーダル。1 行 = 課金応答 1 件、下段に合計。</summary>
class InstrDetailForm : Form
{
    public InstrDetailForm(object[] gr)
    {
        string prompt = (string)gr[2];
        var t0 = (DateTime)gr[3];
        var t1 = (DateTime)gr[4];
        var rows = (List<object[]>)gr[8];

        Text = "指示の内訳";
        Size = new Size(980, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = new Font("Yu Gothic UI", 9f);
        MinimizeBox = false;
        KeyPreview = true;
        KeyDown += delegate (object o, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        // 上段: 指示の全文（記録している先頭 200 字）
        var head = new Label
        {
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(12, 8, 12, 4),
            Text = prompt.Length > 0 ? prompt : "（指示の記録なし）",
            ForeColor = prompt.Length > 0 ? Theme.Fg : Theme.Mut,
        };
        var sub = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(12, 0, 12, 0),
            ForeColor = Theme.Mut,
            Text = string.Format("セッション {0}   {1} → {2}   {3}",
                ((string)gr[0]).Length >= 8 ? ((string)gr[0]).Substring(0, 8) : (string)gr[0],
                t0.ToString("HH:mm:ss"), t1.ToString("HH:mm:ss"),
                gr.Length > 9 ? (string)gr[9] : ""),
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = Theme.Card,
            ForeColor = Theme.Fg,
            BorderStyle = BorderStyle.None,
        };
        string[] cols = { "#", "時刻", "モデル", "input", "cache-w 5m", "cache-w 1h",
                          "cache-read", "output", "(思考)", "合計", "コスト" };
        int[] ws = { 36, 70, 130, 60, 84, 84, 100, 76, 66, 100, 84 };
        for (int i = 0; i < cols.Length; i++) list.Columns.Add(cols[i], ws[i]);

        long tIn = 0, tC5 = 0, tC1 = 0, tCr = 0, tOut = 0, tTh = 0, tTok = 0;
        double tCost = 0;
        int n = 0;
        foreach (var r in rows)
        {
            n++;
            var it = new ListViewItem(n.ToString());
            it.SubItems.Add(((DateTime)r[0]).ToString("HH:mm:ss"));
            it.SubItems.Add((string)r[1]);
            it.SubItems.Add(((long)r[2]).ToString("N0"));
            it.SubItems.Add(((long)r[3]).ToString("N0"));
            it.SubItems.Add(((long)r[4]).ToString("N0"));
            it.SubItems.Add(((long)r[5]).ToString("N0"));
            it.SubItems.Add(((long)r[6]).ToString("N0"));
            it.SubItems.Add(((long)r[7]).ToString("N0"));
            it.SubItems.Add(((long)r[8]).ToString("N0"));
            it.SubItems.Add(Store.Money((double)r[9]));
            list.Items.Add(it);
            tIn += (long)r[2]; tC5 += (long)r[3]; tC1 += (long)r[4];
            tCr += (long)r[5]; tOut += (long)r[6]; tTh += (long)r[7];
            tTok += (long)r[8]; tCost += (double)r[9];
        }

        // 下段: 合計
        var foot = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(12, 6, 12, 6),
            Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold),
            ForeColor = Theme.Fg,
            Text = string.Format(
                "合計  応答 {0} ／ {1} tok ／ {2}\r\n" +
                "内訳  in {3:N0} ・ cache-w5m {4:N0} ・ cache-w1h {5:N0} ・ cache-r {6:N0} ・ out {7:N0}（思考 {8:N0}）",
                n, tTok.ToString("N0"), Store.Money(tCost),
                tIn, tC5, tC1, tCr, tOut, tTh),
        };

        Controls.Add(list);
        Controls.Add(foot);
        Controls.Add(sub);
        Controls.Add(head);
    }
}

/// <summary>ファーム書き込み中の全画面級モーダル。書き込みの間ずっと最前面に居座り、
/// 「ケーブルを抜かないでください」を大きく出し、× もタスクからも閉じられない。
/// 完了/失敗で初めて閉じられる（成功は自動で閉じる）。UI スレッドで動かす。</summary>
class FlashForm : Form
{
    readonly Label title = new Label();
    readonly Label warn = new Label();
    readonly Label sub = new Label();
    readonly Panel barBg = new Panel();
    readonly Panel bar = new Panel();
    readonly Button close = new Button();
    bool done;              // 完了/失敗して閉じてよい状態
    int pulse;
    readonly Timer blink = new Timer();

    public FlashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 320);
        BackColor = Color.FromArgb(16, 15, 20);
        TopMost = true;
        ShowInTaskbar = false;
        ControlBox = false;
        DoubleBuffered = true;

        var accent = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Warn };
        Controls.Add(accent);

        // 何の話か一目で分かるよう、対象デバイスを常に出す（heading で消さない）
        var device = new Label
        {
            Text = "🔌  ATOMS3R サブモニタ（USB 接続の小型ディスプレイ）",
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        };
        device.SetBounds(36, 22, 488, 24);
        Controls.Add(device);

        title.Text = "ファーム書き込み中";
        title.ForeColor = Theme.Fg;
        title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        title.SetBounds(36, 48, 488, 32);
        Controls.Add(title);

        warn.Text = "⚠  ATOMS3R の USB ケーブルを抜かないでください";
        warn.ForeColor = Theme.Warn;
        warn.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
        warn.TextAlign = ContentAlignment.MiddleCenter;
        warn.SetBounds(20, 100, 520, 44);
        Controls.Add(warn);

        var expl = new Label
        {
            Text = "書き込み中に接続を切ると、ATOMS3R が起動しなくなることがあります。\n"
                 + "完了メッセージが出るまで、ケーブルも本体もそのままにしてください。",
            ForeColor = Theme.Mut,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        expl.SetBounds(30, 148, 500, 44);
        Controls.Add(expl);

        barBg.SetBounds(36, 206, 488, 16);
        barBg.BackColor = Theme.Line;
        Controls.Add(barBg);
        bar.SetBounds(0, 0, 0, 16);
        bar.BackColor = Theme.Ok;
        barBg.Controls.Add(bar);

        sub.Text = "準備中…";
        sub.ForeColor = Theme.Mut;
        sub.Font = new Font("Consolas", 9.5f);
        sub.SetBounds(36, 230, 488, 24);
        Controls.Add(sub);

        close.Text = "閉じる";
        close.SetBounds(232, 262, 96, 34);
        close.FlatStyle = FlatStyle.Flat;
        close.ForeColor = Theme.Fg;
        close.BackColor = Theme.Card;
        close.Visible = false;
        close.Click += delegate { Hide(); };
        Controls.Add(close);

        blink.Interval = 500;
        blink.Tick += delegate
        {
            if (done) return;
            pulse ^= 1;
            warn.ForeColor = pulse == 0 ? Theme.Warn : Color.FromArgb(255, 210, 150);
        };
        blink.Start();
    }

    // 書き込み中は一切閉じさせない（Alt+F4・タスク終了・× すべて拒否）。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!done && e.CloseReason != CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    const int CP_NOCLOSE_BUTTON = 0x200;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= CP_NOCLOSE_BUTTON; return cp; }
    }

    public void Start(string heading)
    {
        done = false;
        title.Text = heading;
        warn.Visible = true;
        close.Visible = false;
        SetProgress(0, "書き込みモードに入っています…");
        blink.Start();
        if (!Visible) Show();
        BringToFront();
        Activate();
    }

    public void SetProgress(int pct, string text)
    {
        pct = Math.Max(0, Math.Min(100, pct));
        bar.Width = (int)(barBg.ClientSize.Width * (pct / 100.0));
        sub.Text = text + "   " + pct + "%";
    }

    public void Finish(bool ok, string text)
    {
        done = true;
        blink.Stop();
        warn.Visible = false;
        title.Text = ok ? "書き込み完了" : "書き込み失敗";
        bar.BackColor = ok ? Theme.Ok : Theme.Bad;
        if (ok) bar.Width = barBg.ClientSize.Width;
        sub.ForeColor = ok ? Theme.Ok : Theme.Bad;
        sub.Text = (ok ? "✓ " : "✕ ") + text
                 + (ok ? "  — 抜いて構いません" : "  — ケーブルを抜いて、挿し直してください");
        close.Visible = true;
        if (ok)
        {
            var t = new Timer { Interval = 2500 };
            t.Tick += delegate { t.Stop(); if (Visible) Hide(); };
            t.Start();
        }
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
        icon.Text = "gClaudeTokenMonitor";
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

        // ATOM のファーム更新など、バックグラウンドスレッドからの通知を UI で出す
        SubMon.Notify = delegate (string title, string msg, bool err)
        {
            try
            {
                compact.BeginInvoke((Action)delegate
                {
                    icon.ShowBalloonTip(8000, title, msg,
                        err ? ToolTipIcon.Error : ToolTipIcon.Info);
                });
            }
            catch { }
        };

        // ファーム書き込み中モーダル（「ケーブルを抜かないでください」）。
        // SubMon の書き込みスレッドから呼ばれるので UI スレッドへ渡す。
        SubMon.FlashBegin = delegate (string heading)
        {
            try { compact.BeginInvoke((Action)delegate { FlashUi().Start(heading); }); } catch { }
        };
        SubMon.FlashProgress = delegate (int pct, string text)
        {
            try { compact.BeginInvoke((Action)delegate { FlashUi().SetProgress(pct, text); }); } catch { }
        };
        SubMon.FlashEnd = delegate (bool ok, string text)
        {
            try { compact.BeginInvoke((Action)delegate { FlashUi().Finish(ok, text); }); } catch { }
        };

        // 記録が止まっていれば起こす。直後に Supervise が誤検知で殺さないよう印を付ける
        if (!Store.RecorderAlive())
        {
            Store.Run("record -quiet", false);
            Store.NoteRestart();
        }

        tip.Interval = 30000;
        tip.Tick += delegate { UpdateTip(); };
        tip.Start();
        UpdateTip();
    }

    void Toggle() { compact.Toggle(); }

    FlashForm flashForm;
    FlashForm FlashUi()
    {
        if (flashForm == null || flashForm.IsDisposed) flashForm = new FlashForm();
        return flashForm;
    }

    bool startErrorShown;

    void UpdateTip()
    {
        // レコーダーを起動できない環境（Smart App Control / SmartScreen のブロック等）は
        // 「記録停止中」だけでは原因が分からないので、理由を一度だけ通知する
        if (!startErrorShown && Store.StartError.Length > 0 && !Store.RecorderAlive())
        {
            startErrorShown = true;
            icon.ShowBalloonTip(15000, "レコーダー (ctm.exe) を起動できません",
                Store.StartError + "\nSmartScreen / Smart App Control にブロックされた場合の対処は、同梱 README の「つまずいたら」を参照。",
                ToolTipIcon.Error);
        }
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
    // Icon.FromHandle の HICON は Dispose では解放されず、30 秒ごとに作ると
    // 既定の GDI 上限 (10000) を数日で食い潰す。色ごとに一度だけ作って使い回す。
    static readonly Dictionary<int, Icon> iconCache = new Dictionary<int, Icon>();

    static Icon appIcon;

    /// <summary>exe に埋め込んだマルチサイズ icon.ico。ウィンドウ（タスクバー）用。
    /// 高 DPI でも OS が適切なサイズを選べる。無ければ実行時生成にフォールバック。</summary>
    public static Icon AppIcon
    {
        get
        {
            if (appIcon != null) return appIcon;
            try
            {
                var s = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("icon.ico");
                if (s != null) appIcon = new Icon(s);
            }
            catch { }
            if (appIcon == null) appIcon = BuildIcon(Theme.Accent);
            return appIcon;
        }
    }

    public static Icon BuildIcon(Color c)
    {
        Icon cached;
        if (iconCache.TryGetValue(c.ToArgb(), out cached)) return cached;
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
            var ic = Icon.FromHandle(bmp.GetHicon());
            iconCache[c.ToArgb()] = ic;
            return ic;
        }
    }

    void Quit()
    {
        tip.Stop();
        icon.Visible = false;
        compact.OnQuit = null;   // FormClosing の再入を防ぐ
        compact.Hide();
        SubMon.Shutdown();
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
                MessageBox.Show("gClaudeTokenMonitor は既に起動しています。",
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
