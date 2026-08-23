// ctm-atom — ATOMS3R (128x128) をサブモニタとして使うファームウェア。
//
// PC 側 (CtmMonitor.exe) が 200ms ごとに 1 行 JSON を USB CDC へ送ってくる。
// 本機は表示専用: 受け取った状態を「レイアウト 2A」（PC の Big レイアウトを
// 低解像度向けに再構成したもの）で描く。数字のスプリング補間・水槽・
// バースト演出はデバイス側で 30fps で動かすので、200ms 間隔でもなめらか。
//
// IMU (BMI270) で重力方向を監視し、本体が物理的に回されたら表示を
// 0/90/180/270° の最寄りへ「ボールが転がるように」ゆっくり回して合わせる。
//
// 受信 (PC → ATOM, NDJSON):
//   {"per":"5H","tok":52830000,"pct5":34.5,"pctw":61.2,
//    "f5":0.42,"fw":0.77,"cost":57.31,"rec":1,"src":"dir 名"}
// 送信 (ATOM → PC): 起動時と {"ping":1} 受信時に {"hello":"ctm-atom",...} を返す。
// {"dbg":1} で IMU の生値を 1 秒ごとに出す（軸マッピングの現物合わせ用）。

#include <M5Unified.h>
#include <math.h>
#include "soc/rtc_cntl_reg.h"   // {"flash":1} でダウンロードモードに入るため

static const char *FW_VER = "0.2.0";

// ---- 画面・色 ------------------------------------------------------------
static const int W = 128, H = 128;

static M5Canvas content(&M5.Display);   // 直立で描くレイヤ
static M5Canvas screenBuf(&M5.Display); // 回転合成して LCD へ送るレイヤ

static uint16_t C_BG, C_FG, C_MUT, C_WARN, C_BAD, C_MINT, C_GOLD;
static uint16_t C_W_BLUE, C_W_BOTH, C_W_PURP;      // 水: 青のみ / 重なり / 紫のみ
static uint16_t C_W_BLUE_D, C_W_BOTH_D, C_W_PURP_D; // その深い部分
static uint16_t C_SURF_S, C_SURF_W;                 // 水面線
static uint16_t C_BAR_S, C_BAR_W, C_BAR_S_T, C_BAR_W_T; // 縁バー (fill/track)
static uint16_t C_LEG_S, C_LEG_W;                   // 凡例の文字色

static uint16_t blend565(uint8_t r1, uint8_t g1, uint8_t b1,
                         uint8_t r2, uint8_t g2, uint8_t b2, float a)
{
    return content.color565(
        (uint8_t)(r1 + (r2 - r1) * a),
        (uint8_t)(g1 + (g2 - g1) * a),
        (uint8_t)(b1 + (b2 - b1) * a));
}

static void initColors()
{
    // PC 版 Theme と同じ系統。アルファ合成は事前計算して 16bit 固定色にする。
    C_BG   = content.color565(24, 23, 28);
    C_FG   = content.color565(236, 234, 240);
    C_MUT  = content.color565(150, 146, 160);
    C_WARN = content.color565(214, 139, 107);
    C_BAD  = content.color565(201, 107, 143);
    C_MINT = content.color565(150, 235, 170);
    C_GOLD = content.color565(255, 224, 120);

    // 背景(24,23,28) に 紫(150,115,235) α0.35 / 青(96,155,245) α0.45 を重ねた色
    C_W_PURP   = blend565(24, 23, 28, 150, 115, 235, 0.35f);
    C_W_BLUE   = blend565(24, 23, 28, 96, 155, 245, 0.45f);
    { // 紫の上に青: まず紫を作り、その RGB に青を重ねる
        uint8_t pr = 24 + (uint8_t)((150 - 24) * 0.35f);
        uint8_t pg = 23 + (uint8_t)((115 - 23) * 0.35f);
        uint8_t pb = 28 + (uint8_t)((235 - 28) * 0.35f);
        C_W_BOTH = blend565(pr, pg, pb, 96, 155, 245, 0.45f);
    }
    // 深部はさらに暗色へ寄せて「深さ」を出す
    C_W_PURP_D = blend565(24, 23, 28, 55, 38, 130, 0.55f);
    C_W_BLUE_D = blend565(24, 23, 28, 30, 52, 135, 0.60f);
    C_W_BOTH_D = blend565(20, 22, 40, 40, 45, 140, 0.55f);

    C_SURF_S = content.color565(170, 210, 255);
    C_SURF_W = content.color565(200, 170, 255);
    C_BAR_S  = content.color565(120, 180, 255);
    C_BAR_W  = content.color565(190, 160, 255);
    C_BAR_S_T = blend565(24, 23, 28, 120, 180, 255, 0.22f);
    C_BAR_W_T = blend565(24, 23, 28, 190, 160, 255, 0.22f);
    C_LEG_S  = content.color565(140, 190, 255);
    C_LEG_W  = content.color565(190, 160, 255);
}

