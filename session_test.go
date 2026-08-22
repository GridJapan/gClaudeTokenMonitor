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
