package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

// Recorder archives every billed message to an append-only NDJSON file, one
// file per day, plus a human-readable Markdown table.
//
// It covers every session at once: the scanner walks the whole projects tree,
// so any Claude Code window that exists — or is opened later — is picked up
// with no per-session setup.
//
// Restart safety comes from state.json, which persists the byte offset reached
// in each log. A restart resumes where it left off, so nothing is re-emitted
// and nothing is skipped. Duplicate lines (the log writes one response as
// several lines) are dropped via keys reloaded from the recent archive.
type Recorder struct {
	Dir  string
	sc   *Scanner
	seen map[string]int // dedup キー -> その日 (yyyymmdd)

	mu         sync.Mutex // usageNext / usageFails を守る（poll は別 goroutine）
	usageEvery time.Duration
	usageNext  time.Time
	usageFails int
	usageBusy  int32  // atomic: 1 = poll 実行中
	lastErr    string // 同じエラーを 200ms ごとに書かないための直近値
	skipUnlock bool   // panic 時はロックを残し、UI にクラッシュとして検知させる

	lockFile *os.File
	lockIntv time.Duration
	prunedOn int
	lastBeat time.Time
}

type recState struct {
	Offsets map[string]int64 `json:"offsets"`
	Updated string           `json:"updated"`
	Version string           `json:"version"`
}

// recEvent is one archived message. Field names follow GLOSSARY.md.
type recEvent struct {
	TS      string `json:"ts"`
	Session string `json:"session"`
	CWDName string `json:"cwd_name"`
	CWD     string `json:"cwd,omitempty"` // フルパス
	Model   string `json:"model"`
	Effort  string `json:"effort,omitempty"` // 推論エフォート (high / max など)
	Speed   string `json:"speed,omitempty"`  // standard / fast

	Input  int `json:"input"`
	CW5m   int `json:"cache_write_5m"`
	CW1h   int `json:"cache_write_1h"`
	CRead  int `json:"cache_read"`
	Output int `json:"output"`
	Think  int `json:"thinking_tokens,omitempty"` // output の内数
	Total  int `json:"total"`

	Cost   float64 `json:"cost_usd"`
	Priced bool    `json:"priced"`

	Key string `json:"key"` // 重複排除キー message.id|requestId
	// Prompt は必ず最後に置く。値に "total": のような文字列が入っても、
	// 素朴な先頭一致パーサ（UI 側）が実フィールドを先に見つけられるように。
	Prompt string `json:"prompt,omitempty"`
}

func defaultRecordDir() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return ".ctm"
	}
	return filepath.Join(home, ".ctm")
}

func NewRecorder(dir, root string) (*Recorder, error) {
	for _, d := range []string{dir, filepath.Join(dir, "events"), filepath.Join(dir, "daily")} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			return nil, err
		}
	}
	r := &Recorder{Dir: dir, sc: NewScanner(root, time.Time{}), seen: map[string]int{}}
	r.sc.PathTTL = 3 * time.Second // 発見は 3 秒ごと、追記チェックは毎 tick
	r.loadState()
	r.loadSeen()
	return r, nil
}

// dayNum turns "2006-01-02" into 20060102 for cheap age comparison.
func dayNum(d string) int {
	n := 0
	for _, c := range d {
		if c >= '0' && c <= '9' {
			n = n*10 + int(c-'0')
		}
	}
	return n
}

// pruneSeen drops dedup keys older than two days. Duplicate lines are written
// within seconds of each other, so nothing useful is lost, and a long-running
// recorder stops growing its memory without bound.
func (r *Recorder) pruneSeen(today int) {
	if len(r.seen) < 20000 || today == r.prunedOn {
		return
	}
	r.prunedOn = today
	cutoff := dayNum(time.Now().AddDate(0, 0, -2).Format("2006-01-02"))
	for k, d := range r.seen {
		if d < cutoff {
			delete(r.seen, k)
		}
	}
}

