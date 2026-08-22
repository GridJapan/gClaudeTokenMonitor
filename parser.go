package main

import (
	"bytes"
	"encoding/json"
	"path/filepath"
	"strings"
	"time"
)

// Usage is the token accounting of a single assistant message.
type Usage struct {
	Input        int
	CacheWrite5m int
	CacheWrite1h int
	CacheRead    int
	Output       int
}

func (u Usage) CacheWrite() int { return u.CacheWrite5m + u.CacheWrite1h }
func (u Usage) Total() int      { return u.Input + u.CacheWrite() + u.CacheRead + u.Output }

// Entry is one priced assistant message.
type Entry struct {
	TS      time.Time
	Model   string
	Project string
	Session string
	Key     string // dedup key: message.id + requestId
	Prompt  string // 直前の人間の指示の先頭 200 文字（スキャナが紐付ける）
	Effort  string // 推論エフォート（行トップレベルの "effort"）
	Speed   string // "standard" / fast モード
	Think   int    // output_tokens_details.thinking_tokens（output の内数）
	Usage
	Cost  float64
	Known bool // model price was found
}

type rawUsage struct {
	Input         int    `json:"input_tokens"`
	CacheCreation int    `json:"cache_creation_input_tokens"`
	CacheRead     int    `json:"cache_read_input_tokens"`
	Output        int    `json:"output_tokens"`
	Speed         string `json:"speed"`
	Breakdown     *struct {
		E5m int `json:"ephemeral_5m_input_tokens"`
		E1h int `json:"ephemeral_1h_input_tokens"`
	} `json:"cache_creation"`
	Details *struct {
		Thinking int `json:"thinking_tokens"`
	} `json:"output_tokens_details"`
}

type rawLine struct {
	Type      string `json:"type"`
	Timestamp string `json:"timestamp"`
	Effort    string `json:"effort"`
	SessionID string `json:"sessionId"`
	CWD       string `json:"cwd"`
	RequestID string `json:"requestId"`
	UUID      string `json:"uuid"`
	Message   struct {
		ID    string    `json:"id"`
		Model string    `json:"model"`
		Usage *rawUsage `json:"usage"`
	} `json:"message"`
}

var usageMarker = []byte(`"usage"`)

var (
	userMarker    = []byte(`"type":"user"`)
	toolUseMarker = []byte(`"tool_use_id"`)
)

type rawUserLine struct {
	Type    string `json:"type"`
	IsMeta  bool   `json:"isMeta"`
	Message struct {
		Role    string          `json:"role"`
		Content json.RawMessage `json:"content"`
	} `json:"message"`
}

// ParsePrompt extracts the human instruction text from a user line. Tool
// results, meta rows, and harness wrappers are not instructions and return
// ok=false; only text a person actually typed comes back.
func ParsePrompt(line []byte) (string, bool) {
	if !bytes.Contains(line, userMarker) || bytes.Contains(line, toolUseMarker) {
		return "", false
	}
	var r rawUserLine
	if json.Unmarshal(line, &r) != nil || r.Type != "user" || r.IsMeta {
		return "", false
	}
	c := r.Message.Content
	text := ""
	if len(c) > 0 && c[0] == '"' {
		json.Unmarshal(c, &text)
	} else if len(c) > 0 && c[0] == '[' {
		var blocks []struct {
			Type string `json:"type"`
			Text string `json:"text"`
		}
		if json.Unmarshal(c, &blocks) == nil {
			for _, b := range blocks {
				if b.Type == "text" && strings.TrimSpace(b.Text) != "" {
					text = b.Text
					break
				}
			}
		}
	}
	text = strings.TrimSpace(text)
	if text == "" ||
		strings.HasPrefix(text, "<") || // <command-name> や <system-reminder> の包み
		strings.HasPrefix(text, "[Request interrupted") ||
		strings.HasPrefix(text, "Caveat:") {
		return "", false
	}
	return text, true
}

// TruncatePrompt keeps the first n characters (runes, not bytes) on one line.
func TruncatePrompt(s string, n int) string {
	s = strings.NewReplacer("\r", " ", "\n", " ", "\t", " ").Replace(s)
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n])
}

// ParseLine turns a JSONL line into an Entry. The second return value is the
// dedup key; ok is false when the line carries no billable usage.
func ParseLine(line []byte, fallbackProject string) (e Entry, key string, ok bool) {
	if !bytes.Contains(line, usageMarker) {
		return e, "", false
	}
	var r rawLine
	if err := json.Unmarshal(line, &r); err != nil {
		return e, "", false
	}
	if r.Type != "assistant" || r.Message.Usage == nil {
		return e, "", false
	}
	if r.Message.Model == "" || strings.HasPrefix(r.Message.Model, "<") {
		return e, "", false // e.g. "<synthetic>"
	}

	u := r.Message.Usage
	usage := Usage{Input: u.Input, CacheRead: u.CacheRead, Output: u.Output}
	if u.Breakdown != nil && (u.Breakdown.E5m+u.Breakdown.E1h) > 0 {
		usage.CacheWrite5m = u.Breakdown.E5m
		usage.CacheWrite1h = u.Breakdown.E1h
	} else {
		usage.CacheWrite5m = u.CacheCreation
	}
	if usage.Total() == 0 {
		return e, "", false
	}

	ts, err := time.Parse(time.RFC3339, r.Timestamp)
	if err != nil {
		ts = time.Now()
	}

	project := fallbackProject
	if r.CWD != "" {
		project = filepath.Base(strings.ReplaceAll(r.CWD, `\`, `/`))
	}

	cost, known := Cost(r.Message.Model, u.Speed, usage)

	e = Entry{
		TS:      ts.Local(),
		Model:   normalizeModel(r.Message.Model),
		Project: project,
		Session: r.SessionID,
		Effort:  r.Effort,
		Speed:   u.Speed,
		Usage:   usage,
		Cost:    cost,
		Known:   known,
	}
	if u.Details != nil {
		e.Think = u.Details.Thinking
	}

	key = r.Message.ID + "|" + r.RequestID
	if key == "|" {
		key = r.UUID
	}
	e.Key = key
	return e, key, true
}
