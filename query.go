package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// Querying the archive is how measurement works once `ctm record` is resident:
// everything is already recorded, so a measurement is just a window cut out
// afterwards. Nothing is asked of the session being measured.

const (
	gapMinutes = 10     // longer than this between messages counts as a 中断
	resumeCW   = 50_000 // cache-read 0 with a write this big = cache expiry rebuild
)

// QueryOpts selects a window of the archive.
type QueryOpts struct {
	Dir     string
	From    time.Time
	To      time.Time
	Session string // session id prefix to keep
	Exclude string // session id prefix to drop
	Out     string // if set, write the slice here
}

// LoadArchive reads only the day files the window touches.
func LoadArchive(o QueryOpts) ([]recEvent, error) {
	var out []recEvent
	for d := o.From; !d.After(o.To); d = d.AddDate(0, 0, 1) {
		p := filepath.Join(o.Dir, "events", d.Format("2006-01-02")+".ndjson")
		f, err := os.Open(p)
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 1<<20), 1<<20)
		for sc.Scan() {
			var e recEvent
			if json.Unmarshal(sc.Bytes(), &e) != nil {
				continue
			}
			t, err := time.Parse(time.RFC3339, e.TS)
			if err != nil || t.Before(o.From) || t.After(o.To) {
				continue
			}
			if o.Session != "" && !strings.HasPrefix(e.Session, o.Session) {
				continue
			}
			if o.Exclude != "" && strings.HasPrefix(e.Session, o.Exclude) {
				continue
			}
			out = append(out, e)
		}
		f.Close()
	}
	sort.Slice(out, func(i, j int) bool { return evTime(out[i]).Before(evTime(out[j])) })
	return out, nil
}

func evTime(e recEvent) time.Time {
	t, _ := time.Parse(time.RFC3339, e.TS)
	return t
}

// splitClusters groups messages into runs of activity, splitting on any gap
// longer than gapMinutes. Elapsed time and active time differ enormously when
// work is interrupted, so the two are always reported separately.
func splitClusters(rows []recEvent) [][]recEvent {
	if len(rows) == 0 {
		return nil
	}
	var out [][]recEvent
	cur := []recEvent{rows[0]}
	for i := 1; i < len(rows); i++ {
		if evTime(rows[i]).Sub(evTime(rows[i-1])) > gapMinutes*time.Minute {
			out = append(out, cur)
			cur = []recEvent{rows[i]}
		} else {
			cur = append(cur, rows[i])
		}
	}
	return append(out, cur)
}

type querySum struct {
	N                          int
	Tok                        int
	Cost                       float64
	In, CW5m, CW1h, CRead, Out int
	Elapsed, Active            time.Duration
	Breaks                     int
	ResumeN                    int
	ResumeCost                 float64
	Clusters                   [][]recEvent
}

func summarize(rows []recEvent) querySum {
	var s querySum
	s.N = len(rows)
	if s.N == 0 {
		return s
	}
	for _, e := range rows {
		s.Tok += e.Total
		s.Cost += e.Cost
		s.In += e.Input
		s.CW5m += e.CW5m
		s.CW1h += e.CW1h
		s.CRead += e.CRead
		s.Out += e.Output
		if e.CRead == 0 && e.CW1h+e.CW5m >= resumeCW {
			s.ResumeN++
			s.ResumeCost += e.Cost
		}
	}
	s.Elapsed = evTime(rows[len(rows)-1]).Sub(evTime(rows[0]))
	s.Clusters = splitClusters(rows)
	s.Breaks = len(s.Clusters) - 1
	for _, c := range s.Clusters {
		d := evTime(c[len(c)-1]).Sub(evTime(c[0]))
		if d < 12*time.Second {
			d = 12 * time.Second // a single-message cluster still took some time
		}
		s.Active += d
	}
	return s
}