func (r *Recorder) statePath() string { return filepath.Join(r.Dir, "state.json") }
func (r *Recorder) lockPath() string  { return filepath.Join(r.Dir, "record.lock") }
func (r *Recorder) stopPath() string  { return filepath.Join(r.Dir, "record.stop") }

// acquireLock refuses to start when another recorder is already archiving into
// this directory. Two recorders would each keep their own offsets and append the
// same messages twice. The lock is a heartbeat file: a stale one (older than
// three intervals, i.e. the previous process died) is taken over.
func (r *Recorder) acquireLock(interval time.Duration) error {
	for attempt := 0; attempt < 2; attempt++ {
		// O_EXCL makes creation itself the exclusive gate: two racing recorders
		// cannot both succeed, unlike read-then-write.
		f, err := os.OpenFile(r.lockPath(), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
		if err == nil {
			r.lockFile = f
			r.lockIntv = interval
			return r.touchLock()
		}
		if !os.IsExist(err) {
			return err
		}
		b, rerr := os.ReadFile(r.lockPath())
		if rerr != nil {
			return rerr
		}
		var l recLock
		if json.Unmarshal(b, &l) == nil {
			if t, perr := time.Parse(time.RFC3339, l.Heartbeat); perr == nil {
				age := time.Since(t)
				if age < l.staleAfter() {
					if pidAlive(l.PID) {
						return fmt.Errorf("別のレコーダーが %s を記録中 (pid %d, %.0f 秒前に更新)。"+
							"二重記録を避けるため起動しない", r.Dir, l.PID, age.Seconds())
					}
					// heartbeat は新しいのにプロセスが居ない = クラッシュ。
					// 証跡を crash.log に残して引き継ぐ
					appendCrashLog(r.Dir, fmt.Sprintf(
						"[recorder] takeover: pid %d は消滅 (heartbeat %.0f 秒前) — クラッシュとみなして引き継ぐ",
						l.PID, age.Seconds()))
				} else {
					appendCrashLog(r.Dir, fmt.Sprintf(
						"[recorder] takeover: pid %d の heartbeat が %s 前で停止 — 引き継ぐ",
						l.PID, dur(age)))
				}
			}
		}
		// Stale or unparseable: drop it and retry the exclusive create once.
		os.Remove(r.lockPath())
	}
	return fmt.Errorf("ロックを取得できない: %s", r.lockPath())
}

func (r *Recorder) touchLock() error {
	b, err := json.MarshalIndent(recLock{
		PID: os.Getpid(), Heartbeat: time.Now().Format(time.RFC3339), Dir: r.Dir,
		IntervalSec: int(r.lockIntv / time.Second)}, "", "  ")
	if err != nil {
		return err
	}
	if r.lockFile != nil {
		// Truncate → Write だと空になる瞬間があり、30fps で監視している UI が
		// 「記録停止中」を誤検知して点滅する。固定長に空白パディングして
		// 先頭から 1 回で書けば、読者は常に完全な JSON を見る。
		const lockLen = 256
		if len(b) < lockLen {
			pad := make([]byte, lockLen)
			copy(pad, b)
			for i := len(b); i < lockLen; i++ {
				pad[i] = ' '
			}
			pad[lockLen-1] = '\n'
			b = pad
		}
		if _, err := r.lockFile.WriteAt(b, 0); err != nil {
			return err
		}
		return r.lockFile.Sync()
	}
	return os.WriteFile(r.lockPath(), b, 0o644)
}

func (r *Recorder) releaseLock() {
	if r.lockFile != nil {
		r.lockFile.Close()
		r.lockFile = nil
	}
	if r.skipUnlock {
		// panic 経由。ロックを残せば「ロック有り + プロセス消滅」になり、
		// UI の監視がクラッシュとして検知・自動復旧できる
		return
	}
	os.Remove(r.lockPath())
}

type recLock struct {
	PID         int    `json:"pid"`
	Heartbeat   string `json:"heartbeat"`
	Dir         string `json:"dir"`
	IntervalSec int    `json:"interval_sec"`
}

// staleAfter is how long without a heartbeat means the writer died. Readers and
// writers must agree on this, so it is derived from the interval in the lock.
func (l recLock) staleAfter() time.Duration {
	d := time.Duration(l.IntervalSec) * time.Second
	if d <= 0 {
		d = 20 * time.Second
	}
	if d < time.Minute {
		d = time.Minute
	}
	return 3 * d
}

func (r *Recorder) loadState() {
	b, err := os.ReadFile(r.statePath())
	if err != nil {
		return
	}
	var st recState
	if json.Unmarshal(b, &st) != nil || st.Offsets == nil {
		return
	}
	r.sc.offsets = st.Offsets
}

func (r *Recorder) saveState() error {
	st := recState{Offsets: r.sc.offsets, Updated: time.Now().Format(time.RFC3339), Version: version}
	b, err := json.MarshalIndent(st, "", "  ")
	if err != nil {
		return err
	}
	tmp := r.statePath() + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	// atomic: a crash mid-write cannot corrupt state
	return os.Rename(tmp, r.statePath())
}

// loadSeen rebuilds the dedup set from the last two days of archive. Duplicate
// lines are written within seconds of each other, so two days is ample.
func (r *Recorder) loadSeen() {
	now := time.Now()
	for _, d := range []string{now.Format("2006-01-02"), now.AddDate(0, 0, -1).Format("2006-01-02")} {
		f, err := os.Open(filepath.Join(r.Dir, "events", d+".ndjson"))
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 1<<20), 1<<20)
		for sc.Scan() {
			var e recEvent
			if json.Unmarshal(sc.Bytes(), &e) == nil && e.Key != "" {
				r.seen[e.Key] = dayNum(d)
			}
		}
		f.Close()
	}
}

