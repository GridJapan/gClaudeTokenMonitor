# -*- coding: utf-8 -*-
"""計測結果を md / HTML / スライド の 3 形式で書き出す。"""
import json, os, datetime

import sys
M      = sys.argv[1] if len(sys.argv) > 1 else "."
TARGET = os.environ.get("CTM_TARGET", "")
SELF   = os.environ.get("CTM_SELF", "")

ev = [json.loads(l) for l in open(os.path.join(M, "final-events.ndjson"), encoding="utf-8") if l.strip()]
base = json.load(open(os.path.join(M, "baseline.json"), encoding="ascii"))
BASE_TS = open(os.path.join(M, ".baseline_ts")).read().strip()


def agg(rows):
    f = lambda k: sum(r[k] for r in rows)
    return dict(n=len(rows), tok=f("total"), cost=sum(r["cost_usd"] for r in rows),
                inp=f("input"), cw5=f("cache_write_5m"), cw1=f("cache_write_1h"),
                cr=f("cache_read"), out=f("output"))


tgt  = [e for e in ev if e["session"].startswith(TARGET)]
mine = [e for e in ev if e["session"].startswith(SELF)]
A, B = agg(tgt), agg(mine)

t0 = datetime.datetime.fromisoformat(tgt[0]["ts"])
t1 = datetime.datetime.fromisoformat(tgt[-1]["ts"])
MIN = max((t1 - t0).total_seconds() / 60, 0.1)

pre = None
for k in base["by_session"]:
    if k["key"].startswith(TARGET):
        a = k["agg"]
        pre = dict(n=a["messages"], cost=a["cost_usd"],
                   tok=a["input_tokens"] + a["cache_write_5m_tokens"] + a["cache_write_1h_tokens"]
                       + a["cache_read_tokens"] + a["output_tokens"])

first = tgt[0]
rest_avg = sum(e["cost_usd"] for e in tgt[1:]) / max(len(tgt) - 1, 1)
cum, series = 0.0, []
for e in tgt:
    cum += e["cost_usd"]
    series.append((datetime.datetime.fromisoformat(e["ts"]), e["cost_usd"], cum))

SHARE = [("input", A["inp"]), ("cache-write 1h", A["cw1"]), ("cache-read", A["cr"]), ("output", A["out"])]
PALETTE = ["#6b7bd6", "#d68b6b", "#5fa88a", "#c96b8f"]
CR_PCT = A["cr"] / A["tok"] * 100
FIRST_PCT = first["cost_usd"] / A["cost"] * 100


