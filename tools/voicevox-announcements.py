#!/usr/bin/env python3
"""場内放送（ウグイス嬢・issue #919）の音声部品を VOICEVOX で生成する。

声: VOICEVOX:No.7（アナウンス）。文面は「4番、キャッチャー、田中くん。」、代打は「バッター、田中くん。」。
部品は3種類で、ゲームが順につないで鳴らす（engine の PaAnnouncement が並びを決める）:
  order_1 .. order_9       「1番、」〜「9番、」
  pos_p / pos_c / pos_1b / pos_2b / pos_3b / pos_ss / pos_lf / pos_cf / pos_rf / pos_dh
                           「ピッチャー、」〜「ライト、」「指名打者、」
  batter                   「バッター、」
  sei_xxxxxxxx             「田中くん。」（苗字ごとに1本。名前の途中につなぎ目を作らない）
苗字の部品名は苗字の UTF-8 の FNV-1a 32bit を16進8桁にしたもの（engine の PaAnnouncement.SurnameClip と同じ規則。
engine のテスト SurnameClip_UsesFnv1aOfUtf8_SameAsTheGeneratorScript が既知値で固定している）。
苗字の一覧は data/player-names.yaml の family_names。

生成物は Git 管理外（Assets/KokoSim/Audio/Vendor/）。No.7 は「VOICEVOX:No.7」のクレジット表記で
非商用利用可・商用は No.7 製作委員会へ事前確認（https://voiceseven.com/）。

苗字の読みは VOICEVOX の辞書任せ。誤読は READINGS に「苗字: カタカナの読み」で足して作り直す。
--report を付けると、苗字ごとに VOICEVOX が選んだ読み（audio_query の kana）を TSV に書き出す＝誤読の確認用。

使い方: VOICEVOX（エンジン 127.0.0.1:50021）を起動して
  python3 tools/voicevox-announcements.py [--out 出力先] [--report 読み.tsv] [--force] [--only 苗字,...]
既にある部品は作り直さない（--force で全部作り直す）。標準ライブラリだけで動く。
"""
import argparse
import array
import io
import json
import os
import re
import sys
import urllib.parse
import urllib.request
import wave

API = "http://127.0.0.1:50021"
SPEAKER = 30  # No.7 アナウンス
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_OUT = os.path.join(ROOT, "Assets/KokoSim/Audio/Vendor/VoiceVox/Resources/Voice/No7/Announce")
NAMES_YAML = os.path.join(ROOT, "data/player-names.yaml")

# 固定の部品（部品名, 文面）。部品名は engine の PaAnnouncement と一致させる。
# 4番は「よばん」（場内放送の言い方。VOICEVOX は「よんばん」と読む）
FIXED = [("order_%d" % n, "よばん、" if n == 4 else "%d番、" % n) for n in range(1, 10)] + [
    ("pos_p", "ピッチャー、"),
    ("pos_c", "キャッチャー、"),
    ("pos_1b", "ファースト、"),
    ("pos_2b", "セカンド、"),
    ("pos_3b", "サード、"),
    ("pos_ss", "ショート、"),
    ("pos_lf", "レフト、"),
    ("pos_cf", "センター、"),
    ("pos_rf", "ライト、"),
    ("pos_dh", "しめいだしゃ、"),
    ("batter", "バッター、"),
]

# 苗字の読みの上書き（VOICEVOX が誤読するもの）。値はカタカナ。
# 2026-11-14 に全苗字の読み（--report）を見て、明らかな誤読だけ直した。ほかの怪しい読みは人の耳で確かめる。
READINGS = {
    "比嘉": "ヒガ", "茂木": "モテギ", "須賀": "スガ", "古屋": "フルヤ", "粕谷": "カスヤ",
    "富樫": "トガシ", "末吉": "スエヨシ", "吉松": "ヨシマツ", "和久": "ワク", "中間": "ナカマ",
    "瀬川": "セガワ", "米原": "ヨネハラ", "内野": "ウチノ", "大川内": "オオカワウチ", "庄子": "ショウジ",
    "土本": "ツチモト", "中窪": "ナカクボ", "松林": "マツバヤシ", "山腰": "ヤマコシ", "角": "スミ",
    "菅": "スガ", "野上": "ノガミ", "尾上": "オノウエ", "田上": "タガミ", "長谷": "ハセ",
    "萩原": "ハギワラ", "土田": "ツチダ",
}