// formatND renders one archived message as an ASCII NDJSON line.
func formatND(e Entry) ([]byte, error) {
	rec := recEvent{
		TS: e.TS.Format(time.RFC3339), Key: e.Key, Session: e.Session,
		CWDName: e.Project, CWD: e.CWD, Model: e.Model,
		Input: e.Input, CW5m: e.CacheWrite5m, CW1h: e.CacheWrite1h,
		CRead: e.CacheRead, Output: e.Output, Total: e.Total(),
		Cost: e.Cost, Priced: e.Known, Prompt: e.Prompt,
		Effort: e.Effort, Speed: e.Speed, Think: e.Think,
	}
	b, err := json.Marshal(rec)
	if err != nil {
		return nil, err
	}
	out := asciiEscape(b)
	return append(out, '\n'), nil
}

func formatMD(e Entry) string {
	return fmt.Sprintf("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | $%.6f | %s |\n",
		e.TS.Format("15:04:05"), trunc(e.Session, 8), e.Project, e.Model,
		comma(e.Input), comma(e.CacheWrite5m), comma(e.CacheWrite1h),
		comma(e.CacheRead), comma(e.Output), comma(e.Total()), e.Cost,
		mdSafe(TruncatePrompt(e.Prompt, 60)))
}

func mdHeader(day string) string {
	return fmt.Sprintf("# %s のトークン消費\n\n"+
		"1 行 = 重複排除後の課金単位 1 件。全セッションを対象に自動記録。\n\n"+
		"| 時刻 | セッション | 作業ディレクトリ | モデル | input | cache-write 5m | "+
		"cache-write 1h | cache-read | output | 合計 | コスト | 指示 |\n"+
		"|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|\n", day)
}

