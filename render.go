package main

import (
	"fmt"
	"strings"
	"time"
)

const (
	cReset = "\x1b[0m"
	cBold  = "\x1b[1m"
	cRed   = "\x1b[91m"
	cGreen = "\x1b[92m"
	cYell  = "\x1b[93m"
	cCyan  = "\x1b[96m"
	cWhite = "\x1b[97m"
	cGray  = "\x1b[90m"
)

// View identifiers.
const (
	viewLive = iota
	viewDaily
	viewModels
	viewProjects
	viewSessions
	viewBlocks
	viewCount
)

var viewNames = []string{"LIVE", "DAILY", "MODELS", "PROJECTS", "SESSIONS", "BLOCKS"}

var sparkChars = []rune("▁▂▃▄▅▆▇█")

// filterLabel describes any active -session / -since-now narrowing so the
// header can say that the numbers are not the full picture.
var filterLabel string

// Render builds the whole frame for the given view.
func Render(s *Snapshot, view, w int) string {
	if w < 72 {
		w = 72
	}
	var b strings.Builder
	b.WriteString(header(s, view, w))

	var body []string
	switch view {
	case viewDaily:
		body = viewTable(s.Days, "DATE", w, 20)
	case viewModels:
		body = viewTable(s.Models, "MODEL", w, 20)
	case viewProjects:
		body = viewTable(s.Projects, "PROJECT", w, 20)
	case viewSessions:
		body = sessionsView(s, w)
	case viewBlocks:
		body = blocksView(s, w)
	default:
		body = liveView(s, w)
	}
	for _, line := range body {
		b.WriteString(line)
		b.WriteString("\x1b[K\n")
	}
	b.WriteString(footer(s, w))
	return b.String()
}

func header(s *Snapshot, view, w int) string {
	title := cBold + cCyan + " Claude Token Monitor" + cReset
	right := cGray + s.Now.Format("2006-01-02 15:04:05") + " " + cReset
	tabs := ""
	for i, n := range viewNames {
		if i == view {
			tabs += cBold + cWhite + "[" + n + "]" + cReset + " "
		} else {
			tabs += cGray + fmt.Sprint(i+1) + ":" + n + cReset + " "
		}
	}
	if filterLabel != "" {
		title += cYell + "  [" + filterLabel + "]" + cReset
	}
	pad := maxi(1, w-dispWidth(title)-dispWidth(right))
	l1 := title + strings.Repeat(" ", pad) + right
	return l1 + "\x1b[K\n" +
		" " + tabs + "\x1b[K\n" +
		cGray + " " + strings.Repeat("-", maxi(1, w-2)) + cReset + "\x1b[K\n"
}

func footer(s *Snapshot, w int) string {
	warn := ""
	if len(s.Unknown) > 0 {
		names := make([]string, 0, len(s.Unknown))
		for m := range s.Unknown {
			names = append(names, m)
		}
		warn = cYell + " no price for: " + strings.Join(names, ", ") + cReset
	}
	keys := cGray + " [1-6] view  [r] rescan  [q] quit" + cReset
	files := fmt.Sprintf("%s%d log files%s", cGray, s.Files, cReset)
	pad := maxi(1, w-dispWidth(keys)-dispWidth(files)-1)
	line := keys + strings.Repeat(" ", pad) + files
	return cGray + " " + strings.Repeat("-", maxi(1, w-2)) + cReset + "\x1b[K\n" +
		line + "\x1b[K\n" + warn + "\x1b[K\n\x1b[J"
}

