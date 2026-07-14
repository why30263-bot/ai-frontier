#!/usr/bin/env python3
"""Assemble locally edited reports into one atomic, fixed-batch edition.

This is the deterministic fallback when the remote editorial model is slow or
unavailable. It reuses the same gates and batching contract as update_feed.py.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path

import update_feed


def load_items(paths: list[Path]) -> list[dict]:
    items: list[dict] = []
    for path in paths:
        document = json.loads(path.read_text(encoding="utf-8"))
        rows = document.get("items") if isinstance(document, dict) else document
        if not isinstance(rows, list):
            raise ValueError(f"{path} must contain a JSON array or an items array")
        items.extend(row for row in rows if isinstance(row, dict))
    return items


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, default=Path("Data/news.json"))
    parser.add_argument("--maximum", type=int, default=40)
    parser.add_argument("--exclude-id", action="append", default=[])
    args = parser.parse_args()

    excluded = set(args.exclude_id)
    candidates = update_feed.deduplicate_events(
        [item for item in load_items(args.inputs) if item.get("id") not in excluded]
    )
    valid: list[dict] = []
    for item in candidates:
        issues = update_feed.chinese_report_issues(item)
        technical = item.get("technicalRelevanceScore")
        innovation = item.get("innovationScore")
        if not isinstance(technical, (int, float)) or float(technical) < 0.55:
            issues.append("technicalRelevanceScore below 0.55")
        if not isinstance(innovation, (int, float)) or float(innovation) < 0.35:
            issues.append("innovationScore below 0.35")
        if update_feed.is_low_priority(item):
            issues.append("low-priority topic")
        if issues:
            print(f"rejected {item.get('id')}: {issues}")
            continue
        valid.append(item)

    batches = update_feed.compose_fixed_batches(valid, args.maximum)
    if len(batches) < update_feed.MINIMUM_EDITION_ITEMS:
        print(f"cannot publish: {len(valid)} valid reports cannot form two complete batches")
        return 1

    for item in batches:
        for internal in ("_sourceTitle", "sourceMaterial", "defaultBranch", "contentTypeLocked"):
            item.pop(internal, None)
        item["fullBrief"] = "\n\n".join(
            f"{section['title']}\n{section['body']}" for section in item.get("briefSections", [])
        )

    edition = {
        "schemaVersion": update_feed.SCHEMA_VERSION,
        "editionDate": dt.date.today().isoformat(),
        "windowHours": 72,
        "generatedAt": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "items": batches,
    }
    update_feed.atomic_write_json(args.output, edition)
    print(f"wrote {len(batches)} items to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