func printSummary(rows []recEvent, label string) {
	s := summarize(rows)
	if s.N == 0 {
		fmt.Printf("%s: 該当なし\n", label)
		return
	}
	am := s.Active.Minutes()
	fmt.Printf("\n== %s ==\n", label)
	fmt.Printf("  メッセージ   %s\n", comma(s.N))
	fmt.Printf("  合計トークン %s\n", comma(s.Tok))
	for _, kv := range []struct {
		name string
		v    int
	}{{"input", s.In}, {"cache-write 5m", s.CW5m}, {"cache-write 1h", s.CW1h},
		{"cache-read", s.CRead}, {"output", s.Out}} {
		fmt.Printf("    %-16s %14s  %5.1f%%\n", kv.name, comma(kv.v), pct(kv.v, s.Tok))
	}
	fmt.Printf("  コスト       %s\n", money(s.Cost))
	fmt.Printf("  経過         %s   実稼働 %s   中断 %d 回\n",
		dur(s.Elapsed), dur(s.Active), s.Breaks)
	fmt.Printf("  トークン速度 %s tok/min   コスト速度 $%.3f/min  (実稼働ベース)\n",
		comma(int(float64(s.Tok)/am)), s.Cost/am)
	fmt.Printf("  1 メッセージ %s tok / $%.4f\n", comma(s.Tok/s.N), s.Cost/float64(s.N))

	if s.ResumeN > 0 && s.Cost > 0 {
		fmt.Printf("  %sキャッシュ失効による再構築 %d 件 %s (全体の %.1f%%)%s\n",
			cYell, s.ResumeN, money(s.ResumeCost), s.ResumeCost/s.Cost*100, cReset)
		for _, e := range rows {
			if e.CRead == 0 && e.CW1h+e.CW5m >= resumeCW {
				fmt.Printf("      %s  cache-write %s  %s\n",
					evTime(e).Format("01-02 15:04:05"), comma(e.CW1h+e.CW5m), money(e.Cost))
			}
		}
	}
	if s.Breaks > 0 {
		fmt.Println("  稼働クラスタ")
		for _, c := range s.Clusters {
			var cc float64
			for _, e := range c {
				cc += e.Cost
			}
			fmt.Printf("      %s -> %s  %6s  %3d 件  %9s\n",
				evTime(c[0]).Format("01-02 15:04"), evTime(c[len(c)-1]).Format("15:04"),
				dur(evTime(c[len(c)-1]).Sub(evTime(c[0]))), len(c), money(cc))
		}
	}
}

func printBySession(rows []recEvent) {
	g := map[string][]recEvent{}
	for _, e := range rows {
		g[e.Session] = append(g[e.Session], e)
	}
	if len(g) < 2 {
		return
	}
	type row struct {
		sid  string
		rs   []recEvent
		cost float64
	}
	var list []row
	for sid, rs := range g {
		var c float64
		for _, e := range rs {
			c += e.Cost
		}
		list = append(list, row{sid, rs, c})
	}
	sort.Slice(list, func(i, j int) bool { return list[i].cost > list[j].cost })

	fmt.Printf("\n== セッション別 ==\n")
	fmt.Printf("  %-10s %-22s %7s %14s %10s  %s\n",
		"セッション", "作業ディレクトリ", "件数", "トークン", "コスト", "期間")
	for _, r := range list {
		var tok int
		for _, e := range r.rs {
			tok += e.Total
		}
		fmt.Printf("  %-10s %-22s %7s %14s %10s  %s -> %s\n",
			trunc(r.sid, 8), trunc(r.rs[len(r.rs)-1].CWDName, 22), comma(len(r.rs)),
			comma(tok), money(r.cost),
			evTime(r.rs[0]).Format("01-02 15:04"), evTime(r.rs[len(r.rs)-1]).Format("15:04"))
	}
}

// RunQuery cuts a window out of the archive and reports it.
func RunQuery(o QueryOpts) error {
	rows, err := LoadArchive(o)
	if err != nil {
		return err
	}
	fmt.Printf("期間: %s -> %s\n",
		o.From.Format("2006-01-02 15:04:05"), o.To.Format("2006-01-02 15:04:05"))
	if o.Session != "" {
		fmt.Printf("対象: セッション %s\n", o.Session)
	}
	if o.Exclude != "" {
		fmt.Printf("除外: セッション %s\n", o.Exclude)
	}
	if len(rows) == 0 {
		fmt.Println("該当メッセージなし")
		return nil
	}
	printSummary(rows, "全体")
	printBySession(rows)

	if o.Out != "" {
		if err := os.MkdirAll(o.Out, 0o755); err != nil {
			return err
		}
		p := filepath.Join(o.Out, "events.ndjson")
		f, err := os.Create(p)
		if err != nil {
			return err
		}
		defer f.Close()
		w := bufio.NewWriter(f)
		for _, e := range rows {
			b, err := json.Marshal(e)
			if err != nil {
				continue
			}
			w.Write(asciiEscape(b))
			w.WriteByte('\n')
		}
		w.Flush()
		fmt.Printf("\n切り出しを保存: %s\n", p)
	}
	return nil
}

func pct(v, total int) float64 {
	if total == 0 {
		return 0
	}
	return float64(v) / float64(total) * 100
}

// parseWhen accepts a full timestamp or a bare wall-clock time, which resolves
// against today (or yesterday, when that would be in the future).
func parseWhen(v string, now time.Time) (time.Time, error) {
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
	return time.Time{}, fmt.Errorf("時刻として解釈できない: %q", v)
}
