#!/usr/bin/env python3
"""実在の高校名と、校名語彙（data/school-names.yaml）から作れる架空名の完全一致を洗い出す（issue #1470・設計書05 §2.1）。

実在校名の一覧は repo に置かない（権利整理 #1318）。非公開リポジトリ KoKoSimNext-assets 側に
  school-names/real-high-schools.txt   （1行1校。「北海道札幌東高等学校」「県立盛岡工業高校」のような正式名でよい）
を置き、このスクリプトで語彙と照合する。当たった名前だけを data/school-names.yaml の
excluded_public_names / excluded_private_names に足す（repo に載るのは当たった名前だけ）。

  python3 tools/check-real-school-names.py [--list PATH] [--yaml PATH]

出力は YAML に貼れる形。終了コードは、YAML の除外リストに未登録の当たりがあれば 1、無ければ 0。
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_YAML = os.path.join(ROOT, "data", "school-names.yaml")
DEFAULT_LIST = os.path.join(ROOT, "..", "KoKoSimNext-assets", "school-names", "real-high-schools.txt")

PREFECTURE = re.compile(r"^(北海道|東京都|京都府|大阪府|[^\s県]{2,3}県)")
OWNER = re.compile(r"^(県立|都立|道立|府立|市立|区立|町立|村立|組合立|私立)")
TAIL = re.compile(r"(高等学校|高校)$")


def parse_vocab(text):
    lines, buf, depth = [], "", 0
    for raw in text.split("\n"):
        t = raw.strip()
        if depth == 0 and (not t or t.startswith("#")):
            continue
        buf = raw if depth == 0 else buf + " " + t
        depth += raw.count("[") - raw.count("]")
        if depth <= 0:
            lines.append(buf)
            depth = 0
    top, flat, places = None, {}, set()
    for line in lines:
        indent = len(line) - len(line.lstrip(" "))
        key, _, value = line.strip().partition(":")
        key, value = key.strip(), value.strip()
        m = re.search(r"\[(.*)\]", value)
        items = [x.strip() for x in m.group(1).split(",") if x.strip()] if m else []
        if indent == 0:
            top = key
            flat[key] = items
        elif top == "places_by_prefecture":
            places.update(items)
    return flat, places


def candidates(raw):
    s = re.sub(r"\s+", "", raw)
    if not TAIL.search(s):
        return set()
    s = TAIL.sub("", s)
    out = {s}
    bare = PREFECTURE.sub("", s)
    out.add(bare)
    out.add(OWNER.sub("", bare))
    out.add(OWNER.sub("", s))
    return {c for c in out if c}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", default=DEFAULT_LIST)
    ap.add_argument("--yaml", default=DEFAULT_YAML)
    a = ap.parse_args()
    if not os.path.exists(a.list):
        print(f"実在校名の一覧が無い: {a.list}\n非公開リポジトリ KoKoSimNext-assets の school-names/real-high-schools.txt を置くか --list で指す", file=sys.stderr)
        return 2

    flat, places = parse_vocab(open(a.yaml, encoding="utf-8").read())
    places.update(flat.get("place_prefixes", []))
    pub_suffixes = flat.get("public_suffixes", [])
    public = {p + s for p in places for s in pub_suffixes
              if not (p[-1] == s[0] or p.endswith(s))}
    private = {t + s for t in flat.get("private_stems", []) for s in flat.get("private_suffixes", [])}

    real = set()
    for line in open(a.list, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            real |= candidates(line)

    hit_pub, hit_pri = sorted(public & real), sorted(private & real)
    have_pub, have_pri = set(flat.get("excluded_public_names", [])), set(flat.get("excluded_private_names", []))
    new_pub, new_pri = [n for n in hit_pub if n not in have_pub], [n for n in hit_pri if n not in have_pri]

    print(f"公立: 語彙の組み合わせ {len(public)} 通りのうち実在と一致 {len(hit_pub)}（未登録 {len(new_pub)}）")
    print(f"私立: 語彙の組み合わせ {len(private)} 通りのうち実在と一致 {len(hit_pri)}（未登録 {len(new_pri)}）")
    if new_pub:
        print("\n# excluded_public_names に足す:\n" + ", ".join(new_pub))
    if new_pri:
        print("\n# excluded_private_names に足す:\n" + ", ".join(new_pri))
    return 1 if (new_pub or new_pri) else 0


if __name__ == "__main__":
    sys.exit(main())