// ---- PC から届く状態 -------------------------------------------------------
struct RxState {
    char   per[8]   = "5H";
    double tok      = 0;
    double pct5     = 0, pctw = 0;
    double f5       = -1, fw = -1;   // 窓の経過 0..1（-1 = 不明）
    double cost     = 0;
    int    rec      = 1;
    char   src[96]  = "";            // 直近バーストの作業ディレクトリ（あれば）
    uint32_t lastRx = 0;             // 最終受信 millis
    bool   ever     = false;         // 一度でも受信したか
};
static RxState rx;
static bool dbgImu = false;

// ---- 演出状態（PC の Big と同じ定数系） -------------------------------------
static double shown = 0, target = 0, vel = 0;
static bool   primed = false;
static float  flash = 0;             // バースト直後の金色
static float  chop = 0;              // 波の荒れ
static float  m2 = 0, m2v = 0;       // 中央がぼよんと跳ねる対称モード
static double waterPhase = 0;
static int    glintFor = 0;
static char   curPer[8] = "5H";

struct Floaty { char txt[20]; char src[96]; float life; };
static Floaty flo[3];
static int    floN = 0;

// ---- 回転（IMU） -----------------------------------------------------------
static float ang = 0, angVel = 0;    // 表示角（度）
static int   targetAng = 0;          // 0/90/180/270
static int   candAng = 0, candFrames = 0;

// ---- 最小 JSON 読み（PC 側 CtmMonitor.cs と同じ流儀） ----------------------
static bool jsonNum(const char *line, const char *key, double *out)
{
    char pat[24];
    snprintf(pat, sizeof(pat), "\"%s\":", key);
    const char *p = strstr(line, pat);
    if (!p) return false;
    p += strlen(pat);
    char *end;
    double v = strtod(p, &end);
    if (end == p) return false;
    *out = v;
    return true;
}

static bool jsonStr(const char *line, const char *key, char *out, size_t cap)
{
    char pat[24];
    snprintf(pat, sizeof(pat), "\"%s\":\"", key);
    const char *p = strstr(line, pat);
    if (!p) { return false; }
    p += strlen(pat);
    size_t n = 0;
    while (*p && *p != '"' && n + 1 < cap) {
        if (*p == '\\' && p[1]) p++;   // エスケープは素通し（表示用途なので十分）
        out[n++] = *p++;
    }
    out[n] = 0;
    return true;
}

// ---- トークン数の表示（PC の Store.Tokens AUTO と同じ丸め） -----------------
static void fmtTokens(double n, char *out, size_t cap)
{
    if (n >= 1e9)      snprintf(out, cap, "%.2fG", n / 1e9);
    else if (n >= 1e6) snprintf(out, cap, "%.2fM", n / 1e6);
    else if (n >= 1e3) snprintf(out, cap, "%.1fK", n / 1e3);
    else               snprintf(out, cap, "%d", (int)n);
}

// ---- バースト演出 -----------------------------------------------------------
static void triggerFx(double delta)
{
    int tier = delta < 50000 ? 1 : delta < 200000 ? 2 : delta < 1000000 ? 3 : 4;
    flash = 0.5f + 0.125f * tier;
    chop  = fminf(1.6f, chop + 0.10f + 0.08f * tier);
    m2v  -= 0.9f * tier;
    glintFor = 45 + tier * 15;

    if (floN == 3) { flo[0] = flo[1]; flo[1] = flo[2]; floN = 2; }
    char t[20];
    fmtTokens(delta, t, sizeof(t));
    snprintf(flo[floN].txt, sizeof(flo[floN].txt), "+%s", t);
    strlcpy(flo[floN].src, rx.src, sizeof(flo[floN].src));
    flo[floN].life = 1.0f;
    floN++;
}

// ---- 受信 1 行の反映 ---------------------------------------------------------
static void sendHello()
{
    Serial.printf("{\"hello\":\"ctm-atom\",\"fw\":\"%s\"}\n", FW_VER);
}

