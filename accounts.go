package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// 使用率アカウントの切り替え。
//
// Claude Code のログインは常に 1 つ（~/.claude/.credentials.json）。個人 MAX と
// 会社 Teams のように複数アカウントを使い分けるユーザーのため、ctm はログインを
// 見かけるたびにトークン一式を <root>/accounts/<orgUuid>.json へ控えておき、
// 使用率（%）の取得元を CLI / 右クリックメニューで選べるようにする。
// 控えたトークンは失効しても refreshToken で自前更新する（usage.go と同じ経路）
// ので、Claude Code 側のログインを切り替え直す必要はない。
//
// 選択は <root>/account.json の {"selected":"auto"|"<orgUuid>"}。
// 無ければ auto（従来どおり現在のログインに追従）。

type acctFile struct {
	Key           string          `json:"key"`   // organizationUuid
	Label         string          `json:"label"` // メニュー・ヘッダの表示名
	Email         string          `json:"email"`
	Org           string          `json:"org"`
	Sub           string          `json:"subscription"` // "MAX 20x" / "Teams Standard" など
	SavedAt       string          `json:"saved_at"`
	ClaudeAiOauth json.RawMessage `json:"claudeAiOauth"` // credentials.json と同じ形
}

func accountsDir(root string) string      { return filepath.Join(root, "accounts") }
func accountPath(root, key string) string { return filepath.Join(accountsDir(root), key+".json") }
func selectionPath(root string) string    { return filepath.Join(root, "account.json") }

// readSelection returns "auto" or an account key.
func readSelection(root string) string {
	b, err := os.ReadFile(selectionPath(root))
	if err != nil {
		return "auto"
	}
	var v struct {
		Selected string `json:"selected"`
	}
	if json.Unmarshal(b, &v) != nil || v.Selected == "" {
		return "auto"
	}
	return v.Selected
}

func writeSelection(root, key string) error {
	if err := os.MkdirAll(root, 0o755); err != nil {
		return err
	}
	b, _ := json.Marshal(struct {
		Selected string `json:"selected"`
	}{key})
	return os.WriteFile(selectionPath(root), b, 0o644)
}

// claudeConfigPath finds ~/.claude.json (who is logged in), honoring
// CLAUDE_CONFIG_DIR the same way credentialsPath does.
func claudeConfigPath() string {
	if v := os.Getenv("CLAUDE_CONFIG_DIR"); v != "" {
		return filepath.Join(v, ".claude.json")
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return ".claude.json"
	}
	return filepath.Join(home, ".claude.json")
}

// subDisplay names the plan the way people say it ("MAX 20x", "Teams Standard").
func subDisplay(subType, rateTier, orgType, seatTier string) string {
	lt := strings.ToLower(rateTier)
	switch {
	case strings.Contains(lt, "20x"):
		return "MAX 20x"
	case strings.Contains(lt, "5x"):
		return "MAX 5x"
	}
	if strings.Contains(strings.ToLower(orgType+subType), "team") {
		if seatTier != "" {
			return "Teams " + capFirst(seatTier)
		}
		return "Teams"
	}
	if subType != "" {
		return capFirst(subType)
	}
	return "?"
}

func capFirst(s string) string {
	if s == "" {
		return s
	}
	return strings.ToUpper(s[:1]) + s[1:]
}

// orgShort drops the boilerplate suffix of personal orgs.
func orgShort(org string) string {
	return strings.TrimSuffix(org, "'s Organization")
}

// currentIdentity reads who is logged in right now.
func currentIdentity() (key, label, email, org, sub string, err error) {
	b, err := os.ReadFile(claudeConfigPath())
	if err != nil {
		return
	}
	var cfg struct {
		OauthAccount struct {
			OrganizationUuid string `json:"organizationUuid"`
			EmailAddress     string `json:"emailAddress"`
			OrganizationName string `json:"organizationName"`
			OrganizationType string `json:"organizationType"`
			SeatTier         string `json:"seatTier"`
		} `json:"oauthAccount"`
	}
	if err = json.Unmarshal(b, &cfg); err != nil {
		return
	}
	a := cfg.OauthAccount
	if a.OrganizationUuid == "" {
		err = fmt.Errorf("%s に oauthAccount が無い", claudeConfigPath())
		return
	}
	var subType, rateTier string
	if c, cerr := loadCreds(); cerr == nil {
		subType = c.ClaudeAiOauth.SubscriptionType
		rateTier = c.ClaudeAiOauth.RateLimitTier
	}
	key = a.OrganizationUuid
	email = a.EmailAddress
	org = a.OrganizationName
	sub = subDisplay(subType, rateTier, a.OrganizationType, a.SeatTier)
	label = orgShort(org) + " · " + sub
	return
}

