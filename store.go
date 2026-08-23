package main

import (
	"math"
	"sort"
	"time"
)

const blockLen = 5 * time.Hour // Claude Code rate-limit window

// Agg is a running aggregate over a set of entries.
type Agg struct {
	Input        int       `json:"input_tokens"`
	CacheWrite5m int       `json:"cache_write_5m_tokens"`
	CacheWrite1h int       `json:"cache_write_1h_tokens"`
	CacheRead    int       `json:"cache_read_tokens"`
	Output       int       `json:"output_tokens"`
	Cost         float64   `json:"cost_usd"`
	Messages     int       `json:"messages"`
	First        time.Time `json:"first"`
	Last         time.Time `json:"last"`
}

func (a *Agg) add(e Entry) {
	a.Input += e.Input
	a.CacheWrite5m += e.CacheWrite5m
	a.CacheWrite1h += e.CacheWrite1h
	a.CacheRead += e.CacheRead
	a.Output += e.Output
	a.Cost += e.Cost
	a.Messages++
	if a.First.IsZero() || e.TS.Before(a.First) {
		a.First = e.TS
	}
	if e.TS.After(a.Last) {
		a.Last = e.TS
	}
}

func (a Agg) CacheWrite() int { return a.CacheWrite5m + a.CacheWrite1h }
func (a Agg) Total() int {
	return a.Input + a.CacheWrite() + a.CacheRead + a.Output
}

// CacheReadPct is the share of cache reads in the total (0-100, one decimal).
// Cache reads are 0.1x the base rate, so a total dominated by them looks far
// bigger than what was actually paid for — surface the share next to totals.
func (a Agg) CacheReadPct() float64 {
	t := a.Total()
	if t == 0 {
		return 0
	}
	return math.Round(float64(a.CacheRead)/float64(t)*1000) / 10
}

// Store holds every deduplicated entry seen so far.
type Store struct {
	entries []Entry
	seen    map[string]struct{}
	sorted  bool
	Unknown map[string]int // models with no known price
}

func NewStore() *Store {
	return &Store{seen: map[string]struct{}{}, Unknown: map[string]int{}}
}

// Add records an entry unless its dedup key was already seen.
func (s *Store) Add(e Entry, key string) bool {
	if key != "" {
		if _, dup := s.seen[key]; dup {
			return false
		}
		s.seen[key] = struct{}{}
	}
	s.entries = append(s.entries, e)
	s.sorted = false
	if !e.Known {
		s.Unknown[e.Model]++
	}
	return true
}

func (s *Store) sortEntries() {
	if s.sorted {
		return
	}
	sort.Slice(s.entries, func(i, j int) bool { return s.entries[i].TS.Before(s.entries[j].TS) })
	s.sorted = true
}

// Block is one 5-hour usage window.
type Block struct {
	Start time.Time `json:"start"`
	End   time.Time `json:"end"`
	Agg   `json:"agg"`
}

// KV pairs a group key with its aggregate. Note carries an optional label for
// the group (for sessions: the project the session ran in).
type KV struct {
	Key  string `json:"key"`
	Note string `json:"note,omitempty"`
	Agg  Agg    `json:"agg"`
}

// Snapshot is everything the UI needs for one frame.
type Snapshot struct {
	Now            time.Time
	Total          Agg
	Models         []KV
	Projects       []KV
	Days           []KV
	Sessions       []KV
	Blocks         []Block
	Cur            *Block
	BurnTokens     float64 // tokens / minute over the burn window
	BurnCost       float64 // USD / minute
	Spark          []int   // per-minute totals, oldest first
	Recent         []Entry
	ActiveSessions int
	Files          int
	Unknown        map[string]int
}

const (
	burnWindow  = 5 * time.Minute
	sparkWindow = 60 // minutes
	activeIdle  = 30 * time.Minute
)

