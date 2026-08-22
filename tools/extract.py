# -*- coding: utf-8 -*-
"""Claude Code のセッションログから、やり取りの全文とトークン明細を書き出す。

価格とトークン数は ctm の -events 出力（message.id|requestId をキー）と突き合わせるので、
集計値と 1 円もずれない。
"""
import json, os, sys, glob, datetime, subprocess

CTM  = os.environ.get("CTM_EXE") or "ctm.exe"
ROOT = os.environ.get("CLAUDE_PROJECTS") or os.path.join(
    os.path.expanduser("~"), ".claude", "projects")
CAP  = 20000  # md 内の 1 ブロック上限。超過分は raw ndjson に全文あり


def find_log(sid):
    hits = glob.glob(os.path.join(ROOT, "*", sid + "*.jsonl"))
    return hits[0] if hits else None


def load_costs(sid):
    """ctm から当該セッションの明細を読み、dedup キー -> 明細 の辞書にする。"""
    r = subprocess.run([CTM, "-session", sid, "-events"], capture_output=True, timeout=120)
    out = {}
    for line in r.stdout.decode("ascii").splitlines():
        if not line.strip():
            continue
        e = json.loads(line)
        out[e["key"]] = e
    return out


def render(block):
    """content ブロック 1 個を (種別, 本文) にする。"""
    t = block.get("type")
    if t == "text":
        return "text", block.get("text", "")
    if t == "thinking":
        return "thinking", block.get("thinking", "")
    if t == "tool_use":
        return "tool_use:" + str(block.get("name")), json.dumps(
            block.get("input"), ensure_ascii=False, indent=2)
    if t == "tool_result":
        c = block.get("content")
        if isinstance(c, list):
            c = "\n".join(x.get("text", "") for x in c if isinstance(x, dict))
        return "tool_result", c if isinstance(c, str) else json.dumps(c, ensure_ascii=False)
    if t == "image":
        return "image", "(画像)"
    return t or "unknown", json.dumps(block, ensure_ascii=False)[:2000]


def content_blocks(msg):
    c = msg.get("content")
    if isinstance(c, str):
        return [("text", c)]
    if isinstance(c, list):
        return [render(b) for b in c if isinstance(b, dict)]
    return []


def fence(body, kind):
    body = body if isinstance(body, str) else str(body)
    n = len(body)
    if n > CAP:
        body = body[:CAP] + f"\n\n... (以下 {n - CAP:,} 文字省略 / 全文は raw ndjson)"
    lang = "json" if kind.startswith("tool_use") else ""
    return f"```{lang}\n{body}\n```" if body.strip() else "_(空)_"


def extract(sid, since=None, outdir="."):
    path = find_log(sid)
    if not path:
        return None
    costs = load_costs(sid)
    since_dt = datetime.datetime.fromisoformat(since) if since else None

    rows, raw = [], []
    counted = set()   # 同一 key の行は 1 回だけ計上する（ログは同じメッセージを複数行書く）
    dup = 0
    turn = 0
    cum_cost = 0.0
    cum_tok = 0
    totals = dict(msgs=0, tok=0, cost=0.0, inp=0, cw5=0, cw1=0, cr=0, out=0)

    for line in open(path, encoding="utf-8", errors="replace"):
        try:
            j = json.loads(line)
        except Exception:
            continue
        typ = j.get("type")
        if typ not in ("user", "assistant"):
            continue
        ts = j.get("timestamp")
        if not ts:
            continue
        dt = datetime.datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone()
        if since_dt and dt < since_dt:
            continue
        msg = j.get("message") or {}
        blocks = content_blocks(msg)
        raw.append(json.dumps({"ts": dt.isoformat(), "type": typ, "uuid": j.get("uuid"),
                               "requestId": j.get("requestId"), "message": msg}, ensure_ascii=False))
        turn += 1

        if typ == "user":
            rows.append(f"\n### {turn}. 🧑 ユーザー — {dt:%H:%M:%S}\n")
            for kind, body in blocks:
                rows.append(f"**{kind}**\n\n{fence(body, kind)}\n")
            rows.append("> トークン消費: なし（入力は次の assistant メッセージの usage に計上される）\n")
        else:
            key = (msg.get("id") or "") + "|" + (j.get("requestId") or "")
            ev = costs.get(key)
            rows.append(f"\n### {turn}. 🤖 アシスタント — {dt:%H:%M:%S}\n")
            for kind, body in blocks:
                rows.append(f"**{kind}**\n\n{fence(body, kind)}\n")
            if ev and key in counted:
                dup += 1
                rows.append("> 重複行（同一 key は計上済み）。トークンは二重計上しない。" + chr(10))
            elif ev:
                counted.add(key)
                cum_cost += ev["cost_usd"]; cum_tok += ev["total"]
                totals["msgs"] += 1; totals["tok"] += ev["total"]; totals["cost"] += ev["cost_usd"]
                totals["inp"] += ev["input"]; totals["cw5"] += ev["cache_write_5m"]
                totals["cw1"] += ev["cache_write_1h"]; totals["cr"] += ev["cache_read"]
                totals["out"] += ev["output"]
                rows.append(
                    "| in | cache-w 5m | cache-w 1h | cache-r | out | 計 | コスト | 累計コスト |\n"
                    "|---:|---:|---:|---:|---:|---:|---:|---:|\n"
                    f"| {ev['input']:,} | {ev['cache_write_5m']:,} | {ev['cache_write_1h']:,} | "
                    f"{ev['cache_read']:,} | {ev['output']:,} | **{ev['total']:,}** | "
                    f"**${ev['cost_usd']:.6f}** | ${cum_cost:.4f} |\n")
            else:
                rows.append("> 課金対象の usage なし（重複行 / synthetic / usage ゼロ）\n")

    head = [f"# セッション {sid} 明細トランスクリプト\n",
            f"- ログ: `{os.path.basename(path)}`",
            f"- 生成: {datetime.datetime.now():%Y-%m-%d %H:%M:%S}",
            f"- 範囲: {since or '全期間'} 以降",
            f"- ターン数: {turn}（うち課金対象 assistant {totals['msgs']} / 重複行 {dup}）",
            f"- 合計トークン: **{totals['tok']:,}** / コスト **${totals['cost']:.4f}**",
            f"- 内訳: input {totals['inp']:,} / cache-write 5m {totals['cw5']:,} / "
            f"cache-write 1h {totals['cw1']:,} / cache-read {totals['cr']:,} / output {totals['out']:,}",
            f"- md 内の 1 ブロックは {CAP:,} 文字で打ち切り。全文は `transcript-{sid[:8]}.ndjson` にある。\n",
            "---\n"]

    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, f"transcript-{sid[:8]}.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(head) + "\n".join(rows))
    with open(os.path.join(outdir, f"transcript-{sid[:8]}.ndjson"), "w", encoding="utf-8") as f:
        f.write("\n".join(raw) + "\n")
    return totals


if __name__ == "__main__":
    sid = sys.argv[1]
    since = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != "-" else None
    outdir = sys.argv[3] if len(sys.argv) > 3 else "."
    t = extract(sid, since, outdir)
    print(json.dumps(t, ensure_ascii=False) if t else "not found")