static void applyLine(const char *line)
{
    double v;
    if (jsonNum(line, "ping", &v)) { sendHello(); return; }
    if (jsonNum(line, "dbg", &v)) { dbgImu = v != 0; return; }
    if (jsonNum(line, "flash", &v) && v != 0) {
        // 次回書き込みを手放しで行うための入口。ROM ダウンロードモードへ
        // 再起動する（esptool は --before no_reset で開く）。
        Serial.println("{\"bye\":\"download-mode\"}");
        Serial.flush();
        delay(100);
        REG_WRITE(RTC_CNTL_OPTION1_REG, RTC_CNTL_FORCE_DOWNLOAD_BOOT);
        esp_restart();
    }
    if (!jsonNum(line, "tok", &v)) return;   // 状態行以外は無視

    char per[8];
    if (jsonStr(line, "per", per, sizeof(per)) && strcmp(per, curPer) != 0) {
        strlcpy(curPer, per, sizeof(curPer));    // 期間切替はスナップ（演出しない）
        shown = target = v;
        vel = 0;
        primed = true;
    }
    rx.src[0] = 0;
    jsonStr(line, "src", rx.src, sizeof(rx.src));
    jsonNum(line, "pct5", &rx.pct5);
    jsonNum(line, "pctw", &rx.pctw);
    jsonNum(line, "f5", &rx.f5);
    jsonNum(line, "fw", &rx.fw);
    jsonNum(line, "cost", &rx.cost);
    double r = 1;
    jsonNum(line, "rec", &r);
    rx.rec = (int)r;
    strlcpy(rx.per, curPer, sizeof(rx.per));
    rx.tok = v;
    rx.lastRx = millis();
    rx.ever = true;

    if (!primed) { primed = true; shown = target = v; }
    else if (v > target + 0.5) { triggerFx(v - target); target = v; }
    else if (v < target - 0.5) { target = v; shown = v; vel = 0; }  // 窓リセット
}

static void pollSerial()
{
    static char buf[640];
    static size_t n = 0;
    while (Serial.available()) {
        char c = (char)Serial.read();
        if (c == '\n' || c == '\r') {
            if (n > 0) { buf[n] = 0; applyLine(buf); n = 0; }
        } else if (n + 1 < sizeof(buf)) {
            buf[n++] = c;
        } else {
            n = 0;   // 長すぎる行は捨てる
        }
    }
}

// ---- IMU → 目標角 ------------------------------------------------------------
// 画面と同じ面内の重力成分から「どの辺が下か」を決める。斜め置き・平置きでは
// 変えない（ヒステリシス: 同じ候補が 10 フレーム続いたときだけ確定）。
static void updateOrientation()
{
    if (!M5.Imu.update()) return;
    float ax, ay, az;
    M5.Imu.getAccel(&ax, &ay, &az);

    if (dbgImu) {
        static uint32_t last = 0;
        if (millis() - last > 1000) {
            last = millis();
            Serial.printf("{\"acc\":[%.2f,%.2f,%.2f],\"target\":%d,\"ang\":%.1f}\n",
                          ax, ay, az, targetAng, ang);
        }
    }

    float mag = sqrtf(ax * ax + ay * ay);
    if (mag < 0.55f) { candFrames = 0; return; }   // ほぼ平置き → 現状維持

    // AtomS3R 実機合わせ済み (2026-08-23): X はそのまま、Y は符号が想定と逆
    // だった（「上下が逆。左右は正しい」）。ここは現物基準の表なので、
    // 変更するときは必ず実機で 4 方向を回して確認すること。
    int cand;
    if (fabsf(ax) > fabsf(ay)) cand = (ax > 0) ? 90 : 270;
    else                       cand = (ay > 0) ? 0 : 180;

    if (cand != candAng) { candAng = cand; candFrames = 0; }
    if (++candFrames >= 10 && cand != targetAng) {
        targetAng = cand;
        candFrames = 0;
    }
}

// 目標角へバネで追従。最短経路で回り、わずかに行き過ぎてから収まる
// （ボールが転がって止まる感じ）。
static void animateAngle()
{
    float diff = (float)targetAng - ang;
    while (diff > 180.0f)  diff -= 360.0f;
    while (diff < -180.0f) diff += 360.0f;
    angVel = angVel * 0.93f + diff * 0.011f;
    ang += angVel;
    if (ang >= 360.0f) ang -= 360.0f;
    if (ang < 0.0f)    ang += 360.0f;
    if (fabsf(diff) < 0.05f && fabsf(angVel) < 0.05f) { ang = targetAng % 360; angVel = 0; }
}

