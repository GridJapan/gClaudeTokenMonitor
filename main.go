package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io/fs"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"time"
	"unicode/utf16"
	"unicode/utf8"
)

const version = "0.1.2"

// ctm is a subcommand CLI. Running it bare opens the live view, which is the
// common case; everything else is an explicit verb.
func main() {
	args := os.Args[1:]
	args = translateLegacy(args)

	cmd := "live"
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		cmd, args = args[0], args[1:]
	}

	var err error
	switch cmd {
	case "live":
		err = cmdLive(args)
	case "show":
		err = cmdShow(args)
	case "events":
		err = cmdEvents(args)
	case "record":
		err = cmdRecord(args)
	case "status":
		err = cmdRecordCtl(args, true)
	case "stop":
		err = cmdRecordCtl(args, false)
	case "query":
		err = cmdQuery(args)
	case "limits":
		err = cmdLimits(args)
	case "version":
		fmt.Println("ctm", version)
	case "help", "-h", "--help":
		usage(os.Stdout)
	default:
		fmt.Fprintf(os.Stderr, "不明なコマンド: %s\n\n", cmd)
		usage(os.Stderr)
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, cmd+":", err)
		os.Exit(1)
	}
}

func usage(w *os.File) {
	fmt.Fprint(w, `ctm — Claude Code のトークン消費を計測する

使い方:
  ctm [live] [flags]        リアルタイム表示（既定）
  ctm show   [flags]        集計を 1 回出力（-json でスクリプト向け）
  ctm events [flags]        1 メッセージ 1 行の NDJSON 明細
  ctm record [flags]        全セッションを常時アーカイブ（常駐）
  ctm status                常駐レコーダーの状態
  ctm stop                  常駐レコーダーを停止
  ctm query  -from t        アーカイブから期間を切り出して集計
  ctm limits [show|history] プラン使用制限（5時間 / 週次）の使用率と消費
  ctm version
  ctm help

計測の流れ:
  1. ctm record            を常駐させる（一度きり。以後なにもしない）
  2. 好きなだけ作業する    対象セッションには一切触れない
  3. ctm query -from 13:00 であとから期間を切り出す

各コマンドの詳細は "ctm <コマンド> -h"。
`)
}

// translateLegacy keeps the pre-subcommand flags working, so existing shortcuts
// and the startup launcher do not break.
func translateLegacy(args []string) []string {
	if len(args) == 0 || !strings.HasPrefix(args[0], "-") {
		return args
	}
	legacy := map[string]string{
		"-record": "record", "-record-status": "status", "-record-stop": "stop",
		"-events": "events", "-version": "version",
	}
	for i, a := range args {
		name := strings.SplitN(a, "=", 2)[0]
		if cmd, ok := legacy[name]; ok {
			rest := append([]string{}, args[:i]...)
			rest = append(rest, args[i+1:]...)
			return append([]string{cmd}, rest...)
		}
		if name == "-json" || name == "-once" {
			rest := append([]string{"show"}, args...)
			return rest
		}
	}
	return args
}

// commonFlags are the ones that describe *what* to read and how to price it.
type commonFlags struct {
	dir     *string
	pricing *string
	days    *int
	session *string
	since   *string
	nowOnly *bool
}

func addCommon(fs *flag.FlagSet) *commonFlags {
	return &commonFlags{
		dir:     fs.String("dir", defaultProjectsDir(), "Claude Code の projects ディレクトリ"),
		pricing: fs.String("pricing", "", "料金表を上書きする JSON ファイル"),
		days:    fs.Int("days", 0, "直近 N 日だけ集計 (0 = 全期間)"),
		session: fs.String("session", "", `1 セッションに絞る: ID 接頭辞 / "new" / "last"`),
		since:   fs.String("since", "", `基準時刻 ("15:04" / "2006-01-02 15:04:05" / RFC3339)`),
		nowOnly: fs.Bool("since-now", false, "起動時点以降だけ集計"),
	}
}

