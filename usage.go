package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// The plan-limit percentages are not written to disk anywhere; Claude Code asks
// Anthropic for them each time it renders the bars. The same endpoint is
// reachable with the OAuth token Claude Code already stores locally, so ctm can
// read the official numbers instead of estimating them.
//
// Endpoint and headers follow the public CodexBar client
// (github.com/steipete/CodexBar, docs/claude.md "OAuth API").

const (
	usageEndpoint = "https://api.anthropic.com/api/oauth/usage"
	usageBeta     = "oauth-2025-04-20"
	usageScope    = "user:profile" // tokens without this cannot call usage
)

type oauthCreds struct {
	ClaudeAiOauth struct {
		AccessToken      string   `json:"accessToken"`
		ExpiresAt        int64    `json:"expiresAt"`
		Scopes           []string `json:"scopes"`
		SubscriptionType string   `json:"subscriptionType"`
		RateLimitTier    string   `json:"rateLimitTier"`
	} `json:"claudeAiOauth"`
}

// UsageWindow is one quota window as the API reports it.
type UsageWindow struct {
	Utilization float64 `json:"utilization"`
	ResetsAt    string  `json:"resets_at"`
}

// UsageLimit is one row of the `limits` array, which is what the Claude Code
// UI renders. kind is session / weekly_all / weekly_scoped.
type UsageLimit struct {
	Kind     string  `json:"kind"`
	Group    string  `json:"group"`
	Percent  float64 `json:"percent"`
	Severity string  `json:"severity"`
	ResetsAt string  `json:"resets_at"`
	IsActive bool    `json:"is_active"`
	Scope    *struct {
		Model *struct {
			DisplayName string `json:"display_name"`
		} `json:"model"`
	} `json:"scope"`
}

// Label names the row the way the UI does.
func (l UsageLimit) Label() string {
	if l.Scope != nil && l.Scope.Model != nil && l.Scope.Model.DisplayName != "" {
		return "週次・" + l.Scope.Model.DisplayName
	}
	switch l.Kind {
	case "session":
		return "5時間制限"
	case "weekly_all":
		return "週間・全モデル"
	}
	return l.Kind
}

// Key is a stable identifier for logging and history.
func (l UsageLimit) Key() string {
	if l.Kind == "weekly_scoped" && l.Scope != nil && l.Scope.Model != nil {
		return "weekly-" + strings.ToLower(l.Scope.Model.DisplayName)
	}
	return l.Kind
}

type UsageResponse struct {
	FiveHour *UsageWindow `json:"five_hour"`
	SevenDay *UsageWindow `json:"seven_day"`
	Limits   []UsageLimit `json:"limits"`
	Extra    *struct {
		IsEnabled   bool     `json:"is_enabled"`
		Utilization *float64 `json:"utilization"`
	} `json:"extra_usage"`
}

func credentialsPath() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return filepath.Join(".claude", ".credentials.json")
	}
	return filepath.Join(home, ".claude", ".credentials.json")
}

// loadCreds re-reads the file every call: Claude Code refreshes the token in
// place, so caching it would break after an hour.
func loadCreds() (oauthCreds, error) {
	var c oauthCreds
	b, err := os.ReadFile(credentialsPath())
	if err != nil {
		return c, fmt.Errorf("%s を読めない: %w", credentialsPath(), err)
	}
	if err := json.Unmarshal(b, &c); err != nil {
		return c, err
	}
	if c.ClaudeAiOauth.AccessToken == "" {
		return c, fmt.Errorf("claudeAiOauth が無い。claude にログインし直す必要がある")
	}
	ok := false
	for _, s := range c.ClaudeAiOauth.Scopes {
		if s == usageScope {
			ok = true
		}
	}
	if len(c.ClaudeAiOauth.Scopes) > 0 && !ok {
		return c, fmt.Errorf("トークンに %s スコープが無いので使用量は取得できない", usageScope)
	}
	return c, nil
}

var (
	verOnce   sync.Once
	verCached = "2.1.0"
)

// claudeVersion is only used to build a User-Agent, so it is resolved once and
// with a hard timeout: a hung `claude` must never stall the resident recorder.
func claudeVersion() string {
	verOnce.Do(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		out, err := exec.CommandContext(ctx, "claude", "--version").Output()
		if err != nil {
			return
		}
		if f := strings.Fields(string(out)); len(f) > 0 {
			verCached = f[0]
		}
	})
	return verCached
}

// UsageRateLimited reports that Anthropic throttled the usage endpoint itself.
type UsageRateLimited struct{ RetryAfter time.Duration }