def fnv1a32(s):
    h = 2166136261
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * 16777619) & 0xFFFFFFFF
    return h


def surname_clip(surname):
    return "sei_%08x" % fnv1a32(surname)


def surnames():
    """data/player-names.yaml の family_names（出現順・重複なし）。"""
    out, seen, inside = [], set(), False
    with open(NAMES_YAML, encoding="utf-8") as f:
        for line in f:
            if re.match(r"^family_names\s*:", line):
                inside = True
                continue
            if inside and re.match(r"^\S", line) and not line.startswith("#"):
                break
            m = re.search(r"value:\s*([^,}\s]+)", line) if inside else None
            if m and m.group(1) not in seen:
                seen.add(m.group(1))
                out.append(m.group(1))
    return out


def post(path, params, body=None):
    url = API + path + "?" + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else b""
    req = urllib.request.Request(url, data=data, method="POST",
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return r.read()


def query(text):
    q = json.loads(post("/audio_query", {"text": text, "speaker": SPEAKER}))
    q["speedScale"] = 0.95       # 場内放送らしく少しゆっくり
    q["intonationScale"] = 1.0
    q["pitchScale"] = 0.0
    q["volumeScale"] = 1.3
    q["prePhonemeLength"] = 0.05
    q["postPhonemeLength"] = 0.1
    return q


def trim(samples, rate):
    """前後の無音を刈る（部品のつなぎ目の間はゲーム側で決める）。頭は5ms、尻は40ms残す。"""
    peak = max((abs(s) for s in samples), default=0) or 1
    th = peak * 0.02
    start = next((i for i, s in enumerate(samples) if abs(s) > th), 0)
    end = len(samples) - next((i for i, s in enumerate(reversed(samples)) if abs(s) > th), 0)
    start = max(0, start - int(rate * 0.005))
    end = min(len(samples), end + int(rate * 0.04))
    out = samples[start:end]
    fade = min(len(out), int(rate * 0.01))
    for i in range(fade):  # 尻を10msで絞る（プツ音防止）
        out[len(out) - 1 - i] = int(out[len(out) - 1 - i] * i / fade)
    return out


def synth(text, path):
    q = query(text)
    raw = post("/synthesis", {"speaker": SPEAKER}, q)
    with wave.open(io.BytesIO(raw)) as w:
        rate, ch = w.getframerate(), w.getnchannels()
        samples = array.array("h", w.readframes(w.getnframes()))
    if ch == 2:
        samples = array.array("h", ((samples[i] + samples[i + 1]) // 2 for i in range(0, len(samples), 2)))
    samples = trim(samples, rate)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(samples.tobytes())
    return q.get("kana", "")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--report", help="苗字ごとの読み（VOICEVOX の kana）を書き出す TSV")
    ap.add_argument("--force", action="store_true", help="既にある部品も作り直す")
    ap.add_argument("--only", help="この苗字だけ作る（カンマ区切り）")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    jobs = []
    if not a.only:
        jobs += [(name, text, None) for name, text in FIXED]
    names = a.only.split(",") if a.only else surnames()
    for s in names:
        reading = READINGS.get(s)
        jobs.append((surname_clip(s), (reading or s) + "くん。", s))

    report = []
    made = 0
    for name, text, surname in jobs:
        path = os.path.join(a.out, name + ".wav")
        if os.path.exists(path) and not a.force and not a.report:
            continue
        if os.path.exists(path) and not a.force:
            kana = query(text).get("kana", "")   # 読みだけ取り直す
        else:
            kana = synth(text, path)
            made += 1
        if surname is not None:
            report.append((surname, name, kana))
    if a.report:
        with open(a.report, "w", encoding="utf-8") as f:
            f.write("surname\tclip\tkana\n")
            for row in report:
                f.write("\t".join(row) + "\n")
    print("生成 %d 本 / 部品 %d 本 → %s" % (made, len(jobs), a.out))


if __name__ == "__main__":
    sys.exit(main())