// prepare applies the common flags and returns a loaded store.
func (c *commonFlags) prepare() (*Scanner, *Store, *sessionFilter, func(Entry, string) bool, error) {
	if *c.pricing != "" {
		if err := LoadPricing(*c.pricing); err != nil {
			return nil, nil, nil, nil, fmt.Errorf("料金表: %w", err)
		}
	}
	if _, err := os.Stat(*c.dir); err != nil {
		return nil, nil, nil, nil, fmt.Errorf("%s を読めない: %w", *c.dir, err)
	}

	var since time.Time
	if *c.days > 0 {
		since = time.Now().AddDate(0, 0, -*c.days)
	}
	if *c.nowOnly {
		since = time.Now()
		filterLabel = "since-now"
	}
	if *c.since != "" {
		t, err := parseSince(*c.since, time.Now())
		if err != nil {
			return nil, nil, nil, nil, err
		}
		since = t
		filterLabel = "since " + t.Format("01-02 15:04:05")
	}

	filt, err := newFilter(*c.dir, *c.session)
	if err != nil {
		return nil, nil, nil, nil, err
	}
	st := NewStore()
	add := filt.wrap(st)
	sc := NewScanner(*c.dir, since)
	if _, err := sc.Scan(add); err != nil {
		return nil, nil, nil, nil, err
	}
	return sc, st, filt, add, nil
}

func cmdLive(args []string) error {
	fs := flag.NewFlagSet("live", flag.ExitOnError)
	c := addCommon(fs)
	interval := fs.Duration("interval", time.Second, "更新間隔")
	view := fs.Int("view", 1, "起動時のビュー 1-6")
	fs.Parse(args)

	sc, st, filt, add, err := c.prepare()
	if err != nil {
		return err
	}
	if filt.mode == "new" {
		fmt.Println("新しいセッションが現れるのを待っている…")
	}
	run(sc, st, filt, add, *interval, clampView(*view-1))
	return nil
}

func cmdShow(args []string) error {
	fs := flag.NewFlagSet("show", flag.ExitOnError)
	c := addCommon(fs)
	asJSON := fs.Bool("json", false, "JSON で出力（純 ASCII・スクリプト向け）")
	view := fs.Int("view", 1, "描画するビュー 1-6")
	fs.Bool("once", true, "（既定の挙動。互換のため残置）")
	fs.Parse(args)

	sc, st, filt, _, err := c.prepare()
	if err != nil {
		return err
	}
	if filt.mode == "new" {
		return fmt.Errorf(`-session new は live 専用。終わった分を見るなら -session last か ID 接頭辞`)
	}
	if *asJSON {
		emitJSON(st.Snapshot(time.Now(), sc.Files))
		return nil
	}
	w, _ := termSize()
	fmt.Print(Render(st.Snapshot(time.Now(), sc.Files), clampView(*view-1), w))
	return nil
}

func cmdEvents(args []string) error {
	fs := flag.NewFlagSet("events", flag.ExitOnError)
	c := addCommon(fs)
	fs.Parse(args)

	_, st, filt, _, err := c.prepare()
	if err != nil {
		return err
	}
	if filt.mode == "new" {
		return fmt.Errorf(`-session new は live 専用`)
	}
	emitEvents(st)
	return nil
}

func cmdRecord(args []string) error {
	fs := flag.NewFlagSet("record", flag.ExitOnError)
	dir := fs.String("dir", defaultProjectsDir(), "Claude Code の projects ディレクトリ")
	out := fs.String("out", defaultRecordDir(), "アーカイブの保存先")
	interval := fs.Duration("interval", 200*time.Millisecond, "取り込み間隔（差分読み。変化が無い tick は Stat だけで終わる）")
	quiet := fs.Bool("quiet", false, "何も表示しない（常駐サービス用）")
	usage := fs.Duration("usage-interval", 5*time.Minute,
		"プラン使用率を取得する間隔 (0 で無効。API 側が絞るので短くしすぎない)")
	pricing := fs.String("pricing", "", "料金表を上書きする JSON ファイル")
	fs.Parse(args)

	if *interval <= 0 {
		return fmt.Errorf("-interval は正の値を指定する")
	}
	if *pricing != "" {
		if err := LoadPricing(*pricing); err != nil {
			return fmt.Errorf("料金表: %w", err)
		}
	}
	return RunRecord(*out, *dir, *interval, *usage, *quiet)
}

