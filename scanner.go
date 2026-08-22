package main

import (
	"bufio"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Scanner incrementally tails every *.jsonl under the projects directory.
type Scanner struct {
	Root    string
	Since   time.Time // zero = no cutoff
	offsets map[string]int64
	Files   int
	Bytes   int64

	// PathTTL > 0 のとき、ディレクトリ走査の結果をこの時間キャッシュする。
	// 200ms 周期の常駐では走査が支配的コストになるため、ファイル発見は
	// 数秒に 1 回で十分（新セッションのログは開始から数秒で見つかればよい）。
	PathTTL     time.Duration
	pathCache   []string
	pathCacheAt time.Time

	// prompts holds, per session log, the most recent human instruction seen
	// while streaming the file. Each assistant entry is stamped with it, so
	// the archive can say which instruction a message was serving.
	prompts map[string]string
}

func NewScanner(root string, since time.Time) *Scanner {
	return &Scanner{Root: root, Since: since,
		offsets: map[string]int64{}, prompts: map[string]string{}}
}

// Scan reads everything appended since the previous call and hands each new
// entry to emit. It returns the number of entries emit accepted.
func (s *Scanner) Scan(emit func(Entry, string) bool) (int, error) {
	paths, err := s.listPaths()
	if err != nil {
		return 0, err
	}
	s.Files = len(paths)

	n := 0
	for _, p := range paths {
		st, err := os.Stat(p)
		if err != nil {
			continue
		}
		off := s.offsets[p]
		if st.Size() < off { // truncated or rotated
			off = 0
		}
		if st.Size() == off {
			continue
		}
		project := projectFromPath(s.Root, p)
		newOff, count, err := s.readFrom(p, off, project, emit)
		if err != nil {
			continue
		}
		s.offsets[p] = newOff
		s.Bytes += newOff - off
		n += count
	}
	return n, nil
}

func (s *Scanner) readFrom(path string, off int64, project string, emit func(Entry, string) bool) (int64, int, error) {
	f, err := os.Open(path)
	if err != nil {
		return off, 0, err
	}
	defer f.Close()
	if _, err := f.Seek(off, io.SeekStart); err != nil {
		return off, 0, err
	}

	r := bufio.NewReaderSize(f, 1<<20)
	consumed := off
	count := 0
	for {
		line, err := r.ReadBytes('\n')
		if err != nil {
			// Incomplete trailing line: leave it for the next pass.
			break
		}
		consumed += int64(len(line))
		if txt, isPrompt := ParsePrompt(line); isPrompt {
			s.prompts[path] = TruncatePrompt(txt, 200)
			continue
		}
		e, key, ok := ParseLine(line, project)
		if !ok {
			continue
		}
		e.Prompt = s.prompts[path]
		if !s.Since.IsZero() && e.TS.Before(s.Since) {
			continue
		}
		if emit(e, key) {
			count++
		}
	}
	return consumed, count, nil
}

// listPaths walks the tree, honoring the PathTTL cache.
func (s *Scanner) listPaths() ([]string, error) {
	if s.PathTTL > 0 && s.pathCache != nil && time.Since(s.pathCacheAt) < s.PathTTL {
		return s.pathCache, nil
	}
	var paths []string
	err := filepath.WalkDir(s.Root, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil // unreadable dir: skip, keep going
		}
		if d.IsDir() || !strings.HasSuffix(d.Name(), ".jsonl") {
			return nil
		}
		paths = append(paths, p)
		return nil
	})
	if err != nil {
		return nil, err
	}
	s.pathCache, s.pathCacheAt = paths, time.Now()
	return paths, nil
}

// projectFromPath falls back to the encoded directory name Claude Code uses,
// e.g. "C--claude-dev" -> "claude-dev".
func projectFromPath(root, path string) string {
	rel, err := filepath.Rel(root, path)
	if err != nil {
		return "unknown"
	}
	dir := filepath.Dir(rel)
	if dir == "." {
		return "unknown"
	}
	name := filepath.Base(dir)
	if i := strings.Index(name, "--"); i >= 0 && i <= 2 {
		name = name[i+2:]
	}
	if name == "" {
		return "unknown"
	}
	return name
}

func defaultProjectsDir() string {
	if v := os.Getenv("CLAUDE_CONFIG_DIR"); v != "" {
		return filepath.Join(v, "projects")
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return filepath.Join(".claude", "projects")
	}
	return filepath.Join(home, ".claude", "projects")
}