// appendDay writes one day's batch. On any failure both files are truncated
// back to their pre-write size, so a rolled-back tick can retry cleanly:
// no partial line survives, and the retried batch never double-appends.
func (r *Recorder) appendDay(day string, nd, md []byte) error {
	evPath := filepath.Join(r.Dir, "events", day+".ndjson")
	mdPath := filepath.Join(r.Dir, "daily", day+".md")

	evPre := fileSize(evPath)
	mdPre := fileSize(mdPath)
	if mdPre < 0 {
		md = append([]byte(mdHeader(day)), md...)
	}
	err := appendSync(evPath, nd)
	if err == nil {
		err = appendSync(mdPath, md)
	}
	if err != nil {
		truncateTo(evPath, evPre)
		truncateTo(mdPath, mdPre)
		return err
	}
	return nil
}

func fileSize(path string) int64 {
	st, err := os.Stat(path)
	if err != nil {
		return -1
	}
	return st.Size()
}

func appendSync(path string, b []byte) error {
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	if _, err := f.Write(b); err != nil {
		f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		f.Close()
		return err
	}
	return f.Close()
}

func truncateTo(path string, size int64) {
	if size < 0 {
		os.Remove(path)
		return
	}
	os.Truncate(path, size)
}

// appendCrashLog is the dedicated ledger of abnormal events: takeovers and
// crash-restarts, written by both the recorder and the UI supervisor.
func appendCrashLog(dir, line string) {
	f, err := os.OpenFile(filepath.Join(dir, "crash.log"),
		os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, "%s %s\n", time.Now().Format(time.RFC3339), line)
}

// mdSafe keeps a prompt from breaking the Markdown table.
func mdSafe(s string) string {
	return strings.ReplaceAll(s, "|", "\u2502")
}

func (r *Recorder) logf(format string, args ...any) {
	f, err := os.OpenFile(filepath.Join(r.Dir, "record.log"),
		os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, "%s "+format+"\n", append([]any{time.Now().Format(time.RFC3339)}, args...)...)
}

// tick reads everything appended since the last pass and archives it.
//
// Offsets are only persisted after the batch is safely on disk. If a write
// fails, the in-memory offsets are rolled back so the next pass re-reads the
// tick reads everything appended since the last pass and archives it.
//
// Offsets are only persisted after the batch is safely on disk. If a write
// fails the in-memory offsets are rolled back AND the day files are truncated
// to their pre-batch size, so the retried batch cannot duplicate lines.
func (r *Recorder) tick() (int, error) {
	before := make(map[string]int64, len(r.sc.offsets))
	for k, v := range r.sc.offsets {
		before[k] = v
	}

	var batch []Entry
	var fresh []string
	// 重複行は同一応答が数ミリ秒差で連続して書かれるため、ほぼ必ず同じ tick に
	// 入ってくる。確定済みの seen だけでなく、この batch 内でも弾くこと。
	// （これを怠ると再取り込みで全重複が素通りする。実際に一度やらかした）
	batchSeen := map[string]struct{}{}
	_, err := r.sc.Scan(func(e Entry, key string) bool {
		if key != "" {
			if _, dup := r.seen[key]; dup {
				return false
			}
			if _, dup := batchSeen[key]; dup {
				return false
			}
			batchSeen[key] = struct{}{}
		}
		batch = append(batch, e)
		fresh = append(fresh, key)
		return true
	})
	if err != nil {
		r.sc.offsets = before
		return 0, err
	}
	if len(batch) > 0 {
		sort.Slice(batch, func(i, j int) bool { return batch[i].TS.Before(batch[j].TS) })

		// 日別にメモリ上で組み立ててから、日ごとに 1 回で追記する
		type dayBuf struct{ nd, md []byte }
		bufs := map[string]*dayBuf{}
		var days []string
		for _, e := range batch {
			day := e.TS.Format("2006-01-02")
			db, ok := bufs[day]
			if !ok {
				db = &dayBuf{}
				bufs[day] = db
				days = append(days, day)
			}
			nd, err := formatND(e)
			if err != nil {
				continue
			}
			db.nd = append(db.nd, nd...)
			db.md = append(db.md, []byte(formatMD(e))...)
		}
		for _, day := range days {
			if err := r.appendDay(day, bufs[day].nd, bufs[day].md); err != nil {
				r.sc.offsets = before
				return 0, err
			}
		}
		// Only now is the batch durable, so remember the keys and keep offsets.
		today := dayNum(time.Now().Format("2006-01-02"))
		for _, k := range fresh {
			if k != "" {
				r.seen[k] = today
			}
		}
		r.pruneSeen(today)
		if err := r.saveState(); err != nil {
			r.logf("saveState: %v", err)
		}
	}
	// 変化の有無に関わらず、生存表明は 2 秒に 1 回だけ書く。
	if time.Since(r.lastBeat) >= 2*time.Second {
		if err := r.touchLock(); err != nil {
			r.logf("touchLock: %v", err)
		}
		r.lastBeat = time.Now()
	}
	return len(batch), nil
}