func cmdRecordCtl(args []string, status bool) error {
	name := "stop"
	if status {
		name = "status"
	}
	fs := flag.NewFlagSet(name, flag.ExitOnError)
	out := fs.String("out", defaultRecordDir(), "アーカイブの保存先")
	fs.Parse(args)
	if status {
		return RecordStatus(*out)
	}
	return RecordStop(*out)
}

func cmdLimits(args []string) error {
	sub := ""
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		sub, args = args[0], args[1:]
	}
	fs := flag.NewFlagSet("limits", flag.ExitOnError)
	archive := fs.String("archive", defaultRecordDir(), "アーカイブの場所")
	day := fs.String("day", "", `history 用の日付 "2026-08-22"（既定: 今日）`)
	win := fs.String("w", "", "history を 1 つの窓に絞る (session / weekly_all / weekly-fable)")
	fs.Parse(args)

	switch sub {
	case "", "show":
		return ShowLimits(*archive)
	case "history":
		return LimitHistory(*archive, *day, *win)
	default:
		return fmt.Errorf("不明なサブコマンド: %s（show / history）", sub)
	}
}

func cmdQuery(args []string) error {
	fs := flag.NewFlagSet("query", flag.ExitOnError)
	out := fs.String("archive", defaultRecordDir(), "アーカイブの場所")
	from := fs.String("from", "", `開始時刻 "13:00" / "2026-08-22 13:00"（必須）`)
	to := fs.String("to", "", "終了時刻（既定: 現在）")
	session := fs.String("session", "", "このセッション ID 接頭辞だけに絞る")
	exclude := fs.String("exclude", "", "このセッション ID 接頭辞を除く")
	save := fs.String("save", "", "切り出しをこのディレクトリに保存")
	fs.Parse(args)

	if *from == "" {
		fs.Usage()
		return fmt.Errorf("-from は必須")
	}
	now := time.Now()
	f, err := parseWhen(*from, now)
	if err != nil {
		return err
	}
	t := now
	if *to != "" {
		if t, err = parseWhen(*to, now); err != nil {
			return err
		}
	}
	return RunQuery(QueryOpts{Dir: *out, From: f, To: t,
		Session: *session, Exclude: *exclude, Out: *save})
}

// sessionFilter narrows every view down to a single Claude Code session.
//
// Mode "new" is the one that answers "how much does that other window cost?":
// it records which session logs already exist at startup, then latches onto the
// first log file that appears afterwards — i.e. the session you launch next.
type sessionFilter struct {
	root   string
	mode   string // "", "fixed", "new"
	target string // sessionId prefix once known
	known  map[string]bool
}

func newFilter(root, spec string) (*sessionFilter, error) {
	f := &sessionFilter{root: root}
	switch spec {
	case "":
		return f, nil
	case "new":
		f.mode = "new"
		f.known = map[string]bool{}
		for _, id := range sessionIDs(root) {
			f.known[id] = true
		}
		filterLabel = "waiting for a new session"
	case "last":
		ids := sessionIDs(root)
		if len(ids) == 0 {
			return nil, fmt.Errorf("no session logs under %s", root)
		}
		f.mode, f.target = "fixed", newestSession(root)
		filterLabel = "session " + trunc(f.target, 8)
	default:
		f.mode, f.target = "fixed", spec
		filterLabel = "session " + trunc(spec, 12)
	}
	return f, nil
}

