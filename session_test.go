package main

import (
	"os"
	"path/filepath"
	"testing"
)

func writeLog(t *testing.T, dir, id string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(dir, id+".jsonl"), []byte("{}\n"), 0o644); err != nil {
		t.Fatal(err)
	}
}

// -session new must ignore sessions that already existed and latch onto the
// first log file that shows up afterwards.
func TestFilterNewLatchesOnLaterSession(t *testing.T) {
	root := t.TempDir()
	sub := filepath.Join(root, "C--demo")
	if err := os.Mkdir(sub, 0o755); err != nil {
		t.Fatal(err)
	}
	writeLog(t, sub, "old-session")

	f, err := newFilter(root, "new")
	if err != nil {
		t.Fatal(err)
	}
	st := NewStore()
	add := f.wrap(st)

	f.latch()
	if f.target != "" {
		t.Fatalf("latched onto a pre-existing session: %q", f.target)
	}
	if add(Entry{Session: "old-session"}, "k1") {
		t.Fatal("accepted an entry while unlatched")
	}

	writeLog(t, sub, "fresh-session")
	f.latch()
	if f.target != "fresh-session" {
		t.Fatalf("target = %q, want fresh-session", f.target)
	}
	if add(Entry{Session: "old-session"}, "k2") {
		t.Fatal("accepted an entry from the wrong session")
	}
	if !add(Entry{Session: "fresh-session"}, "k3") {
		t.Fatal("rejected an entry from the latched session")
	}
	if got := len(st.entries); got != 1 {
		t.Fatalf("store has %d entries, want 1", got)
	}
}

func TestFilterPrefixAndPassthrough(t *testing.T) {
	root := t.TempDir()
	f, err := newFilter(root, "abc123")
	if err != nil {
		t.Fatal(err)
	}
	add := f.wrap(NewStore())
	if !add(Entry{Session: "abc123-4567"}, "k1") {
		t.Fatal("prefix match rejected")
	}
	if add(Entry{Session: "zzz"}, "k2") {
		t.Fatal("non-matching session accepted")
	}

	f2, err := newFilter(root, "")
	if err != nil {
		t.Fatal(err)
	}
	if !f2.wrap(NewStore())(Entry{Session: "anything"}, "k3") {
		t.Fatal("unfiltered mode dropped an entry")
	}
}

func TestParsePrompt(t *testing.T) {
	cases := []struct {
		line string
		want string
		ok   bool
	}{
		{`{"type":"user","message":{"role":"user","content":"計測して"}}`, "計測して", true},
		{`{"type":"user","message":{"role":"user","content":[{"type":"text","text":"表を作れ"}]}}`, "表を作れ", true},
		{`{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"x"}]}}`, "", false},
		{`{"type":"user","isMeta":true,"message":{"role":"user","content":"meta"}}`, "", false},
		{`{"type":"user","message":{"role":"user","content":"<command-name>/foo</command-name>"}}`, "", false},
		{`{"type":"assistant","message":{"role":"assistant","content":"x"}}`, "", false},
	}
	for i, c := range cases {
		got, ok := ParsePrompt([]byte(c.line))
		if ok != c.ok || got != c.want {
			t.Errorf("case %d: got (%q,%v) want (%q,%v)", i, got, ok, c.want, c.ok)
		}
	}
	long := make([]rune, 0, 300)
	for i := 0; i < 300; i++ {
		long = append(long, 'あ')
	}
	if got := TruncatePrompt(string(long), 200); len([]rune(got)) != 200 {
		t.Errorf("truncate: got %d runes", len([]rune(got)))
	}
}
