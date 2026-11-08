#!/usr/bin/env python3
"""審判コール（アウト／セーフ）の音声を VOICEVOX で生成する。

声: VOICEVOX:No.7（ノーマル）。言い回しは 2026-11-08 に監督が聴いて選んだもの:
  アウト = 「アウトォ！」をそのまま（抑揚 1.6）
  セーフ = 「セーフ！」のアクセントを最後の音へ移す（語尾で下がらない言い方）

生成物は Git 管理外（Assets/KokoSim/Audio/Vendor/）。No.7 は「VOICEVOX:No.7」のクレジット表記で
非商用利用可・商用は No.7 製作委員会へ事前確認（https://voiceseven.com/）。

使い方: VOICEVOX（エンジン localhost:50021）を起動して
  python3 tools/voicevox-umpire-calls.py [出力先]
標準ライブラリだけで動く。
"""
import array
import io
import json
import os
import sys
import urllib.parse
import urllib.request
import wave

API = "http://localhost:50021"
SPEAKER = 29  # No.7 ノーマル
DEFAULT_OUT = "Assets/KokoSim/Audio/Vendor/VoiceVox/Resources/Voice/No7"

# (ファイル名, 文面, アクセントを最後の音へ移すか)
CALLS = [
    ("umpire_out_01", "アウトォ！", False),
    ("umpire_safe_01", "セーフ！", True),
]


def post(path, params, body=None):
    url = API + path + "?" + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else b""
    req = urllib.request.Request(url, data=data, method="POST",
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return r.read()


def build_query(text, accent_last):
    q = json.loads(post("/audio_query", {"text": text, "speaker": SPEAKER}))
    q["speedScale"] = 1.0
    q["intonationScale"] = 1.6
    q["pitchScale"] = 0.0
    q["volumeScale"] = 1.3
    q["postPhonemeLength"] = 0.6
    if accent_last:
        ap = q["accent_phrases"]
        ap[0]["accent"] = len(ap[0]["moras"])
        q["accent_phrases"] = json.loads(post("/mora_pitch", {"speaker": SPEAKER}, ap))
    return q


def trim(samples, rate):
    """前後の無音を刈る（声の頭を判定の瞬間に合わせるため）。頭は3ms、尻は50ms残す。"""
    peak = max(abs(s) for s in samples) or 1
    th = peak * 0.02
    start = next(i for i, s in enumerate(samples) if abs(s) > th)
    end = len(samples) - next(i for i, s in enumerate(reversed(samples)) if abs(s) > th)
    start = max(0, start - int(rate * 0.003))
    end = min(len(samples), end + int(rate * 0.05))
    out = samples[start:end]
    fade = min(len(out), int(rate * 0.01))
    for i in range(fade):  # 尻を10msで絞る（プツ音防止）
        out[len(out) - 1 - i] = int(out[len(out) - 1 - i] * i / fade)
    return out


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    os.makedirs(out_dir, exist_ok=True)
    for name, text, accent_last in CALLS:
        raw = post("/synthesis", {"speaker": SPEAKER}, build_query(text, accent_last))
        with wave.open(io.BytesIO(raw)) as w:
            rate, ch = w.getframerate(), w.getnchannels()
            samples = array.array("h", w.readframes(w.getnframes()))
        if ch == 2:
            samples = array.array("h", ((samples[i] + samples[i + 1]) // 2 for i in range(0, len(samples), 2)))
        samples = trim(samples, rate)
        path = os.path.join(out_dir, name + ".wav")
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(samples.tobytes())
        print(f"{path}  {len(samples) / rate:.2f}s")


if __name__ == "__main__":
    main()