// maybePollUsage fires the plan-limit poll in its own goroutine so a slow
// HTTP round trip (up to 30s) can never stall ingestion or the heartbeat.
func (r *Recorder) maybePollUsage(quiet bool) {
	r.mu.Lock()
	due := !time.Now().Before(r.usageNext)
	r.mu.Unlock()
	if !due || !atomic.CompareAndSwapInt32(&r.usageBusy, 0, 1) {
		return
	}
	go func() {
		defer atomic.StoreInt32(&r.usageBusy, 0)
		samples, err := PollLimits(r.Dir)
		r.mu.Lock()
		defer r.mu.Unlock()
		if err != nil {
			r.usageFails++
			back := time.Duration(1<<minInt(r.usageFails, 4)) * r.usageEvery
			r.usageNext = time.Now().Add(back)
			r.logf("usage: %v (次回 %s 後)", err, dur(back))
			return
		}
		r.usageFails = 0
		r.usageNext = time.Now().Add(r.usageEvery)
		if !quiet {
			var parts []string
			for _, s := range samples {
				parts = append(parts, fmt.Sprintf("%s %.0f%%", s.Label, s.Percent))
			}
			fmt.Printf("\n%s  %s\n", time.Now().Format("15:04:05"), strings.Join(parts, " / "))
		}
	}()
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// RunRecord archives continuously until interrupted.
func RunRecord(dir, root string, interval, usageEvery time.Duration, quiet bool) error {
	r, err := NewRecorder(dir, root)
	if err != nil {
		return err
	}
	if err := r.acquireLock(interval); err != nil {
		return err
	}
	defer r.releaseLock()
	defer func() {
		if p := recover(); p != nil {
			// panic はロックを残して落ちる → UI がクラッシュとして自動復旧する
			appendCrashLog(r.Dir, fmt.Sprintf("[recorder] panic: %v", p))
			r.skipUnlock = true
			panic(p)
		}
	}()
	// 前回の停止要求が残っていると起動直後に自死するので、掃除してから始める。
	os.Remove(r.stopPath())
	r.usageEvery = usageEvery
	r.logf("start dir=%s root=%s interval=%s", dir, root, interval)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)

	if !quiet {
		fmt.Printf("ctm record\n  記録先: %s\n  対象  : %s (全セッション)\n  間隔  : %s\n\n",
			dir, root, interval)
	}

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	total := 0
	report := func(n int) {
		total += n
		if !quiet && n > 0 {
			fmt.Printf("\r%s  +%d 件 (累計 %d)      ",
				time.Now().Format("15:04:05"), n, total)
		}
	}

	n, err := r.tick()
	if err != nil {
		r.logf("tick: %v", err)
	}
	report(n)
	if usageEvery > 0 {
		r.maybePollUsage(quiet)
	}

	for {
		select {
		case <-sig:
			r.logf("stop total=%d", total)
			if !quiet {
				fmt.Printf("\n停止。累計 %d 件を記録した。\n", total)
			}
			return nil
		case <-ticker.C:
			if _, err := os.Stat(r.stopPath()); err == nil {
				os.Remove(r.stopPath())
				r.logf("stop requested total=%d", total)
				if !quiet {
					fmt.Printf("\n停止要求を受け取った。累計 %d 件を記録した。\n", total)
				}
				return nil
			}
			n, err := r.tick()
			if err != nil {
				// projects 未作成の新しいマシン等では同じエラーが続く。
				// 変化したときだけ書いて record.log を膨らませない
				if msg := err.Error(); msg != r.lastErr {
					r.lastErr = msg
					r.logf("tick: %v", err)
				}
				continue
			}
			r.lastErr = ""
			report(n)
			if usageEvery > 0 {
				r.maybePollUsage(quiet)
			}
		}
	}
}

