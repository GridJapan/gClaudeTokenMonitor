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
}

type rawLine struct {
	Type      string `json:"type"`
	Timestamp string `json:"timestamp"`
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
		Usage:   usage,
		Cost:    cost,
		Known:   known,
	}

	key = r.Message.ID + "|" + r.RequestID
	if key == "|" {
		key = r.UUID
	}
	e.Key = key
	return e, key, true
}