def md():
    L = []
    w = L.append
    w("# トークン消費レポート\n")
    w("- 対象セッション: `%s`" % tgt[0]["session"])
    w("- 作業ディレクトリ: `%s`" % tgt[0]["project"])
    w("- 計測基準（開始前）: **%s**" % BASE_TS)
    w("- 計測終了: **%s**" % t1.strftime("%Y-%m-%d %H:%M:%S"))
    w("- 実稼働: **%.1f 分**（%s → %s）\n" % (MIN, t0.strftime("%H:%M:%S"), t1.strftime("%H:%M:%S")))

    w("## 1. 結論\n")
    w("| | 開始前 | 開始後（差分） | 合計 |")
    w("|---|---:|---:|---:|")
    w("| メッセージ | {:,} | **{:,}** | {:,} |".format(pre["n"], A["n"], pre["n"] + A["n"]))
    w("| トークン | {:,} | **{:,}** | {:,} |".format(pre["tok"], A["tok"], pre["tok"] + A["tok"]))
    w("| コスト | ${:,.4f} | **${:,.4f}** | ${:,.4f} |\n".format(pre["cost"], A["cost"], pre["cost"] + A["cost"]))
    w("この稼働で **{:,} トークン / ${:,.2f}** を消費した。平均 **{:,.0f} tok/min**・**${:.3f}/min**、"
      "1 メッセージあたり {:,} トークン / ${:.4f}。\n".format(
          A["tok"], A["cost"], A["tok"] / MIN, A["cost"] / MIN, A["tok"] // A["n"], A["cost"] / A["n"]))

    w("## 2. 種別内訳\n")
    w("| 種別 | トークン | 比率 |")
    w("|---|---:|---:|")
    for name, v in SHARE:
        w("| {} | {:,} | {:.1f}% |".format(name, v, v / A["tok"] * 100))
    w("| **合計** | **{:,}** | 100.0% |\n".format(A["tok"]))
    w("消費の **{:.1f}%** が cache-read。新規入力は {} トークンしかなく、実体は「巨大なコンテキストの"
      "読み直し」である。\n".format(CR_PCT, A["inp"]))

    w("## 3. コストの偏り\n")
    w("- 最初の 1 通（{}）だけで **${:.4f}** — 全体の **{:.1f}%**".format(
        first["ts"][11:19], first["cost_usd"], FIRST_PCT))
    w("- この回は cache-read が 0、cache-write 1h が {:,}。約 27 万トークンを新規にキャッシュ書き込みした"
      "ため単価が跳ねた（1h 書き込みは input の 2 倍レート）".format(first["cache_write_1h"]))
    w("- 2 通目以降は cache-read（0.1 倍レート）に転じ、1 通 ${:.4f} 前後で安定\n".format(rest_avg))

    w("## 4. コンテキストの膨張\n")
    w("| 時点 | cache-read | 1 通あたりコスト |")
    w("|---|---:|---:|")
    step = max(len(tgt) // 5, 1)
    for i in range(0, len(tgt), step):
        e = tgt[i]
        w("| {} | {:,} | ${:.4f} |".format(e["ts"][11:19], e["cache_read"], e["cost_usd"]))
    last = tgt[-1]
    w("| {} | {:,} | ${:.4f} |\n".format(last["ts"][11:19], last["cache_read"], last["cost_usd"]))
    growth = last["cache_read"] - tgt[1]["cache_read"] if len(tgt) > 1 else 0
    w("cache-read は {:.0f} 分で {:,} トークン増加した。会話が進むほど 1 往復の単価が上がり続ける。\n".format(MIN, growth))

    w("## 5. 全メッセージ明細\n")
    w("| # | 時刻 | in | cache-w 1h | cache-read | out | 計 | コスト | 累計 |")
    w("|---:|---|---:|---:|---:|---:|---:|---:|---:|")
    c = 0.0
    for i, e in enumerate(tgt, 1):
        c += e["cost_usd"]
        w("| {} | {} | {:,} | {:,} | {:,} | {:,} | {:,} | ${:.6f} | ${:.4f} |".format(
            i, e["ts"][11:19], e["input"], e["cache_write_1h"], e["cache_read"],
            e["output"], e["total"], e["cost_usd"], c))
    w("")

    w("## 6. 測定用セッションの分離\n")
    w("本計測を回した監視セッション `{}` は別枠で {} メッセージ / {:,} トークン / ${:.4f} を消費した。"
      "上記の対象セッションの数字には含めていない。\n".format(SELF, B["n"], B["tok"], B["cost"]))

    w("## 7. 計測中に見つけた不具合\n")
    w("明細抽出が**同一メッセージの重複行を二重計上**していた。Claude Code のログは 1 つの応答を "
      "`requestId` 単位で複数行書くため、素朴に数えると膨らむ。\n")
    w("| | 修正前 | 修正後（正） |")
    w("|---|---:|---:|")
    w("| メッセージ | 154 | **87** |")
    w("| コスト | $20.44 | **$11.03** |\n")
    w("修正後、抽出値と ctm の集計が完全一致することを確認済み（$11.030919）。あわせて ctm 本体に "
      "`-events`（1 メッセージ 1 行の NDJSON 明細）と `-since`（遡及基準時刻）を追加した。\n")

    w("## 8. 記録物\n")
    w("| ファイル | 内容 |")
    w("|---|---|")
    for f, d in [("baseline.md", "開始前スナップショット"),
                 ("before-transcript-<sid>.md", "開始前までの全やり取り＋トークン明細"),
                 ("transcript-<sid>.md", "**開始後の全やり取り全文＋各返答のトークン内訳**"),
                 ("transcript-<sid>.ndjson", "同上・ロスレス生データ"),
                 ("events.md", "1 メッセージ 1 行の明細表"),
                 ("final-events.ndjson", "確定版の全イベント（本レポートの計算元）"),
                 ("timeline.md", "20 秒ごとの推移"),
                 ("report.html / slides.html", "本レポートの HTML 版・スライド版")]:
        w("| `{}` | {} |".format(f, d))
    w("")
    w("> コストは公開リスト価格ベースの換算値。定額プランでは実請求は発生しない。\n")
    return "\n".join(L)


def svg_cum(width=880, height=300):
    pad = 52
    xs = [(d - t0).total_seconds() for d, _, _ in series]
    mx_x = max(xs) or 1
    mx_y = series[-1][2] or 1
    mx_c = max(s[1] for s in series) or 1
    pts, bars = [], []
    for i, (d, c, cu) in enumerate(series):
        x = pad + (xs[i] / mx_x) * (width - pad - 20)
        y = height - pad - (cu / mx_y) * (height - pad - 24)
        pts.append("%.1f,%.1f" % (x, y))
        bh = (c / mx_c) * (height - pad - 24)
        bars.append('<rect x="%.1f" y="%.1f" width="6" height="%.1f" fill="#6b7bd6" opacity=".30"/>'
                    % (x - 3, height - pad - bh, bh))
    grid = ""
    for f in (0, .25, .5, .75, 1):
        yy = height - pad - (height - pad - 24) * f
        grid += ('<line x1="%d" y1="%.1f" x2="%d" y2="%.1f" stroke="currentColor" opacity=".12"/>'
                 '<text x="%d" y="%.1f" text-anchor="end" font-size="11" fill="currentColor" opacity=".55">$%.1f</text>'
                 % (pad, yy, width - 20, yy, pad - 8, yy + 4, mx_y * f))
    return ('<svg viewBox="0 0 %d %d" width="100%%" role="img" aria-label="累計コストの推移">%s%s'
            '<polyline fill="none" stroke="#c96b8f" stroke-width="2.5" points="%s"/>'
            '<text x="%d" y="%d" font-size="11" fill="currentColor" opacity=".55">%s</text>'
            '<text x="%d" y="%d" text-anchor="end" font-size="11" fill="currentColor" opacity=".55">%s</text></svg>'
            % (width, height, grid, "".join(bars), " ".join(pts), pad, height - 16,
               t0.strftime("%H:%M:%S"), width - 20, height - 16, t1.strftime("%H:%M:%S")))


def svg_share(width=880, height=76):
    x, out = 0.0, []
    for (name, v), col in zip(SHARE, PALETTE):
        w = v / A["tok"] * width
        if w <= 0:
            continue
        out.append('<rect x="%.1f" y="0" width="%.1f" height="34" fill="%s"/>' % (x, max(w, 1), col))
        if w > 100:
            out.append('<text x="%.1f" y="23" font-size="12" fill="#fff">%s %.1f%%</text>'
                       % (x + 8, name, v / A["tok"] * 100))
        x += w
    leg = " ".join('<tspan fill="%s">■</tspan> %s' % (c, n) for (n, _), c in zip(SHARE, PALETTE))
    return ('<svg viewBox="0 0 %d %d" width="100%%" role="img" aria-label="種別内訳">%s'
            '<text x="0" y="58" font-size="12" fill="currentColor">%s</text></svg>'
            % (width, height, "".join(out), leg))


CSS = """
:root{--bg:#fbfaf8;--fg:#1c1b19;--mut:#6b6862;--line:#e3ded6;--card:#fff;--acc:#c96b8f;--acc2:#6b7bd6}
@media (prefers-color-scheme:dark){:root{--bg:#17161a;--fg:#eceaf0;--mut:#9d99a6;--line:#2f2d35;--card:#1f1e24}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font-family:"Segoe UI","Yu Gothic UI",system-ui,sans-serif;line-height:1.75}
.wrap{max-width:960px;margin:0 auto;padding:56px 24px 96px}
h1{font-size:2rem;margin:0 0 8px;letter-spacing:-.02em}
h2{font-size:1.25rem;margin:56px 0 16px;padding-bottom:8px;border-bottom:1px solid var(--line)}
.sub{color:var(--mut);font-size:.9rem;margin-bottom:40px}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px;margin:28px 0}
.kpi{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px}
.kpi .l{font-size:.75rem;color:var(--mut);letter-spacing:.06em;text-transform:uppercase}
.kpi .v{font-size:1.7rem;font-weight:650;margin-top:6px;font-variant-numeric:tabular-nums}
.kpi .d{font-size:.8rem;color:var(--mut);margin-top:2px}
.accent .v{color:var(--acc)}
table{border-collapse:collapse;width:100%;font-size:.86rem;font-variant-numeric:tabular-nums}
th,td{border-bottom:1px solid var(--line);padding:8px 10px;text-align:left}
th{color:var(--mut);font-weight:600;font-size:.78rem;text-transform:uppercase;letter-spacing:.04em}
td.n,th.n{text-align:right}
.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:12px;background:var(--card)}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:22px;margin:20px 0}
.note{border-left:3px solid var(--acc);padding:2px 0 2px 16px;color:var(--mut);margin:20px 0}
code{background:var(--card);border:1px solid var(--line);border-radius:5px;padding:1px 5px;font-size:.85em}
"""


def html_report():
    rows, c = [], 0.0
    for i, e in enumerate(tgt, 1):
        c += e["cost_usd"]
        rows.append("<tr><td class=n>{}</td><td>{}</td><td class=n>{:,}</td><td class=n>{:,}</td>"
                    "<td class=n>{:,}</td><td class=n>{:,}</td><td class=n>{:,}</td>"
                    "<td class=n>${:.6f}</td><td class=n>${:.4f}</td></tr>".format(
                        i, e["ts"][11:19], e["input"], e["cache_write_1h"], e["cache_read"],
                        e["output"], e["total"], e["cost_usd"], c))
    share_rows = "".join("<tr><td>{}</td><td class=n>{:,}</td><td class=n>{:.1f}%</td></tr>".format(
        n, v, v / A["tok"] * 100) for n, v in SHARE)

    return """<title>トークン消費レポート</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>{css}</style>
<div class=wrap>
<h1>トークン消費レポート</h1>
<div class=sub>セッション <code>{sid}</code> ／ {proj}<br>
計測 {base} → {end}（実稼働 {mins:.1f} 分）</div>

<div class=kpis>
  <div class="kpi accent"><div class=l>消費トークン</div><div class=v>{tokM:.2f}M</div><div class=d>{tok:,}</div></div>
  <div class="kpi accent"><div class=l>コスト</div><div class=v>${cost:.2f}</div><div class=d>リスト価格換算</div></div>
  <div class=kpi><div class=l>メッセージ</div><div class=v>{n}</div><div class=d>重複排除後</div></div>
  <div class=kpi><div class=l>速度</div><div class=v>${cpm:.2f}<span style="font-size:.9rem">/min</span></div><div class=d>{tpm:,.0f} tok/min</div></div>
</div>

<h2>開始前 / 開始後</h2>
<div class=scroll><table>
<tr><th>項目</th><th class=n>開始前</th><th class=n>開始後（差分）</th><th class=n>合計</th></tr>
<tr><td>メッセージ</td><td class=n>{pn:,}</td><td class=n><b>{n:,}</b></td><td class=n>{tn:,}</td></tr>
<tr><td>トークン</td><td class=n>{ptok:,}</td><td class=n><b>{tok:,}</b></td><td class=n>{ttok:,}</td></tr>
<tr><td>コスト</td><td class=n>${pcost:,.4f}</td><td class=n><b>${cost:,.4f}</b></td><td class=n>${tcost:,.4f}</td></tr>
</table></div>

<h2>累計コストの推移</h2>
<div class=card>{chart}<div style="color:var(--mut);font-size:.8rem">折れ線＝累計コスト、薄い棒＝各メッセージのコスト</div></div>

<h2>種別内訳</h2>
<div class=card>{share}</div>
<div class=scroll><table><tr><th>種別</th><th class=n>トークン</th><th class=n>比率</th></tr>{share_rows}
<tr><td><b>合計</b></td><td class=n><b>{tok:,}</b></td><td class=n>100.0%</td></tr></table></div>
<div class=note>消費の <b>{crpct:.1f}%</b> が cache-read。新規入力は {inp} トークンだけで、実体は巨大なコンテキストの読み直し。</div>

<h2>コストの偏り</h2>
<div class=note>最初の 1 通（{ftime}）だけで <b>${fcost:.4f}</b>、全体の <b>{fpct:.1f}%</b>。
cache-read が 0 で cache-write 1h が {fcw:,} — 約 27 万トークンを新規キャッシュ書き込みしたため単価が跳ねた
（1h 書き込みは input の 2 倍レート）。2 通目以降は cache-read（0.1 倍）に転じ 1 通 ${ravg:.4f} 前後で安定した。</div>

<h2>全メッセージ明細（{n} 件）</h2>
<div class=scroll><table>
<tr><th class=n>#</th><th>時刻</th><th class=n>in</th><th class=n>cache-w 1h</th><th class=n>cache-read</th>
<th class=n>out</th><th class=n>計</th><th class=n>コスト</th><th class=n>累計</th></tr>{rows}
</table></div>

<h2>計測中に見つけた不具合</h2>
<div class=card>明細抽出が<b>同一メッセージの重複行を二重計上</b>していた。Claude Code のログは 1 つの応答を
<code>requestId</code> 単位で複数行書くため、素朴に数えると膨らむ。
<div class=scroll style="margin-top:14px"><table>
<tr><th>項目</th><th class=n>修正前</th><th class=n>修正後（正）</th></tr>
<tr><td>メッセージ</td><td class=n>154</td><td class=n><b>87</b></td></tr>
<tr><td>コスト</td><td class=n>$20.44</td><td class=n><b>$11.03</b></td></tr>
</table></div>
<p style="margin-bottom:0">修正後、抽出値と ctm の集計が完全一致することを確認（$11.030919）。
あわせて ctm に <code>-events</code>（明細 NDJSON）と <code>-since</code>（遡及基準）を追加した。</p></div>

<h2>測定用セッションの分離</h2>
<p>本計測を回した監視セッション <code>{self}</code> は別枠で {bn} メッセージ / {btok:,} トークン / ${bcost:.4f}。
上記の対象セッションの数字には含めていない。</p>

<div class=note>コストは公開リスト価格ベースの換算値。定額プランでは実請求は発生しない。</div>
</div>""".format(
        css=CSS, sid=tgt[0]["session"], proj=tgt[0]["project"], base=BASE_TS,
        end=t1.strftime("%Y-%m-%d %H:%M:%S"), mins=MIN,
        tokM=A["tok"] / 1e6, tok=A["tok"], cost=A["cost"], n=A["n"],
        cpm=A["cost"] / MIN, tpm=A["tok"] / MIN,
        pn=pre["n"], tn=pre["n"] + A["n"], ptok=pre["tok"], ttok=pre["tok"] + A["tok"],
        pcost=pre["cost"], tcost=pre["cost"] + A["cost"],
        chart=svg_cum(), share=svg_share(), share_rows=share_rows,
        crpct=CR_PCT, inp=A["inp"], ftime=first["ts"][11:19], fcost=first["cost_usd"],
        fpct=FIRST_PCT, fcw=first["cache_write_1h"], ravg=rest_avg, rows="".join(rows),
        self=SELF, bn=B["n"], btok=B["tok"], bcost=B["cost"])


def slides_data():
    kpi = ('<div class=kpis>'
           '<div class="kpi accent"><div class=l>消費トークン</div><div class=v>{:.2f}M</div><div class=d>{:,}</div></div>'
           '<div class="kpi accent"><div class=l>コスト</div><div class=v>${:.2f}</div><div class=d>{:.1f} 分で</div></div>'
           '<div class=kpi><div class=l>メッセージ</div><div class=v>{}</div><div class=d>重複排除後</div></div>'
           '<div class=kpi><div class=l>速度</div><div class=v>${:.2f}/min</div><div class=d>{:,.0f} tok/min</div></div></div>'
           ).format(A["tok"] / 1e6, A["tok"], A["cost"], MIN, A["n"], A["cost"] / MIN, A["tok"] / MIN)

    share_rows = "".join("<tr><td>{}</td><td class=n>{:,}</td><td class=n>{:.1f}%</td></tr>".format(
        n, v, v / A["tok"] * 100) for n, v in SHARE)

    return [
        ("トークン消費レポート",
         '<p class=lead>計測対象セッション</p><p class=meta><code>{}</code><br>{}</p>'
         '<p class=meta>{} → {}<br>実稼働 {:.1f} 分</p>'.format(
             tgt[0]["session"], tgt[0]["project"], BASE_TS, t1.strftime("%Y-%m-%d %H:%M:%S"), MIN)),
        ("結論", kpi + '<p class=meta>1 メッセージあたり {:,} トークン / ${:.4f}</p>'.format(
            A["tok"] // A["n"], A["cost"] / A["n"])),
        ("開始前 → 開始後",
         '<table><tr><th>項目</th><th class=n>開始前</th><th class=n>差分</th><th class=n>合計</th></tr>'
         '<tr><td>メッセージ</td><td class=n>{:,}</td><td class=n><b>{:,}</b></td><td class=n>{:,}</td></tr>'
         '<tr><td>トークン</td><td class=n>{:,}</td><td class=n><b>{:,}</b></td><td class=n>{:,}</td></tr>'
         '<tr><td>コスト</td><td class=n>${:,.2f}</td><td class=n><b>${:,.2f}</b></td><td class=n>${:,.2f}</td></tr>'
         '</table>'.format(pre["n"], A["n"], pre["n"] + A["n"], pre["tok"], A["tok"], pre["tok"] + A["tok"],
                           pre["cost"], A["cost"], pre["cost"] + A["cost"])),
        ("累計コストの推移",
         '<div class=chart>{}</div><p class=meta>折れ線＝累計、棒＝各メッセージ</p>'.format(svg_cum(1000, 380))),
        ("消費の {:.1f}% は読み直し".format(CR_PCT),
         '<div class=chart>{}</div><table><tr><th>種別</th><th class=n>トークン</th><th class=n>比率</th></tr>{}</table>'
         '<p class=meta>新規入力はわずか {} トークン</p>'.format(svg_share(1000, 86), share_rows, A["inp"])),
        ("コストは最初の 1 通に集中",
         '<div class=big>${:.2f}</div><p class=lead>全体の {:.0f}% が最初の 1 通</p>'
         '<p class=meta>cache-read = 0 / cache-write 1h = {:,}<br>約 27 万トークンを新規キャッシュ書き込み'
         '（1h は input の 2 倍レート）<br>2 通目以降は cache-read（0.1 倍）に転じ ${:.2f} 前後で安定</p>'.format(
             first["cost_usd"], FIRST_PCT, first["cache_write_1h"], rest_avg)),
        ("見つけた不具合：二重計上",
         '<table><tr><th>項目</th><th class=n>修正前</th><th class=n>修正後（正）</th></tr>'
         '<tr><td>メッセージ</td><td class=n>154</td><td class=n><b>87</b></td></tr>'
         '<tr><td>コスト</td><td class=n>$20.44</td><td class=n><b>$11.03</b></td></tr></table>'
         '<p class=meta>ログは 1 応答を <code>requestId</code> 単位で複数行書く。素朴に数えると 1.8 倍に膨らむ。<br>'
         '修正後 ctm の集計と完全一致（$11.030919）。<code>-events</code> / <code>-since</code> を追加。</p>'),
        ("記録物",
         '<table><tr><th>ファイル</th><th>内容</th></tr>'
         '<tr><td><code>transcript-&lt;sid&gt;.md</code></td><td>全やり取り全文＋各返答のトークン内訳</td></tr>'
         '<tr><td><code>events.md</code></td><td>1 メッセージ 1 行の明細（{} 件）</td></tr>'
         '<tr><td><code>timeline.md</code></td><td>20 秒ごとの推移</td></tr>'
         '<tr><td><code>baseline.md</code></td><td>開始前スナップショット</td></tr>'
         '<tr><td><code>report.md / .html</code></td><td>本レポート</td></tr></table>'
         '<p class=meta>測定用セッション {} は別枠 {} msgs / ${:.2f}（対象に含めず）</p>'.format(
             A["n"], SELF, B["n"], B["cost"])),
    ]


def html_slides():
    S = slides_data()
    secs = "".join(
        '<section class=slide><div class=inner><div class=num>{} / {}</div><h2>{}</h2>{}</div></section>'.format(
            i + 1, len(S), t, b) for i, (t, b) in enumerate(S))
    extra = """
body{overflow-y:scroll;scroll-snap-type:y mandatory;height:100vh}
.slide{min-height:100vh;scroll-snap-align:start;display:flex;align-items:center;justify-content:center;padding:48px 24px;border-bottom:1px solid var(--line)}
.inner{max-width:1040px;width:100%}
.num{font-size:.72rem;color:var(--mut);letter-spacing:.14em;margin-bottom:18px}
.slide h2{font-size:2.4rem;border:0;margin:0 0 28px;padding:0;letter-spacing:-.02em}
.lead{font-size:1.4rem;margin:0 0 12px}
.meta{color:var(--mut);font-size:.95rem}
.big{font-size:5.5rem;font-weight:700;color:var(--acc);line-height:1;margin:0 0 12px}
.chart{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:22px;margin-bottom:18px}
.slide table{font-size:1rem}
.slide .kpi .v{font-size:2.1rem}
"""
    script = """
addEventListener('keydown',function(e){
  var s=[].slice.call(document.querySelectorAll('.slide'));
  var i=s.findIndex(function(x){return x.getBoundingClientRect().top>-window.innerHeight/2});
  if(e.key==='ArrowRight'||e.key==='PageDown'||e.key===' '){e.preventDefault();if(s[Math.min(i+1,s.length-1)])s[Math.min(i+1,s.length-1)].scrollIntoView({behavior:'smooth'})}
  if(e.key==='ArrowLeft'||e.key==='PageUp'){e.preventDefault();if(s[Math.max(i-1,0)])s[Math.max(i-1,0)].scrollIntoView({behavior:'smooth'})}
});
"""
    return ('<title>トークン消費 スライド</title>'
            '<meta name="viewport" content="width=device-width,initial-scale=1">'
            '<style>' + CSS + extra + '</style>' + secs + '<script>' + script + '</script>')


for name, body in [("report.md", md()), ("report.html", html_report()), ("slides.html", html_slides())]:
    with open(os.path.join(M, name), "w", encoding="utf-8") as f:
        f.write(body)
    print(name, len(body), "chars")
