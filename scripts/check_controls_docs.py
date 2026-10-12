#!/usr/bin/env python3
"""docs/controls/*.yml(컨트롤 기술서) 형식 검사.

검사: YAML로 읽힘 · 필수 항목 · id = 파일 이름 · kind 값 · 항목 순서(index.yml의 schema.fields 순)
· source·tests 경로가 있음 · used_by·related id가 있음 · index.yml 목록과 파일이 같음
· 목록 항목(features·keyboard·theme)의 키 · 글이 아닌 값(NULL·yes가 null·bool로 읽힘)
· 줄 끝 공백·탭·마지막 줄바꿈.
쓰는 법: python3 scripts/check_controls_docs.py   (문제가 있으면 목록을 찍고 1로 끝남)
"""
import pathlib
import sys

try:
    import yaml
except ImportError:
    sys.exit("PyYAML이 필요합니다: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs" / "controls"
KINDS = {"control", "composite", "dialog", "window", "menu", "settings-page", "helper"}
LIST_KEYS = {"features": ("name", "description"), "keyboard": ("keys", "action"), "theme": ("part", "keys")}


def scalars(value, where):
    """글(str)이 아닌 값: YAML이 NULL·yes·on·숫자를 null·bool·int로 읽어 버린 곳."""
    if isinstance(value, dict):
        for k, v in value.items():
            yield from scalars(v, f"{where}.{k}")
    elif isinstance(value, list):
        for i, v in enumerate(value):
            yield from scalars(v, f"{where}[{i}]")
    elif not isinstance(value, str):
        yield where, value


def main():
    problems = []
    index = yaml.safe_load((DOCS / "index.yml").read_text(encoding="utf-8"))
    schema = index["schema"]
    order = list(schema["fields"])
    required = schema["required"]
    listed = {c["id"]: c for c in index["controls"]}
    files = {p.stem: p for p in DOCS.glob("*.yml") if p.name != "index.yml"}

    for missing in sorted(set(listed) - set(files)):
        problems.append(f"index.yml: '{missing}' 파일이 없음")
    for extra in sorted(set(files) - set(listed)):
        problems.append(f"index.yml: '{extra}.yml'이 목록에 없음")

    for path in sorted(DOCS.glob("*.yml")):
        text = path.read_text(encoding="utf-8")
        name = path.name
        if "\t" in text:
            problems.append(f"{name}: 탭 문자")
        if not text.endswith("\n"):
            problems.append(f"{name}: 마지막 줄바꿈 없음")
        for no, line in enumerate(text.split("\n"), 1):
            if line != line.rstrip():
                problems.append(f"{name}:{no}: 줄 끝 공백")
        if name == "index.yml":
            continue
        doc = yaml.safe_load(text)
        if not isinstance(doc, dict):
            problems.append(f"{name}: 최상위가 맵이 아님")
            continue
        for key in required:
            if not doc.get(key):
                problems.append(f"{name}: 필수 항목 '{key}' 없음")
        unknown = [k for k in doc if k not in order]
        if unknown:
            problems.append(f"{name}: 스키마에 없는 항목 {unknown}")
        known = [k for k in doc if k in order]
        if known != sorted(known, key=order.index):
            problems.append(f"{name}: 항목 순서가 schema.fields와 다름 ({known})")
        if doc.get("id") != path.stem:
            problems.append(f"{name}: id '{doc.get('id')}'가 파일 이름과 다름")
        if doc.get("kind") not in KINDS:
            problems.append(f"{name}: kind '{doc.get('kind')}'")
        entry = listed.get(path.stem)
        if entry and (entry.get("name") != doc.get("name") or entry.get("kind") != doc.get("kind") or entry.get("title") != doc.get("title")):
            problems.append(f"{name}: index.yml의 name·kind·title과 다름")
        for key in ("source", "tests"):
            for rel in doc.get(key) or []:
                if not (ROOT / rel).exists():
                    problems.append(f"{name}: {key} 경로 없음 {rel}")
        for key in ("used_by", "related"):
            for ref in doc.get(key) or []:
                if ref not in listed:
                    problems.append(f"{name}: {key}의 '{ref}'가 index.yml에 없음")
        for where, value in scalars(doc, name):
            problems.append(f"{where}: 글이 아닌 값 {value!r} — NULL·yes·숫자 같은 글은 따옴표로 감싸세요")
        for key, fields in LIST_KEYS.items():
            for i, item in enumerate(doc.get(key) or []):
                if not isinstance(item, dict) or any(not item.get(f) for f in fields):
                    problems.append(f"{name}: {key}[{i}]에 {fields} 필요")

    for p in problems:
        print(p)
    print(f"컨트롤 기술서 {len(files)}개 검사: 문제 {len(problems)}건")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
