package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Plan-limit tracking. The percentages come from Anthropic (see usage.go); the
// consumption figures come from ctm's own archive. Logging them together is the
// point: a percentage alone does not say what it cost to get there.

func limitsDir(dir string) string { return filepath.Join(dir, "limits") }

func limitsLog(dir string, t time.Time) string {
	return filepath.Join(limitsDir(dir), t.Format("2006-01-02")+".ndjson")
}

func limitsMD(dir string, t time.Time) string {
	return filepath.Join(limitsDir(dir), t.Format("2006-01-02")+".md")
}

// AppendSamples writes one poll's worth of records, machine-readable and
// human-readable side by side.
func AppendSamples(dir string, samples []LimitSample, now time.Time) error {
	if len(samples) == 0 {
		return nil
	}
	if err := os.MkdirAll(limitsDir(dir), 0o755); err != nil {
		return err
	}

	f, err := os.OpenFile(limitsLog(dir, now), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	w := bufio.NewWriter(f)
	for _, s := range samples {
		b, err := json.Marshal(s)
		if err != nil {
			continue
		}
		w.Write(asciiEscape(b))
		w.WriteByte('\n')
	}
	w.Flush()
	f.Sync()
	f.Close()

	mdPath := limitsMD(dir, now)
	fresh := false
	if _, err := os.Stat(mdPath); os.IsNotExist(err) {
		fresh = true
	}
	m, err := os.OpenFile(mdPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	defer m.Close()
	mw := bufio.NewWriter(m)
	if fresh {
		fmt.Fprintf(mw, "# %s のプラン使用制限\n\n"+
			"公式の使用率（Anthropic の API から取得）と、同じ窓で ctm が実測した消費を並べたもの。\n\n"+
			"| 時刻 | 窓 | 使用率 | リセットまで | 実測メッセージ | 実測トークン | 実測コスト | 逆算した窓の容量 |\n"+
			"|---|---|---:|---|---:|---:|---:|---:|\n", now.Format("2006-01-02"))
	}
	for _, s := range samples {
		left := "-"
		if t, err := time.Parse(time.RFC3339, s.ResetsAt); err == nil {
			left = dur(time.Until(t))
		}
		capacity := "-"
		if s.ImpliedCost > 0 {
			capacity = money(s.ImpliedCost)
		}
		fmt.Fprintf(mw, "| %s | %s | %.1f%% | %s | %s | %s | %s | %s |\n",
			now.Format("15:04:05"), s.Label, s.Percent, left,
			comma(s.Messages), comma(s.Tokens), money(s.Cost), capacity)
	}
	mw.Flush()
	m.Sync()
	return nil
}

// PollLimits fetches once and archives the result.
func PollLimits(dir string) ([]LimitSample, error) {
	u, err := FetchUsage()
	if err != nil {
		return nil, err
	}
	now := time.Now()
	s := BuildSamples(dir, u, now)
	if err := AppendSamples(dir, s, now); err != nil {
		return s, err
	}
	return s, nil
}

// ShowLimits prints the live picture.
func ShowLimits(dir string) error {
	samples, err := PollLimits(dir)
	if err != nil {
		return err
	}
	c, _ := loadCreds()
	fmt.Printf("%sプラン使用制限%s  %s / %s\n", cBold+cWhite, cReset,
		c.ClaudeAiOauth.SubscriptionType, c.ClaudeAiOauth.RateLimitTier)

	for _, s := range samples {
		mark := " "
		if s.Active {
			mark = cGreen + "*" + cReset
		}
		fmt.Printf("\n%s %s%s%s\n", mark, cBold, s.Label, cReset)
		left := "-"
		if t, err := time.Parse(time.RFC3339, s.ResetsAt); err == nil {
			left = dur(time.Until(t)) + "後 (" + t.Local().Format("01-02 15:04") + ")"
		}
		fmt.Printf("   使用率    %s%.1f%%%s %s\n",
			pctColor(s.Percent), s.Percent, cReset, pctBar(s.Percent, 28))
		fmt.Printf("   リセット  %s\n", left)
		fmt.Printf("   実測消費  %s メッセージ / %s トークン / %s\n",
			comma(s.Messages), tokens(s.Tokens), money(s.Cost))
		if s.ImpliedCost > 0 {
			fmt.Printf("   窓の容量  %s トークン / %s 相当（この使用率からの逆算）\n",
				tokens(s.ImpliedTokens), money(s.ImpliedCost))
			fmt.Printf("   残り      %s 相当\n", money(s.ImpliedCost-s.Cost))
		}
	}
	fmt.Printf("\n%s記録先: %s%s\n", cGray, limitsLog(dir, time.Now()), cReset)
	return nil
}

// LimitHistory prints the archived samples for a day.
func LimitHistory(dir, day, key string) error {
	t := time.Now()
	if day != "" {
		p, err := parseWhen(day, time.Now())
		if err != nil {
			return err
		}
		t = p
	}
	path := limitsLog(dir, t)
	f, err := os.Open(path)
	if err != nil {
		return fmt.Errorf("%s に記録が無い", path)
	}
	defer f.Close()

	fmt.Printf("%-9s %-16s %7s %11s %14s %10s %14s\n",
		"時刻", "窓", "使用率", "メッセージ", "トークン", "コスト", "逆算容量")
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 1<<20)
	n := 0
	for sc.Scan() {
		var s LimitSample
		if json.Unmarshal(sc.Bytes(), &s) != nil {
			continue
		}
		if key != "" && !strings.HasPrefix(s.Key, key) {
			continue
		}
		ts, _ := time.Parse(time.RFC3339, s.TS)
		capacity := "-"
		if s.ImpliedCost > 0 {
			capacity = money(s.ImpliedCost)
		}
		fmt.Printf("%-9s %-16s %6.1f%% %11s %14s %10s %14s\n",
			ts.Format("15:04:05"), trunc(s.Label, 16), s.Percent,
			comma(s.Messages), comma(s.Tokens), money(s.Cost), capacity)
		n++
	}
	if n == 0 {
		fmt.Println("該当なし")
	}
	return nil
}

func pctColor(p float64) string {
	switch {
	case p >= 90:
		return cRed
	case p >= 70:
		return cYell
	default:
		return cGreen
	}
}

func pctBar(p float64, w int) string {
	if p < 0 {
		p = 0
	}
	if p > 100 {
		p = 100
	}
	n := int(p / 100 * float64(w))
	return strings.Repeat("#", n) + strings.Repeat(".", w-n)
}