func liveView(s *Snapshot, w int) []string {
	t := s.Total
	out := []string{
		"",
		fmt.Sprintf(" %sTOTAL%s   %s%s%s tokens    %s%s%s    %s%s msgs%s",
			cBold+cWhite, cReset,
			cBold+cGreen, tokens(t.Total()), cReset,
			cBold+cGreen, money(t.Cost), cReset,
			cGray, comma(t.Messages), cReset),
		fmt.Sprintf(" %s        in %s | cache-w %s | cache-r %s | out %s%s",
			cGray, tokens(t.Input), tokens(t.CacheWrite()), tokens(t.CacheRead), tokens(t.Output), cReset),
		"",
	}

	if s.Cur != nil {
		c := s.Cur
		elapsed := s.Now.Sub(c.Start)
		frac := float64(elapsed) / float64(blockLen)
		remain := c.End.Sub(s.Now)
		projTok := float64(c.Total()) + s.BurnTokens*remain.Minutes()
		projCost := c.Cost + s.BurnCost*remain.Minutes()

		out = append(out,
			fmt.Sprintf(" %sCURRENT BLOCK (5h)%s  %s%s -> %s%s  %s %s%3.0f%%%s",
				cBold+cWhite, cReset,
				cCyan, c.Start.Format("15:04"), c.End.Format("15:04"), cReset,
				bar(frac, maxi(10, w-52)), cCyan, frac*100, cReset),
			fmt.Sprintf("   tokens %s%s%s   cost %s%s%s   burn %s%s tok/min%s   %s left",
				cBold, tokens(c.Total()), cReset,
				cBold, money(c.Cost), cReset,
				cBold, comma(int(s.BurnTokens)), cReset,
				dur(remain)),
			fmt.Sprintf("   %sprojected at block end: %s tokens / %s%s",
				cGray, tokens(int(projTok)), money(projCost), cReset),
			"")
	} else {
		out = append(out, " "+cGray+"no active 5h block"+cReset, "")
	}

	out = append(out,
		fmt.Sprintf(" %sLAST 60 MIN%s  %s  %speak %s/min%s",
			cBold+cWhite, cReset, sparkline(s.Spark), cGray, tokens(maxSlice(s.Spark)), cReset),
		"")

	today := s.Now.Format("2006-01-02")
	for _, d := range s.Days {
		if d.Key == today {
			out = append(out, fmt.Sprintf(" %sTODAY%s   %s tokens   %s   %s msgs   %s%d active sessions%s",
				cBold+cWhite, cReset, tokens(d.Agg.Total()), money(d.Agg.Cost), comma(d.Agg.Messages),
				cGray, s.ActiveSessions, cReset), "")
			break
		}
	}

	out = append(out, " "+cBold+cWhite+"TOP MODELS"+cReset)
	out = append(out, shareRows(s.Models, s.Total.Cost, w, 4)...)
	out = append(out, "", " "+cBold+cWhite+"RECENT"+cReset)
	for _, e := range s.Recent {
		out = append(out, fmt.Sprintf("   %s%s%s  %-22s %10s  %9s  %s%s%s",
			cGray, e.TS.Format("15:04:05"), cReset,
			trunc(e.Model, 22), tokens(e.Total()), money(e.Cost),
			cGray, trunc(e.Project, maxi(5, w-66)), cReset))
	}
	return out
}

func shareRows(rows []KV, total float64, w, n int) []string {
	var out []string
	for i, kv := range rows {
		if i >= n {
			break
		}
		share := 0.0
		if total > 0 {
			share = kv.Agg.Cost / total
		}
		out = append(out, fmt.Sprintf("   %-24s %s %s%5.1f%%%s %10s  %9s",
			trunc(kv.Key, 24), bar(share, maxi(8, w-64)), cGray, share*100, cReset,
			tokens(kv.Agg.Total()), money(kv.Agg.Cost)))
	}
	return out
}