// latch looks for a session log that did not exist at startup. Call it before
// each Scan so the new file is claimed before its first entries are read.
func (f *sessionFilter) latch() {
	if f.mode != "new" || f.target != "" {
		return
	}
	for _, id := range sessionIDs(f.root) {
		if !f.known[id] {
			f.target = id
			filterLabel = "new session " + trunc(id, 8)
			return
		}
	}
}

func (f *sessionFilter) wrap(st *Store) func(Entry, string) bool {
	if f.mode == "" {
		return st.Add
	}
	return func(e Entry, key string) bool {
		if f.target == "" || !strings.HasPrefix(e.Session, f.target) {
			return false
		}
		return st.Add(e, key)
	}
}

// sessionIDs lists every session log under root. Claude Code names each log
// after its sessionId, so the file name is the id.
func sessionIDs(root string) []string {
	var out []string
	filepath.WalkDir(root, func(p string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(d.Name(), ".jsonl") {
			return nil
		}
		out = append(out, strings.TrimSuffix(d.Name(), ".jsonl"))
		return nil
	})
	return out
}

func newestSession(root string) string {
	var best string
	var bestMod time.Time
	filepath.WalkDir(root, func(p string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(d.Name(), ".jsonl") {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return nil
		}
		if info.ModTime().After(bestMod) {
			bestMod, best = info.ModTime(), strings.TrimSuffix(d.Name(), ".jsonl")
		}
		return nil
	})
	return best
}

// parseSince accepts a full timestamp or a bare wall-clock time, which is
// resolved against today (or yesterday, if that would be in the future).
func parseSince(v string, now time.Time) (time.Time, error) {
	for _, layout := range []string{time.RFC3339, "2006-01-02 15:04:05", "2006-01-02 15:04", "2006-01-02"} {
		if t, err := time.ParseInLocation(layout, v, time.Local); err == nil {
			return t, nil
		}
	}
	for _, layout := range []string{"15:04:05", "15:04"} {
		t, err := time.ParseInLocation(layout, v, time.Local)
		if err != nil {
			continue
		}
		out := time.Date(now.Year(), now.Month(), now.Day(),
			t.Hour(), t.Minute(), t.Second(), 0, time.Local)
		if out.After(now) {
			out = out.AddDate(0, 0, -1)
		}
		return out, nil
	}
	return time.Time{}, fmt.Errorf("cannot parse %q as a time", v)
}

func clampView(v int) int {
	if v < 0 || v >= viewCount {
		return viewLive
	}
	return v
}

func run(sc *Scanner, st *Store, filt *sessionFilter, add func(Entry, string) bool, interval time.Duration, view int) {
	con := setupConsole()
	fmt.Print("\x1b[?25l\x1b[2J") // hide cursor, clear screen
	defer func() {
		fmt.Print("\x1b[?25h\x1b[0m\n")
		con.restore()
	}()

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt)

	keys := make(chan byte, 16)
	go readKeys(keys)

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	draw := func() {
		w, _ := termSize()
		fmt.Print("\x1b[H" + Render(st.Snapshot(time.Now(), sc.Files), view, w))
	}
	draw()

	for {
		select {
		case <-sig:
			return
		case k := <-keys:
			switch k {
			case 'q', 'Q', 3, 27:
				return
			case 'r', 'R':
				filt.latch()
				sc.Scan(add)
			case '1', '2', '3', '4', '5', '6':
				view = clampView(int(k - '1'))
			case '\t':
				view = (view + 1) % viewCount
			}
			draw()
		case <-ticker.C:
			filt.latch()
			sc.Scan(add)
			draw()
		}
	}
}

func readKeys(out chan<- byte) {
	buf := make([]byte, 16)
	for {
		n, err := os.Stdin.Read(buf)
		if err != nil {
			return
		}
		for _, b := range buf[:n] {
			if b == '\r' || b == '\n' {
				continue
			}
			select {
			case out <- b:
			default:
			}
		}
	}
}