func (e UsageRateLimited) Error() string {
	return fmt.Sprintf("使用量エンドポイントがレート制限中 (%s 後に再試行)", dur(e.RetryAfter))
}

// FetchUsage asks Anthropic for the current plan-limit percentages.
func FetchUsage() (*UsageResponse, error) {
	c, err := loadCreds()
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequest(http.MethodGet, usageEndpoint, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+c.ClaudeAiOauth.AccessToken)
	req.Header.Set("Accept", "application/json")
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("anthropic-beta", usageBeta)
	req.Header.Set("User-Agent", "claude-cli/"+claudeVersion()+" (external, cli)")

	resp, err := (&http.Client{Timeout: 30 * time.Second}).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, rerr := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if rerr != nil && resp.StatusCode == http.StatusOK {
		return nil, fmt.Errorf("レスポンスを読み切れない: %w", rerr)
	}

	switch resp.StatusCode {
	case http.StatusOK:
		var u UsageResponse
		if err := json.Unmarshal(body, &u); err != nil {
			return nil, err
		}
		return &u, nil
	case http.StatusUnauthorized:
		return nil, fmt.Errorf("認証エラー (401)。claude で再ログインが必要")
	case http.StatusTooManyRequests:
		d := 5 * time.Minute
		if ra := resp.Header.Get("Retry-After"); ra != "" {
			if secs, err := time.ParseDuration(ra + "s"); err == nil {
				d = secs
			}
		}
		return nil, UsageRateLimited{RetryAfter: d}
	default:
		return nil, fmt.Errorf("HTTP %d: %s", resp.StatusCode,
			strings.TrimSpace(string(body))[:mini(300, len(strings.TrimSpace(string(body))))])
	}
}

func mini(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// LimitSample pairs the official percentage with what ctm measured inside the
// same window — "何%のときに、いくら分を消費していたか" in one record.
type LimitSample struct {
	TS       string  `json:"ts"`
	Key      string  `json:"key"`
	Label    string  `json:"label"`
	Percent  float64 `json:"percent"`
	Severity string  `json:"severity"`
	ResetsAt string  `json:"resets_at"`
	Active   bool    `json:"is_active"`

	// ctm's own measurement over the same window, from the archive.
	WindowStart string  `json:"window_start"`
	Messages    int     `json:"messages"`
	Tokens      int     `json:"tokens"`
	Cost        float64 `json:"cost_usd"`

	// Capacity implied by this sample, for headroom estimates between polls.
	ImpliedTokens int     `json:"implied_limit_tokens,omitempty"`
	ImpliedCost   float64 `json:"implied_limit_cost_usd,omitempty"`
}

// windowStart infers when the current window opened, from its reset time.
func windowStart(kind, resetsAt string) time.Time {
	t, err := time.Parse(time.RFC3339, resetsAt)
	if err != nil {
		return time.Time{}
	}
	p := 7 * 24 * time.Hour
	if kind == "session" {
		p = 5 * time.Hour
	}
	return t.Add(-p).Local()
}

// BuildSamples turns one API response into per-window records, measuring the
// archive over each window so the percentage and the consumption line up.
func BuildSamples(archive string, u *UsageResponse, now time.Time) []LimitSample {
	var out []LimitSample
	for _, l := range u.Limits {
		s := LimitSample{
			TS: now.Format(time.RFC3339), Key: l.Key(), Label: l.Label(),
			Percent: l.Percent, Severity: l.Severity, ResetsAt: l.ResetsAt, Active: l.IsActive,
		}
		if start := windowStart(l.Kind, l.ResetsAt); !start.IsZero() {
			s.WindowStart = start.Format(time.RFC3339)
			rows, _ := LoadArchive(QueryOpts{Dir: archive, From: start, To: now})
			model := ""
			if l.Scope != nil && l.Scope.Model != nil {
				model = normalizeModelName(l.Scope.Model.DisplayName)
			}
			for _, e := range rows {
				if model != "" && !strings.Contains(normalizeModelName(e.Model), model) {
					continue
				}
				s.Messages++
				s.Tokens += e.Total
				s.Cost += e.Cost
			}
			if l.Percent > 0 {
				s.ImpliedTokens = int(float64(s.Tokens) / (l.Percent / 100))
				s.ImpliedCost = s.Cost / (l.Percent / 100)
			}
		}
		out = append(out, s)
	}
	return out
}

// normalizeModelName makes a display name like "Opus 4.6" comparable with a
// model id like "claude-opus-4-6": lowercase, and drop every separator.
func normalizeModelName(s string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(s) {
		switch r {
		case ' ', '-', '.', '_':
		default:
			b.WriteRune(r)
		}
	}
	return strings.TrimPrefix(b.String(), "claude")
}