func viewTable(rows []KV, label string, w, limit int) []string {
	out := []string{"",
		fmt.Sprintf(" %s%-26s %11s %11s %11s %11s %10s %9s%s",
			cBold+cWhite, label, "INPUT", "CACHE-W", "CACHE-R", "OUTPUT", "TOTAL", "COST", cReset),
		" " + cGray + strings.Repeat("-", maxi(10, w-2)) + cReset,
	}
	for i, kv := range rows {
		if i >= limit {
			out = append(out, fmt.Sprintf(" %s... %d more%s", cGray, len(rows)-limit, cReset))
			break
		}
		a := kv.Agg
		out = append(out, fmt.Sprintf(" %-26s %11s %11s %11s %11s %10s %s%9s%s",
			trunc(kv.Key, 26), tokens(a.Input), tokens(a.CacheWrite()), tokens(a.CacheRead),
			tokens(a.Output), tokens(a.Total()), cGreen, money(a.Cost), cReset))
	}
	var tot Agg
	for _, kv := range rows {
		a := kv.Agg
		tot.Input += a.Input
		tot.CacheWrite5m += a.CacheWrite5m
		tot.CacheWrite1h += a.CacheWrite1h
		tot.CacheRead += a.CacheRead
		tot.Output += a.Output
		tot.Cost += a.Cost
	}
	out = append(out,
		" "+cGray+strings.Repeat("-", maxi(10, w-2))+cReset,
		fmt.Sprintf(" %s%-26s %11s %11s %11s %11s %10s %9s%s",
			cBold, "TOTAL", tokens(tot.Input), tokens(tot.CacheWrite()), tokens(tot.CacheRead),
			tokens(tot.Output), tokens(tot.Total()), money(tot.Cost), cReset))
	return out
}

func sessionsView(s *Snapshot, w int) []string {
	// データ行は「スペース + 2 文字マーク」で始まりヘッダより 1 文字広い。
	// 幅いっぱいに作ると 1 文字はみ出して全行が折り返すので、行側の 85 を基準にする
	projW := maxi(8, w-85)
	out := []string{"",
		fmt.Sprintf("  %s%-38s %-*s %11s %11s %8s %9s%s",
			cBold+cWhite, "SESSION", projW, "PROJECT", "LAST", "TOKENS", "MSGS", "COST", cReset),
		" " + cGray + strings.Repeat("-", maxi(10, w-2)) + cReset,
	}
	for i, kv := range s.Sessions {
		if i >= 20 {
			out = append(out, fmt.Sprintf(" %s... %d more%s", cGray, len(s.Sessions)-20, cReset))
			break
		}
		mark := "  "
		if s.Now.Sub(kv.Agg.Last) <= activeIdle {
			mark = cGreen + "* " + cReset
		}
		last := kv.Agg.Last.Format("15:04:05")
		if kv.Agg.Last.Format("2006-01-02") != s.Now.Format("2006-01-02") {
			last = kv.Agg.Last.Format("01/02 15:04")
		}
		out = append(out, fmt.Sprintf(" %s%-38s %s%-*s%s %11s %11s %8s %s%9s%s",
			mark, trunc(kv.Key, 38), cGray, projW, trunc(kv.Note, projW), cReset, last,
			tokens(kv.Agg.Total()), comma(kv.Agg.Messages), cGreen, money(kv.Agg.Cost), cReset))
	}
	return out
}

func blocksView(s *Snapshot, w int) []string {
	out := []string{"",
		fmt.Sprintf("  %s%-26s %11s %8s %9s  %s%s",
			cBold+cWhite, "5H BLOCK", "TOKENS", "MSGS", "COST", "USAGE", cReset),
		" " + cGray + strings.Repeat("-", maxi(10, w-2)) + cReset,
	}
	peak := 0
	for _, b := range s.Blocks {
		if b.Total() > peak {
			peak = b.Total()
		}
	}
	from := len(s.Blocks) - 20
	if from < 0 {
		from = 0
	}
	for i := len(s.Blocks) - 1; i >= from; i-- {
		b := s.Blocks[i]
		share := 0.0
		if peak > 0 {
			share = float64(b.Total()) / float64(peak)
		}
		label := b.Start.Format("01/02 15:04") + " -> " + b.End.Format("15:04")
		live := "  "
		if s.Cur != nil && b.Start.Equal(s.Cur.Start) {
			live = cGreen + "> " + cReset
		}
		out = append(out, fmt.Sprintf(" %s%-26s %11s %8s %s%9s%s  %s",
			live, label, tokens(b.Total()), comma(b.Messages), cGreen, money(b.Cost), cReset,
			bar(share, maxi(8, w-72))))
	}
	return out
}