// Snapshot computes all views over the current entry set.
func (s *Store) Snapshot(now time.Time, files int) *Snapshot {
	s.sortEntries()

	snap := &Snapshot{Now: now, Files: files, Unknown: s.Unknown}

	models := map[string]*Agg{}
	projects := map[string]*Agg{}
	days := map[string]*Agg{}
	sessions := map[string]*Agg{}
	sessProj := map[string]string{}

	spark := make([]int, sparkWindow)
	sparkStart := now.Add(-time.Duration(sparkWindow) * time.Minute)
	burnStart := now.Add(-burnWindow)
	var burnTok int
	var burnCost float64

	for _, e := range s.entries {
		snap.Total.add(e)
		bucket(models, e.Model).add(e)
		bucket(projects, e.Project).add(e)
		bucket(days, e.TS.Format("2006-01-02")).add(e)
		bucket(sessions, e.Session).add(e)
		sessProj[groupKey(e.Session)] = e.Project

		if e.TS.After(burnStart) {
			burnTok += e.Total()
			burnCost += e.Cost
		}
		if e.TS.After(sparkStart) {
			i := int(e.TS.Sub(sparkStart) / time.Minute)
			if i >= 0 && i < sparkWindow {
				spark[i] += e.Total()
			}
		}
	}

	snap.Models = sortedByCost(models)
	snap.Projects = sortedByCost(projects)
	snap.Sessions = sortedByRecent(sessions)
	for i := range snap.Sessions {
		snap.Sessions[i].Note = sessProj[snap.Sessions[i].Key]
	}
	snap.Days = sortedByKeyDesc(days)
	snap.Spark = spark
	snap.BurnTokens = float64(burnTok) / burnWindow.Minutes()
	snap.BurnCost = burnCost / burnWindow.Minutes()

	for _, kv := range snap.Sessions {
		if now.Sub(kv.Agg.Last) <= activeIdle {
			snap.ActiveSessions++
		}
	}

	snap.Blocks = s.blocks()
	if n := len(snap.Blocks); n > 0 {
		last := snap.Blocks[n-1]
		if now.Before(last.End) {
			snap.Cur = &snap.Blocks[n-1]
		}
	}

	if n := len(s.entries); n > 0 {
		from := n - 8
		if from < 0 {
			from = 0
		}
		rec := make([]Entry, 0, n-from)
		for i := n - 1; i >= from; i-- {
			rec = append(rec, s.entries[i])
		}
		snap.Recent = rec
	}
	return snap
}

// blocks groups entries into 5-hour windows anchored to the hour of the first
// message, restarting after a gap of one full window.
func (s *Store) blocks() []Block {
	var out []Block
	var lastTS time.Time
	for _, e := range s.entries {
		newBlock := len(out) == 0 ||
			!e.TS.Before(out[len(out)-1].End) ||
			e.TS.Sub(lastTS) >= blockLen
		if newBlock {
			start := e.TS.Truncate(time.Hour)
			out = append(out, Block{Start: start, End: start.Add(blockLen)})
		}
		out[len(out)-1].Agg.add(e)
		lastTS = e.TS
	}
	return out
}

func groupKey(k string) string {
	if k == "" {
		return "(unknown)"
	}
	return k
}

func bucket(m map[string]*Agg, k string) *Agg {
	k = groupKey(k)
	a, ok := m[k]
	if !ok {
		a = &Agg{}
		m[k] = a
	}
	return a
}

func toKV(m map[string]*Agg) []KV {
	out := make([]KV, 0, len(m))
	for k, v := range m {
		out = append(out, KV{Key: k, Agg: *v})
	}
	return out
}

func sortedByCost(m map[string]*Agg) []KV {
	out := toKV(m)
	sort.Slice(out, func(i, j int) bool {
		if out[i].Agg.Cost != out[j].Agg.Cost {
			return out[i].Agg.Cost > out[j].Agg.Cost
		}
		return out[i].Agg.Total() > out[j].Agg.Total()
	})
	return out
}

func sortedByRecent(m map[string]*Agg) []KV {
	out := toKV(m)
	sort.Slice(out, func(i, j int) bool { return out[i].Agg.Last.After(out[j].Agg.Last) })
	return out
}

func sortedByKeyDesc(m map[string]*Agg) []KV {
	out := toKV(m)
	sort.Slice(out, func(i, j int) bool { return out[i].Key > out[j].Key })
	return out
}