// RecordStatus reports what the resident recorder is doing, without touching it.
func RecordStatus(dir string) error {
	b, err := os.ReadFile(filepath.Join(dir, "record.lock"))
	if err != nil {
		fmt.Printf("レコーダー: 停止中（%s にロックなし）\n", dir)
		return nil
	}
	var l recLock
	if err := json.Unmarshal(b, &l); err != nil {
		return err
	}
	hb, _ := time.Parse(time.RFC3339, l.Heartbeat)
	age := time.Since(hb)
	state := "稼働中"
	if age > 3*time.Minute {
		state = "応答なし（" + dur(age) + " 更新なし）"
	} else if !pidAlive(l.PID) {
		state = "クラッシュ（pid " + comma(l.PID) + " は消滅。UI が自動復旧するか、ctm record で再開）"
	}
	fmt.Printf("レコーダー: %s\n", state)
	fmt.Printf("  pid          %d\n", l.PID)
	fmt.Printf("  記録先       %s\n", l.Dir)
	fmt.Printf("  最終更新     %s (%s前)\n", hb.Format("2006-01-02 15:04:05"), dur(age))

	if b, err := os.ReadFile(filepath.Join(dir, "state.json")); err == nil {
		var st recState
		if json.Unmarshal(b, &st) == nil {
			fmt.Printf("  追跡中のログ %d 本\n", len(st.Offsets))
		}
	}

	day := time.Now().Format("2006-01-02")
	n, tok, cost, sess := scanDay(filepath.Join(dir, "events", day+".ndjson"))
	fmt.Printf("  本日の記録   %s メッセージ / %s トークン / %s / %d セッション\n",
		comma(n), tokens(tok), money(cost), sess)
	return nil
}

func scanDay(path string) (n, tok int, cost float64, sessions int) {
	f, err := os.Open(path)
	if err != nil {
		return
	}
	defer f.Close()
	seen := map[string]struct{}{}
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<20)
	for sc.Scan() {
		var e recEvent
		if json.Unmarshal(sc.Bytes(), &e) != nil {
			continue
		}
		n++
		tok += e.Total
		cost += e.Cost
		seen[e.Session] = struct{}{}
	}
	return n, tok, cost, len(seen)
}

// RecordStop asks the resident recorder to finish its current tick and exit.
func RecordStop(dir string) error {
	lock := filepath.Join(dir, "record.lock")
	if _, err := os.Stat(lock); err != nil {
		fmt.Println("レコーダーは動いていない")
		return nil
	}
	if err := os.WriteFile(filepath.Join(dir, "record.stop"), []byte("stop\n"), 0o644); err != nil {
		return err
	}
	fmt.Println("停止を要求した。次の間隔で終了する。")
	for i := 0; i < 60; i++ {
		time.Sleep(time.Second)
		if _, err := os.Stat(lock); err != nil {
			fmt.Println("停止を確認した。")
			return nil
		}
	}
	fmt.Println("60 秒待っても止まらない。間隔が長いか、プロセスが応答していない可能性がある。")
	return nil
}