// ---- formatting helpers ----

func bar(frac float64, width int) string {
	if frac < 0 {
		frac = 0
	}
	if frac > 1 {
		frac = 1
	}
	if width < 1 {
		width = 1
	}
	fill := int(frac * float64(width))
	color := cGreen
	switch {
	case frac > 0.85:
		color = cRed
	case frac > 0.6:
		color = cYell
	}
	return color + strings.Repeat("#", fill) + cGray + strings.Repeat(".", width-fill) + cReset
}

func sparkline(v []int) string {
	max := maxSlice(v)
	var b strings.Builder
	b.WriteString(cCyan)
	for _, n := range v {
		if max == 0 || n == 0 {
			b.WriteString(cGray)
			b.WriteString("_")
			b.WriteString(cCyan)
			continue
		}
		i := int(float64(n) / float64(max) * float64(len(sparkChars)-1))
		b.WriteRune(sparkChars[i])
	}
	b.WriteString(cReset)
	return b.String()
}

func tokens(n int) string {
	switch {
	case n >= 1_000_000_000:
		return fmt.Sprintf("%.2fG", float64(n)/1e9)
	case n >= 1_000_000:
		return fmt.Sprintf("%.2fM", float64(n)/1e6)
	case n >= 1_000:
		return fmt.Sprintf("%.1fK", float64(n)/1e3)
	}
	return fmt.Sprint(n)
}

func money(v float64) string {
	if v >= 1000 {
		return fmt.Sprintf("$%.0f", v)
	}
	if v >= 10 {
		return fmt.Sprintf("$%.2f", v)
	}
	return fmt.Sprintf("$%.3f", v)
}

func comma(n int) string {
	s := fmt.Sprint(n)
	if len(s) <= 3 {
		return s
	}
	var out []byte
	for i, c := range []byte(s) {
		if i > 0 && (len(s)-i)%3 == 0 {
			out = append(out, ',')
		}
		out = append(out, c)
	}
	return string(out)
}

func dur(d time.Duration) string {
	if d < 0 {
		d = 0
	}
	h := int(d.Hours())
	m := int(d.Minutes()) % 60
	if h > 0 {
		return fmt.Sprintf("%dh%02dm", h, m)
	}
	return fmt.Sprintf("%dm", m)
}

func trunc(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	if n <= 1 {
		return string(r[:n])
	}
	return string(r[:n-1]) + "~"
}

func maxi(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func maxSlice(v []int) int {
	m := 0
	for _, n := range v {
		if n > m {
			m = n
		}
	}
	return m
}

// dispWidth counts printable columns, skipping ANSI escapes and counting
// East Asian wide runes as two columns.
func dispWidth(s string) int {
	w, esc := 0, false
	for _, r := range s {
		if esc {
			if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') {
				esc = false
			}
			continue
		}
		if r == 0x1b {
			esc = true
			continue
		}
		if isWide(r) {
			w += 2
		} else {
			w++
		}
	}
	return w
}

func isWide(r rune) bool {
	return (r >= 0x1100 && r <= 0x115F) ||
		(r >= 0x2E80 && r <= 0xA4CF) ||
		(r >= 0xAC00 && r <= 0xD7A3) ||
		(r >= 0xF900 && r <= 0xFAFF) ||
		(r >= 0xFF00 && r <= 0xFF60) ||
		(r >= 0xFFE0 && r <= 0xFFE6)
}
