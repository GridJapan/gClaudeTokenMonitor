package main

import (
	"encoding/json"
	"os"
	"strconv"
	"strings"
)

// Price is the list price per million tokens (MTok).
type Price struct {
	Input  float64 `json:"input"`
	Output float64 `json:"output"`
}

// Cache multipliers applied to the input rate.
const (
	cacheReadMult    = 0.10 // cache_read_input_tokens
	cacheWrite5mMult = 1.25 // ephemeral 5-minute TTL
	cacheWrite1hMult = 2.00 // ephemeral 1-hour TTL
)

// Default list prices, USD per million tokens.
// Override any entry with -pricing <file.json> (see README).
var prices = map[string]Price{
	// Frontier
	"claude-fable-5":  {10, 50},
	"claude-mythos-5": {10, 50},

	// Opus
	"claude-opus-5":        {5, 25},
	"claude-opus-5-fast":   {10, 50}, // fast mode research preview
	"claude-opus-4-8":      {5, 25},
	"claude-opus-4-8-fast": {10, 50},
	"claude-opus-4-7":      {5, 25},
	"claude-opus-4-6":      {5, 25},
	"claude-opus-4-5":      {5, 25},
	"claude-opus-4-1":      {15, 75},
	"claude-opus-4-0":      {15, 75},
	"claude-opus-4":        {15, 75},
	"claude-3-opus":        {15, 75},

	// Sonnet
	"claude-sonnet-5":   {3, 15},
	"claude-sonnet-4-6": {3, 15},
	"claude-sonnet-4-5": {3, 15},
	"claude-sonnet-4-0": {3, 15},
	"claude-sonnet-4":   {3, 15},
	"claude-3-7-sonnet": {3, 15},
	"claude-3-5-sonnet": {3, 15},

	// Haiku
	"claude-haiku-4-5": {1, 5},
	"claude-3-5-haiku": {0.80, 4},
	"claude-3-haiku":   {0.25, 1.25},
}

// LoadPricing merges a JSON file of {"model-id": {"input": n, "output": n}}
// into the default table.
func LoadPricing(path string) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	var over map[string]Price
	if err := json.Unmarshal(b, &over); err != nil {
		return err
	}
	for k, v := range over {
		prices[k] = v
	}
	return nil
}

func normalizeModel(m string) string {
	m = strings.ToLower(strings.TrimSpace(m))
	for _, p := range []string{"us.anthropic.", "eu.anthropic.", "apac.anthropic.", "anthropic."} {
		m = strings.TrimPrefix(m, p)
	}
	m = strings.TrimSuffix(m, "[1m]")
	if i := strings.Index(m, "@"); i > 0 { // Vertex snapshot form
		m = m[:i]
	}
	return m
}

// PriceFor resolves a model id (plus the reported speed) to a list price.
// The bool reports whether a price was found.
func PriceFor(model, speed string) (Price, bool) {
	n := normalizeModel(model)
	if speed == "fast" {
		if p, ok := resolve(n + "-fast"); ok {
			return p, true
		}
		if p, ok := resolve(n); ok {
			return Price{p.Input * 2, p.Output * 2}, true
		}
		return Price{}, false
	}
	return resolve(n)
}

func resolve(n string) (Price, bool) {
	if p, ok := prices[n]; ok {
		return p, true
	}
	// Strip a trailing date snapshot: claude-haiku-4-5-20251001
	if i := strings.LastIndex(n, "-"); i > 0 {
		if _, err := strconv.Atoi(n[i+1:]); err == nil {
			if p, ok := prices[n[:i]]; ok {
				return p, true
			}
			n = n[:i]
		}
	}
	best := ""
	for k := range prices {
		if strings.HasPrefix(n, k) && len(k) > len(best) {
			best = k
		}
	}
	if best != "" {
		return prices[best], true
	}
	return Price{}, false
}

// Cost prices one usage record. Returns the cost in USD and whether the
// model's price is known.
func Cost(model, speed string, u Usage) (float64, bool) {
	p, ok := PriceFor(model, speed)
	if !ok {
		return 0, false
	}
	const M = 1_000_000.0
	c := float64(u.Input)/M*p.Input +
		float64(u.Output)/M*p.Output +
		float64(u.CacheRead)/M*p.Input*cacheReadMult +
		float64(u.CacheWrite5m)/M*p.Input*cacheWrite5mMult +
		float64(u.CacheWrite1h)/M*p.Input*cacheWrite1hMult
	return c, true
}
