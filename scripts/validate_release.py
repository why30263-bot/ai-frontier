#!/usr/bin/env python3
"""Fail closed when an AI Frontier release is not production-ready."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


SEMVER_PATTERN = re.compile(r"^\d+\.\d+\.\d+$")
HAN_PATTERN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
CONTENT_TYPES = {"??", "????", "????", "Agent??", "????"}
ALLOWED_TOPICS = {"???", "Agent", "????", "????", "????"}
ENGLISH_SENTENCE_PATTERN = re.compile(
    r"\b(?:[A-Za-z][A-Za-z0-9+.#/'-]*\s+){7,}[A-Za-z][A-Za-z0-9+.#/'-]*[.!?]?"
)
PROCESS_PHRASES = (
    "??????", "????", "????", "??????", "????", "???????",
    "????", "???", "????", "???", "????", "?????", "????",
    "????", "??????????", "??????", "????",
)


def chinese_character_count(value: Any) -> int:
    return len(HAN_PATTERN.findall(value if isinstance(value, str) else ""))


def load_json(path: Path, errors: list[str]) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append(f"???????? {path}: {exc}")
        return {}
    if not isinstance(value, dict):
        errors.append("?????????? JSON ??")
        return {}
    return value


def project_version(path: Path, errors: list[str]) -> str:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        errors.append(f"???????? {path}: {exc}")
        return ""
    versions = [
        (element.text or "").strip()
        for element in root.iter()
        if element.tag.rsplit("}", 1)[-1] == "Version" and (element.text or "").strip()
    ]
    if len(set(versions)) != 1:
        errors.append(f"AIFrontier.csproj ???????????? Version???? {versions or '?'}")
        return ""
    return versions[0]


def validate_item(item: Any, index: int, errors: list[str]) -> None:
    label = f"items[{index}]"
    if not isinstance(item, dict):
        errors.append(f"{label} ?????")
        return

    item_id = item.get("id")
    if not isinstance(item_id, str) or not item_id.strip():
        errors.append(f"{label}.id ????")

    content_type = item.get("contentType")
    if content_type not in CONTENT_TYPES:
        errors.append(f"{label}.contentType ??: {content_type!r}")

    topics = item.get("topics")
    if not isinstance(topics, list) or not topics or not all(isinstance(topic, str) and topic.strip() for topic in topics):
        errors.append(f"{label}.topics ??????????")
    elif any(topic not in ALLOWED_TOPICS for topic in topics):
        errors.append(f"{label}.topics ???????: {topics!r}")

    title = item.get("title")
    summary = item.get("summary")
    if not isinstance(title, str) or chinese_character_count(title) < 2:
        errors.append(f"{label}.title ???? 2 ?????")
    elif chinese_character_count(title[:16]) < 4:
        errors.append(f"{label}.title ?????????")
    if not isinstance(summary, str) or chinese_character_count(summary) < 50:
        errors.append(f"{label}.summary ???? 50 ?????")

    source_url = item.get("sourceUrl")
    if not isinstance(source_url, str) or not source_url.startswith(("https://", "http://")):
        errors.append(f"{label}.sourceUrl ??? HTTP(S) ??")
    if not isinstance(item.get("publishedAt"), str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", item["publishedAt"]):
        errors.append(f"{label}.publishedAt ??? YYYY-MM-DD")
    for score_name, minimum in (("technicalRelevanceScore", 0.55), ("innovationScore", 0.35)):
        score = item.get(score_name)
        if not isinstance(score, (int, float)) or isinstance(score, bool) or not minimum <= float(score) <= 1:
            errors.append(f"{label}.{score_name} ??? {minimum} ? 1 ??")

    sections = item.get("briefSections")
    if not isinstance(sections, list) or not 3 <= len(sections) <= 5:
        length = len(sections) if isinstance(sections, list) else "???"
        errors.append(f"{label}.briefSections ??? 3-5 ????? {length}")
        return

    total_chinese = 0
    reader_text = [str(title or ""), str(summary or "")]
    for section_index, section in enumerate(sections):
        section_label = f"{label}.briefSections[{section_index}]"
        if not isinstance(section, dict):
            errors.append(f"{section_label} ?????")
            continue
        title = section.get("title")
        body = section.get("body")
        if not isinstance(title, str) or chinese_character_count(title) < 2:
            errors.append(f"{section_label}.title ???? 2 ?????")
        if not isinstance(body, str) or not body.strip():
            errors.append(f"{section_label}.body ????")
            continue
        total_chinese += chinese_character_count(body)
        reader_text.extend((str(title or ""), body))
        if chinese_character_count(body) < 45:
            errors.append(f"{section_label}.body ???? 45 ?????")

    if sections and isinstance(sections[0], dict) and chinese_character_count(sections[0].get("body")) < 60:
        errors.append(f"{label}.briefSections[0].body ???? 60 ?????")

    if total_chinese < 275:
        errors.append(f"{label} ?????? 275 ????????? {total_chinese}")

    for field in ("keyFacts", "context", "beginnerExplainer", "impact", "limitations", "whatToWatch"):
        value = item.get(field, [])
        if isinstance(value, list):
            reader_text.extend(str(part) for part in value)
        else:
            reader_text.append(str(value or ""))
    if any(ENGLISH_SENTENCE_PATTERN.search(text) for text in reader_text):
        errors.append(f"{label} ??????????????")
    leaked = sorted({phrase for text in reader_text for phrase in PROCESS_PHRASES if phrase in text})
    if leaked:
        errors.append(f"{label} ?????????????: {'?'.join(leaked)}")


def validate_content(document: dict[str, Any], errors: list[str], maximum_age_hours: int | None) -> None:
    if document.get("schemaVersion") != 2:
        errors.append(f"schemaVersion ?????? 2???? {document.get('schemaVersion')!r}")
        return

    items = document.get("items")
    if not isinstance(items, list):
        errors.append("items ?????")
        return
    if len(items) < 20:
        errors.append(f"?????? 20 ??????? {len(items)}")
    if len(items) % 10:
        errors.append(f"??????? 10 ???????? {len(items)}")

    generated_at = document.get("generatedAt")
    try:
        generated = dt.datetime.fromisoformat(str(generated_at).replace("Z", "+00:00"))
        if generated.tzinfo is None:
            raise ValueError("missing timezone")
        now = dt.datetime.now(dt.timezone.utc)
        if generated.astimezone(dt.timezone.utc) > now + dt.timedelta(minutes=5):
            errors.append("generatedAt ?????????? 5 ??")
        if maximum_age_hours is not None and now - generated.astimezone(dt.timezone.utc) > dt.timedelta(hours=maximum_age_hours):
            errors.append(f"???????? {maximum_age_hours} ??")
    except (TypeError, ValueError):
        errors.append("generatedAt ??????? ISO 8601 ??")

    edition_date = document.get("editionDate")
    try:
        dt.date.fromisoformat(str(edition_date))
    except ValueError:
        errors.append("editionDate ??? YYYY-MM-DD")
    window_hours = document.get("windowHours")
    if not isinstance(window_hours, int) or not 24 <= window_hours <= 336:
        errors.append("windowHours ??? 24 ? 336 ??")

    for index, item in enumerate(items):
        validate_item(item, index, errors)

    ids = [item.get("id") for item in items if isinstance(item, dict) and item.get("id")]
    if len(ids) != len(set(ids)):
        errors.append("items ????? id")
    source_urls = [item.get("sourceUrl") for item in items if isinstance(item, dict) and item.get("sourceUrl")]
    if len(source_urls) != len(set(source_urls)):
        errors.append("items ????? sourceUrl")

    for start in range(0, len(items), 10):
        batch = items[start:start + 10]
        if len(batch) != 10:
            continue
        coverage = {
            "???": sum(isinstance(item, dict) and "???" in item.get("topics", []) for item in batch) >= 2,
            "Agent": sum(isinstance(item, dict) and "Agent" in item.get("topics", []) for item in batch) >= 2,
            "??": any(isinstance(item, dict) and item.get("contentType") == "??" for item in batch),
            "????": any(isinstance(item, dict) and item.get("contentType") == "????" for item in batch),
        }
        for category, present in coverage.items():
            if not present:
                errors.append(f"? {start // 10 + 1} ???{category}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate release content and version gates.")
    parser.add_argument("--news", type=Path, default=Path("Data/news.json"))
    parser.add_argument("--project", type=Path, default=Path("AIFrontier.csproj"))
    parser.add_argument("--version", default="", help="Expected MAJOR.MINOR.PATCH version; defaults to project Version")
    parser.add_argument("--max-age-hours", type=int, default=None, help="Reject editions older than this many hours")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    errors: list[str] = []

    declared_version = project_version(args.project, errors)
    expected_version = args.version.strip() or declared_version
    if not SEMVER_PATTERN.fullmatch(expected_version):
        errors.append(f"??????? MAJOR.MINOR.PATCH???? {expected_version!r}")
    if declared_version and expected_version != declared_version:
        errors.append(
            f"??????????? {expected_version}?AIFrontier.csproj ?? {declared_version}"
        )

    document = load_json(args.news, errors)
    if document:
        validate_content(document, errors, args.max_age_hours)

    if errors:
        print("Release validation FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(f"Release validation passed: version={expected_version}, items={len(document['items'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