// snapshotAccount copies the current login's tokens into the vault so it stays
// selectable after the user switches Claude Code to another account. No-op
// (and no error) when nothing changed; identity failures are the caller's to
// ignore — auto mode works without a vault.
func snapshotAccount(root string) error {
	rawCred, err := os.ReadFile(credentialsPath())
	if err != nil {
		return err
	}
	var cred map[string]json.RawMessage
	if err := json.Unmarshal(rawCred, &cred); err != nil {
		return err
	}
	oauth, ok := cred["claudeAiOauth"]
	if !ok {
		return fmt.Errorf("claudeAiOauth が無い")
	}
	key, label, email, org, sub, err := currentIdentity()
	if err != nil {
		return err // 身元が分からないトークンは控えない
	}
	if old, lerr := loadAccount(root, key); lerr == nil {
		var a, b struct {
			AccessToken string `json:"accessToken"`
		}
		json.Unmarshal(old.ClaudeAiOauth, &a)
		json.Unmarshal(oauth, &b)
		if a.AccessToken == b.AccessToken {
			return nil // 変化なし。書かない
		}
	}
	return saveAccount(root, acctFile{
		Key: key, Label: label, Email: email, Org: org, Sub: sub,
		SavedAt: time.Now().Format(time.RFC3339), ClaudeAiOauth: oauth,
	})
}

func loadAccount(root, key string) (acctFile, error) {
	var m acctFile
	b, err := os.ReadFile(accountPath(root, key))
	if err != nil {
		return m, err
	}
	err = json.Unmarshal(b, &m)
	return m, err
}

// saveAccount writes atomically with owner-only permission — the vault holds
// refresh tokens, same sensitivity as credentials.json itself.
func saveAccount(root string, m acctFile) error {
	if err := os.MkdirAll(accountsDir(root), 0o755); err != nil {
		return err
	}
	// compact で書く（CtmMonitor 側の最小 JSON 読みが "key":"value" 形式を前提とする）
	b, err := json.Marshal(m)
	if err != nil {
		return err
	}
	p := accountPath(root, m.Key)
	tmp := p + ".tmp"
	if err := os.WriteFile(tmp, asciiEscape(b), 0o600); err != nil {
		return err
	}
	if err := os.Rename(tmp, p); err != nil {
		os.Remove(tmp)
		return err
	}
	return nil
}

func listAccounts(root string) []acctFile {
	var out []acctFile
	ents, err := os.ReadDir(accountsDir(root))
	if err != nil {
		return out
	}
	for _, e := range ents {
		n := e.Name()
		if e.IsDir() || !strings.HasSuffix(n, ".json") {
			continue
		}
		if m, err := loadAccount(root, strings.TrimSuffix(n, ".json")); err == nil && m.Key != "" {
			out = append(out, m)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Label < out[j].Label })
	return out
}

func shortKey(k string) string {
	if len(k) > 8 {
		return k[:8]
	}
	return k
}

// ShowAccounts lists the vault for `ctm limits accounts`.
func ShowAccounts(root string) error {
	_ = snapshotAccount(root) // 今のログインを最新化してから一覧
	sel := readSelection(root)
	accts := listAccounts(root)
	fmt.Printf("%s使用率の取得アカウント%s（切替: ctm limits use <key|auto>）\n\n",
		cBold+cWhite, cReset)
	mark := func(on bool) string {
		if on {
			return cGreen + "*" + cReset
		}
		return " "
	}
	fmt.Printf("%s auto      現在の Claude Code ログインに追従\n", mark(sel == "auto"))
	for _, a := range accts {
		saved := a.SavedAt
		if len(saved) > 10 {
			saved = saved[:10]
		}
		fmt.Printf("%s %s  %s（%s / 保存 %s）\n",
			mark(sel == a.Key), shortKey(a.Key), a.Label, a.Email, saved)
	}
	if len(accts) < 2 {
		fmt.Printf("\n%s別アカウントを追加するには: claude の /login でそのアカウントに一度ログインする%s\n",
			cGray, cReset)
	}
	return nil
}

// UseAccount switches the source and immediately polls once so the change is
// visible (and archived) right away.
func UseAccount(root, want string) error {
	if strings.EqualFold(want, "auto") {
		if err := writeSelection(root, "auto"); err != nil {
			return err
		}
		fmt.Println("auto（現在のログインに追従）に切り替えた。")
		return ShowLimits(root)
	}
	var hit []acctFile
	for _, a := range listAccounts(root) {
		if strings.HasPrefix(a.Key, want) || strings.EqualFold(a.Label, want) {
			hit = append(hit, a)
		}
	}
	if len(hit) == 0 {
		return fmt.Errorf("「%s」に一致するアカウントが無い。ctm limits accounts で一覧を確認", want)
	}
	if len(hit) > 1 {
		return fmt.Errorf("「%s」は複数に一致する。key をもっと長く指定", want)
	}
	if err := writeSelection(root, hit[0].Key); err != nil {
		return err
	}
	fmt.Printf("%s に切り替えた。\n\n", hit[0].Label)
	return ShowLimits(root)
}