// ---- 水槽 --------------------------------------------------------------------
static float levelFor(double pct)
{
    // 0% = 下端近く、100% = 上端近く
    if (pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    return (float)(H - 10 - (H - 24) * pct / 100.0);
}

static float surfaceY(float level, float x, float amp, float k, double phase)
{
    float u = x / (float)W;
    float a = amp * (1.0f + chop * 1.8f);
    return level
        + m2 * cosf(2.0f * (float)M_PI * u) * 0.5f
        + a * sinf(x * k + (float)phase)
        + a * 0.55f * sinf(x * k * 2.6f - (float)phase * 1.6f);
}

static void drawWater()
{
    float lvW = levelFor(rx.pctw);
    float lvS = levelFor(rx.pct5);
    for (int x = 0; x < W; x++) {
        float yw = surfaceY(lvW, (float)x, 1.7f, 0.065f, waterPhase * 0.6 + 1.3);
        float ys = surfaceY(lvS, (float)x, 1.6f, 0.085f, waterPhase);
        int iw = (int)yw, is = (int)ys;
        if (iw < 0) iw = 0; if (iw > H) iw = H;
        if (is < 0) is = 0; if (is > H) is = H;
        int top = iw < is ? iw : is;      // 高い方（画面上で上）の水面
        int bot = iw < is ? is : iw;
        uint16_t onlyC  = iw < is ? C_W_PURP : C_W_BLUE;   // 単層部分の色
        uint16_t onlyD  = iw < is ? C_W_PURP_D : C_W_BLUE_D;
        // 単層部分（深いところは暗色に切り替え）
        if (bot > top) {
            int deep = top + 26;
            if (deep > bot) deep = bot;
            content.drawFastVLine(x, top, deep - top, onlyC);
            if (bot > deep) content.drawFastVLine(x, deep, bot - deep, onlyD);
        }
        // 両層が重なる部分
        if (H > bot) {
            int deep = bot + 26;
            if (deep > H) deep = H;
            content.drawFastVLine(x, bot, deep - bot, C_W_BOTH);
            if (H > deep) content.drawFastVLine(x, deep, H - deep, C_W_BOTH_D);
        }
        // 水面線（1px）
        if (iw >= 0 && iw < H) content.drawPixel(x, iw, C_SURF_W);
        if (is >= 0 && is < H) content.drawPixel(x, is, C_SURF_S);
    }
    // 水面グリント: バースト中、5h 水面に光点がまたたく
    if (glintFor > 0 && (glintFor & 1)) {
        for (int i = 0; i < 2; i++) {
            int x = rand() % W;
            int y = (int)surfaceY(lvS, (float)x, 1.6f, 0.085f, waterPhase) - 1;
            if (y >= 0 && y < H) content.drawFastHLine(x, y, 2, C_FG);
        }
    }
}

static void drawEdgeBars()
{
    // 上端 = 5h 窓の経過（青）、下端 = 週の経過（紫）。PC と同じ向き。
    if (rx.f5 >= 0) {
        content.fillRect(1, 0, W - 2, 2, C_BAR_S_T);
        int w = (int)((W - 2) * rx.f5);
        if (w > 0) content.fillRect(1, 0, w, 2, C_BAR_S);
    }
    if (rx.fw >= 0) {
        content.fillRect(1, H - 2, W - 2, 2, C_BAR_W_T);
        int w = (int)((W - 2) * rx.fw);
        if (w > 0) content.fillRect(1, H - 2, w, 2, C_BAR_W);
    }
}

// ---- 数字（スプリング + 自動フィット） ----------------------------------------
static const lgfx::IFont *BOLD_F[3] = {
    &fonts::FreeSansBold24pt7b, &fonts::FreeSansBold18pt7b, &fonts::FreeSansBold12pt7b };
static const lgfx::IFont *REG_F[3] = {
    &fonts::FreeSans24pt7b, &fonts::FreeSans18pt7b, &fonts::FreeSans12pt7b };

static void drawNumber()
{
    char tok[24];
    fmtTokens(shown, tok, sizeof(tok));

    // 数字部と単位 (K/M/G) を分け、単位はレギュラーで軽く見せる
    int sufAt = strlen(tok);
    while (sufAt > 0 && isalpha((unsigned char)tok[sufAt - 1])) sufAt--;
    char numPart[24], sufPart[8];
    strlcpy(numPart, tok, sufAt + 1);
    strlcpy(sufPart, tok + sufAt, sizeof(sufPart));

    int fi = 0, wNum = 0, wSuf = 0;
    for (fi = 0; fi < 3; fi++) {
        content.setFont(BOLD_F[fi]);
        wNum = content.textWidth(numPart);
        content.setFont(REG_F[fi]);
        wSuf = sufPart[0] ? content.textWidth(sufPart) : 0;
        if (wNum + wSuf <= W - 10) break;
    }
    if (fi == 3) fi = 2;

    // 色は状態で変える: 平常=白 / 70%↑=橙 / 90%↑=赤、カウント中はミント、
    // バースト直後は金色に光って戻る（PC と同じ規則）。
    double maxPct = rx.pct5 > rx.pctw ? rx.pct5 : rx.pctw;
    uint8_t r = 236, g = 234, b = 240;
    if (maxPct >= 90)      { r = 201; g = 107; b = 143; }
    else if (maxPct >= 70) { r = 214; g = 139; b = 107; }
    float counting = (float)fmin(1.0, fabs(target - shown) / 4000.0) * 0.8f;
    r = (uint8_t)(r + (150 - r) * counting);
    g = (uint8_t)(g + (235 - g) * counting);
    b = (uint8_t)(b + (170 - b) * counting);
    float fl = flash > 1.0f ? 1.0f : flash;
    r = (uint8_t)(r + (255 - r) * fl);
    g = (uint8_t)(g + (224 - g) * fl);
    b = (uint8_t)(b + (120 - b) * fl);
    uint16_t col = content.color565(r, g, b);
    uint16_t sh  = content.color565(10, 10, 14);

    content.setTextDatum(middle_left);
    int x0 = (W - (wNum + wSuf)) / 2;
    int cy = H / 2 - 6;
    content.setFont(BOLD_F[fi]);
    content.setTextColor(sh);
    content.drawString(numPart, x0 + 1, cy + 1);
    content.setTextColor(col);
    content.drawString(numPart, x0, cy);
    if (sufPart[0]) {
        content.setFont(REG_F[fi]);
        content.setTextColor(sh);
        content.drawString(sufPart, x0 + wNum + 1, cy + 1);
        content.setTextColor(col);
        content.drawString(sufPart, x0 + wNum, cy);
    }
}

static void drawChrome()
{
    content.setTextDatum(top_center);
    content.setFont(&fonts::Font0);
    content.setTextColor(C_MUT);
    content.drawString(rx.per, W / 2, 6);
    content.drawString("tokens", W / 2, H / 2 + 16);

    // コスト（選択期間）
    char c[16];
    snprintf(c, sizeof(c), "$%.2f", rx.cost);
    content.setTextColor(C_FG);
    content.drawString(c, W / 2, H - 26);

    // 凡例: 5h / week の使用率。水の色と対応
    char l1[16], l2[16];
    snprintf(l1, sizeof(l1), "5h %d%%", (int)(rx.pct5 + 0.5));
    snprintf(l2, sizeof(l2), "wk %d%%", (int)(rx.pctw + 0.5));
    int w1 = content.textWidth(l1), w2 = content.textWidth(l2);
    int lx = (W - (w1 + 8 + w2)) / 2;
    content.setTextDatum(top_left);
    content.setTextColor(C_LEG_S);
    content.drawString(l1, lx, H - 14);
    content.setTextColor(C_LEG_W);
    content.drawString(l2, lx + w1 + 8, H - 14);

    if (!rx.rec) {
        content.setTextDatum(top_left);
        content.setTextColor(C_BAD);
        content.drawString("!REC", 4, 5);
    }
}

static void drawFloats()
{
    for (int i = 0; i < floN; i++) {
        float life = flo[i].life;
        float rise = (1.0f - powf(life, 0.6f)) * 30.0f;   // ease-out で 30px 上昇
        int fy = H / 2 - 26 - (int)rise;
        int a = (int)(255 * fminf(1.0f, life * 3.0f));
        // 16bit 直塗りなので不透明度はミント→背景の補間で表す
        uint16_t col = blend565(24, 23, 28, 150, 235, 170, a / 255.0f);
        content.setTextDatum(top_center);
        content.setFont(&fonts::FreeSansBold9pt7b);
        content.setTextColor(col);
        content.drawString(flo[i].txt, W / 2, fy);
        if (flo[i].src[0]) {
            uint16_t sc = blend565(24, 23, 28, 205, 220, 235, a / 255.0f * 0.8f);
            content.setFont(&fonts::lgfxJapanGothic_12);
            content.setTextColor(sc);
            // 収まらないときは末尾を削る
            char s[96];
            strlcpy(s, flo[i].src, sizeof(s));
            while (content.textWidth(s) > W - 8 && strlen(s) > 3)
                s[strlen(s) - 1] = 0;
            content.drawString(s, W / 2, fy + 18);
        }
    }
}

static void drawWaiting()
{
    content.setTextDatum(top_center);
    content.setFont(&fonts::FreeSansBold12pt7b);
    content.setTextColor(C_FG);
    content.drawString("ctm", W / 2, 38);
    content.setFont(&fonts::lgfxJapanGothic_12);
    content.setTextColor(C_MUT);
    content.drawString("PC 待機中...", W / 2, 66);
    content.setFont(&fonts::Font0);
    content.drawString(FW_VER, W / 2, 88);
}

static void drawNoLink()
{
    // 数秒データが来ない: 最後の状態は残したまま、点滅の帯で知らせる
    if ((millis() / 500) & 1) {
        content.fillRect(0, 54, W, 20, content.color565(60, 30, 36));
        content.setTextDatum(top_center);
        content.setFont(&fonts::Font0);
        content.setTextColor(content.color565(255, 170, 170));
        content.drawString("NO LINK", W / 2, 60);
    }
}

// ---- メイン -------------------------------------------------------------------
void setup()
{
    auto cfg = M5.config();
    M5.begin(cfg);
    M5.Display.setBrightness(160);

    Serial.begin(115200);

    content.setColorDepth(16);
    content.createSprite(W, H);
    content.setPivot(W / 2.0f - 0.5f, H / 2.0f - 0.5f);
    screenBuf.setColorDepth(16);
    screenBuf.createSprite(W, H);

    initColors();
    sendHello();
}

void loop()
{
    uint32_t t0 = millis();
    M5.update();
    pollSerial();
    updateOrientation();
    animateAngle();

    // ---- 物理・演出の 1 ステップ（30fps 前提の定数） ----
    vel = vel * 0.87 + (target - shown) * 0.095;
    shown += vel;
    if (shown > target) { shown = target; vel = 0; }   // カウンタは逆走させない
    if (fabs(target - shown) < 0.6 && fabs(vel) < 0.6) { shown = target; vel = 0; }
    if (flash > 0.01f) flash *= 0.90f; else flash = 0.0f;
    m2v = m2v * 0.925f - m2 * 0.095f;
    m2 += m2v;
    if (m2 > 9.0f) { m2 = 9.0f; m2v *= -0.35f; }
    if (m2 < -9.0f) { m2 = -9.0f; m2v *= -0.35f; }
    chop *= 0.95f;
    if (glintFor > 0) glintFor--;
    waterPhase += 0.05 + chop * 0.25;
    for (int i = floN - 1; i >= 0; i--) {
        flo[i].life -= 1.0f / 75.0f;                        // 2.5 秒で消える
        if (flo[i].life <= 0.0f) {
            for (int j = i; j < floN - 1; j++) flo[j] = flo[j + 1];
            floN--;
        }
    }

    // ---- 描画 ----
    content.fillSprite(C_BG);
    if (!rx.ever) {
        drawWaiting();
    } else {
        drawWater();
        drawEdgeBars();
        drawChrome();
        drawNumber();
        drawFloats();
        if (millis() - rx.lastRx > 3500) drawNoLink();
    }

    // 回転合成: 回転中はほんの少し縮めて「転がっている」立体感を出す
    float diff = (float)targetAng - ang;
    while (diff > 180.0f)  diff -= 360.0f;
    while (diff < -180.0f) diff += 360.0f;
    float prog = fminf(1.0f, fabsf(diff) / 90.0f);
    float scale = 1.0f - 0.14f * sinf(prog * (float)M_PI);

    screenBuf.fillSprite(TFT_BLACK);
    content.pushRotateZoomWithAA(&screenBuf, W / 2.0f - 0.5f, H / 2.0f - 0.5f,
                                 ang, scale, scale);
    screenBuf.pushSprite(0, 0);

    uint32_t spent = millis() - t0;
    if (spent < 33) delay(33 - spent);
}