type jsonOut struct {
	GeneratedAt  string  `json:"generated_at"`
	LogFiles     int     `json:"log_files"`
	Total        Agg     `json:"total"`
	ByModel      []KV    `json:"by_model"`
	ByProject    []KV    `json:"by_project"`
	BySession    []KV    `json:"by_session"`
	ByDay        []KV    `json:"by_day"`
	CurrentBlock *Block  `json:"current_block"`
	BurnPerMin   float64 `json:"burn_tokens_per_min"`
	CostPerMin   float64 `json:"burn_cost_per_min"`
}

// eventOut is one priced message, for auditing a run message by message.
type eventOut struct {
	TS      string  `json:"ts"`
	Key     string  `json:"key"`
	Session string  `json:"session"`
	Project string  `json:"project"`
	CWD     string  `json:"cwd,omitempty"`
	Model   string  `json:"model"`
	Input   int     `json:"input"`
	CW5m    int     `json:"cache_write_5m"`
	CW1h    int     `json:"cache_write_1h"`
	CRead   int     `json:"cache_read"`
	Output  int     `json:"output"`
	Total   int     `json:"total"`
	Cost    float64 `json:"cost_usd"`
	Priced  bool    `json:"priced"`
	Effort  string  `json:"effort,omitempty"`
	Speed   string  `json:"speed,omitempty"`
	Think   int     `json:"thinking_tokens,omitempty"`
	Prompt  string  `json:"prompt,omitempty"`
}

// emitEvents writes one NDJSON line per deduplicated message, oldest first.
func emitEvents(s *Store) {
	s.sortEntries()
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	for _, e := range s.entries {
		enc.Encode(eventOut{
			TS:      e.TS.Format(time.RFC3339Nano),
			Key:     e.Key,
			Session: e.Session,
			Project: e.Project,
			CWD:     e.CWD,
			Model:   e.Model,
			Input:   e.Input,
			CW5m:    e.CacheWrite5m,
			CW1h:    e.CacheWrite1h,
			CRead:   e.CacheRead,
			Output:  e.Output,
			Total:   e.Total(),
			Cost:    e.Cost,
			Priced:  e.Known,
			Effort:  e.Effort,
			Speed:   e.Speed,
			Think:   e.Think,
			Prompt:  e.Prompt,
		})
	}
	os.Stdout.Write(asciiEscape(buf.Bytes()))
}

func emitJSON(s *Snapshot) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetIndent("", "  ")
	enc.Encode(jsonOut{
		GeneratedAt:  s.Now.Format(time.RFC3339),
		LogFiles:     s.Files,
		Total:        s.Total,
		ByModel:      s.Models,
		ByProject:    s.Projects,
		BySession:    s.Sessions,
		ByDay:        s.Days,
		CurrentBlock: s.Cur,
		BurnPerMin:   s.BurnTokens,
		CostPerMin:   s.BurnCost,
	})
	os.Stdout.Write(asciiEscape(buf.Bytes()))
}

// asciiEscape rewrites every non-ASCII rune as a \uXXXX escape. Project names
// are often Japanese, and a Windows console on a legacy code page mangles raw
// UTF-8 on the way into PowerShell's ConvertFrom-Json. Pure-ASCII JSON survives
// any code page and decodes back to the same string in every parser.
func asciiEscape(b []byte) []byte {
	if utf8.Valid(b) && !hasNonASCII(b) {
		return b
	}
	var out bytes.Buffer
	out.Grow(len(b))
	for _, r := range string(b) {
		switch {
		case r < utf8.RuneSelf:
			out.WriteByte(byte(r))
		case r > 0xFFFF:
			hi, lo := utf16.EncodeRune(r)
			fmt.Fprintf(&out, "\\u%04x\\u%04x", hi, lo)
		default:
			fmt.Fprintf(&out, "\\u%04x", r)
		}
	}
	return out.Bytes()
}

func hasNonASCII(b []byte) bool {
	for _, c := range b {
		if c >= utf8.RuneSelf {
			return true
		}
	}
	return false
}
