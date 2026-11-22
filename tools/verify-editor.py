#!/usr/bin/env python3
"""検証用 Editor（KoKoSimNext-verify）を再コンパイルし、使える状態に戻るまで待つ。

夜間エージェントの実機確認が「ブリッジ不応答」で飛んでいた原因（2026-11-22 のサーバログで実測）:
  `refresh_unity` は Editor が戻るまで 60 秒しか待たないが、verify Editor はドメインリロード後に
  UnityMCP のプラグインが張り直されるまで 1分半〜2分かかる（12回中11回が 1:37〜2:15・1回は約17分）。
  エージェントは 60 秒のタイムアウトを「不応答」と受け取り、待たずに目視確認を飛ばしていた。

このスクリプトは前面で（ターンを跨がずに）最大 --timeout 秒まで待つ。判定は名前を信用せず
`Application.dataPath` が verify のパスであることまで見る（名前ずれ事故・memory unity-mcp）。

使い方:
  python3 tools/verify-editor.py wait              # 使える状態になるまで待つ（既定 300 秒）
  python3 tools/verify-editor.py refresh           # アセット更新＋再コンパイルを頼み、戻るまで待つ
  python3 tools/verify-editor.py refresh --timeout 600
終了コード: 0＝使える（最後の行に unity_instance に渡す ID を出す）／1＝時間切れ／2＝引数の誤り。
成功したら、以後の MCP 呼び出しは必ず `unity_instance` にこの ID を渡すか set_active_instance で選ぶこと。
"""
import argparse
import json
import sys
import time
import urllib.error
import urllib.request

URL = "http://127.0.0.1:8080/mcp"
VERIFY_NAME = "KoKoSimNext-verify"
VERIFY_PATH = "/Users/seiya.oda/Unity/KoKoSimNext-verify/Assets"

PROBE = (
    'return UnityEngine.Application.dataPath + "|" + UnityEditor.EditorApplication.isCompiling'
    ' + "|" + UnityEditor.EditorApplication.isUpdating + "|" + UnityEditor.EditorApplication.isPlaying'
    ' + "|" + UnityEditor.EditorUtility.scriptCompilationFailed;'
)


class Mcp:
    def __init__(self):
        self.sid = None
        self.next_id = 1

    def _post(self, body, timeout):
        h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.sid:
            h["mcp-session-id"] = self.sid
        req = urllib.request.Request(URL, json.dumps(body).encode(), h)
        with urllib.request.urlopen(req, timeout=timeout) as r:
            self.sid = r.headers.get("mcp-session-id") or self.sid
            txt = r.read().decode("utf-8", "replace")
        data = [json.loads(l[6:]) for l in txt.splitlines() if l.startswith("data: ")]
        if data:
            return data[-1]
        return json.loads(txt) if txt.strip() else None

    def init(self):
        self.sid = None
        self._post({"jsonrpc": "2.0", "id": 0, "method": "initialize",
                    "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                               "clientInfo": {"name": "verify-editor", "version": "1"}}}, 15)
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"}, 15)

    def call(self, method, params, timeout=30):
        self.next_id += 1
        r = self._post({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}, timeout)
        if r is None:
            raise RuntimeError("空の応答")
        if "error" in r:
            raise RuntimeError(str(r["error"])[:200])
        return r["result"]

    def tool(self, name, args, timeout=30):
        res = self.call("tools/call", {"name": name, "arguments": args}, timeout)
        text = "".join(c.get("text", "") for c in res.get("content", []))
        try:
            return json.loads(text)
        except ValueError:
            return {"success": not res.get("isError"), "message": text}

    def verify_instance(self):
        res = self.call("resources/read", {"uri": "mcpforunity://instances"})
        info = json.loads(res["contents"][0]["text"])
        for inst in info.get("instances", []):
            if inst.get("name") == VERIFY_NAME:
                return inst["id"]
        return None


def probe(m):
    """(状態の説明, 使えるなら instance id) を返す。"""
    iid = m.verify_instance()
    if not iid:
        return "verify のプラグインが未接続", None
    r = m.tool("execute_code", {"action": "execute", "code": PROBE, "unity_instance": iid}, timeout=30)
    if not r.get("success"):
        return "応答待ち（%s）" % (r.get("error") or r.get("message") or "?")[:120], None
    parts = str((r.get("data") or {}).get("result", "")).split("|")
    if len(parts) < 5:
        return "想定外の応答: %r" % parts, None
    path, compiling, updating, playing, failed = parts[:5]
    if path != VERIFY_PATH:
        # 名前は verify でも中身が別プロジェクト＝絶対に操作しない
        return "名前は %s だが実パスが %s（接続しない）" % (VERIFY_NAME, path), None
    if compiling == "True" or updating == "True":
        return "コンパイル/アセット更新中", None
    if failed == "True":
        return "COMPILE_FAILED", iid
    return ("Play 中" if playing == "True" else "待機中"), iid


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["wait", "refresh"])
    ap.add_argument("--timeout", type=int, default=300)
    a = ap.parse_args()

    m = Mcp()
    deadline = time.time() + a.timeout
    last = None

    if a.cmd == "refresh":
        # 戻りを待たせない（wait_for_ready=False）。待つのは下のループ（60 秒の壁を越える）
        while time.time() < deadline:
            try:
                m.init()
                state, iid = probe(m)
                if iid and state not in ("Play 中",):
                    r = m.tool("refresh_unity", {"mode": "force", "scope": "all", "compile": "request",
                                                 "wait_for_ready": False, "unity_instance": iid}, timeout=60)
                    print("再コンパイルを依頼: %s" % (r.get("message") or r), flush=True)
                    time.sleep(10)  # リロードが始まる前に「待機中」を拾わないように
                    break
                if state == "Play 中":
                    print("Play 中なので再コンパイルを頼めない。先に Play を止めること。", flush=True)
                    return 1
                if state != last:
                    print("…%s" % state, flush=True)
                    last = state
            except (urllib.error.URLError, OSError, RuntimeError, ValueError, KeyError) as e:
                if str(e) != last:
                    print("…MCP サーバに届かない（%s）" % str(e)[:120], flush=True)
                    last = str(e)
            time.sleep(5)
        else:
            print("時間切れ: 再コンパイルを頼めなかった", flush=True)
            return 1

    t0 = time.time()
    while time.time() < deadline:
        try:
            m.init()
            state, iid = probe(m)
            if iid:
                if state == "COMPILE_FAILED":
                    print("コンパイルエラーで止まっている。read_console（unity_instance=%s）でエラーを読むこと。" % iid,
                          flush=True)
                    print(iid)
                    return 1
                print("使える（%s・%d 秒待った）" % (state, time.time() - t0), flush=True)
                print(iid)
                return 0
        except (urllib.error.URLError, OSError, RuntimeError, ValueError, KeyError) as e:
            state = "MCP サーバに届かない（%s）" % str(e)[:120]
        if state != last:
            print("…%s" % state, flush=True)
            last = state
        time.sleep(5)
    print("時間切れ（%d 秒）: 最後の状態 = %s" % (a.timeout, last), flush=True)
    return 1


if __name__ == "__main__":
    sys.exit(main())
