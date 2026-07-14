#!/usr/bin/env python3
"""Dependency-free fallback feed updater for AI Frontier.

It consumes official RSS/Atom/API endpoints, merges them with curated entries,
deduplicates by URL and normalized title, and writes the shared news schema.
An optional OpenAI-compatible endpoint can enrich the analysis when
AI_API_KEY, AI_API_BASE and AI_MODEL are configured.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import html
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
from difflib import SequenceMatcher
from pathlib import Path
from typing import Callable


USER_AGENT = "AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)"
WRITER_BATCH_SIZE = 2
REVIEW_BATCH_SIZE = 4
SCHEMA_VERSION = 2
PIPELINE_CONTRACT_VERSION = "2.2"
CONTENT_TYPES = {"??", "????", "????", "Agent??", "????"}
TOPIC_ORDER = ("???", "Agent", "????", "????", "????")
BATCH_SIZE = 10
MINIMUM_EDITION_ITEMS = 20
CORE_DIMENSIONS = ("???", "Agent", "??", "????")
CORE_DIMENSION_MINIMUMS = {"???": 2, "Agent": 2, "??": 1, "????": 1}
OPEN_SOURCE_LICENSES = {
    "Apache-2.0", "MIT", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MPL-2.0",
    "GPL-2.0", "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0", "GPL-3.0-only",
    "GPL-3.0-or-later", "LGPL-2.1", "LGPL-2.1-only", "LGPL-2.1-or-later",
    "LGPL-3.0", "LGPL-3.0-only", "LGPL-3.0-or-later", "AGPL-3.0",
    "AGPL-3.0-only", "AGPL-3.0-or-later", "EPL-2.0", "Unlicense",
}

# These phrases describe the editorial machinery rather than the event. They must
# never leak into reader-facing copy.
PROCESS_PHRASES = (
    "??????", "????", "????", "??????", "????", "???????",
    "????", "???", "????", "???", "????", "?????", "????",
    "????", "??????????", "??????", "????",
)
LOW_VALUE_TERMS = (
    "funding round", "raises $", "valuation", "celebrity", "singer", "influencer",
    "??", "??", "??", "??", "??", "??", "??", "??", "??",
    "successful people", "return to tech", "book excerpt", "??", "??", "???",
)


def fetch(url: str, timeout: int = 20) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def child_text(element: ET.Element, *names: str) -> str:
    for child in element:
        if local_name(child.tag) in names and child.text:
            return child.text.strip()
    return ""


def clean_text(value: str) -> str:
    value = re.sub(r"<[^>]+>", " ", value or "")
    return re.sub(r"\s+", " ", html.unescape(value)).strip()


def clean_document_text(value: str) -> str:
    """Keep source order and paragraph boundaries while removing page chrome markup."""
    value = re.sub(r"<(script|style|noscript|svg)\b[^>]*>.*?</\1>", " ", value, flags=re.IGNORECASE | re.DOTALL)
    article = re.search(r"<article\b[^>]*>(.*?)</article>", value, flags=re.IGNORECASE | re.DOTALL)
    if article:
        value = article.group(1)
    value = re.sub(r"<(?:p|div|section|h[1-6]|li|blockquote|br)\b[^>]*>", "\n", value, flags=re.IGNORECASE)
    value = re.sub(r"<[^>]+>", " ", value)
    value = html.unescape(value)
    value = re.sub(r"!\[[^\]]*\]\([^)]*\)", " ", value)
    value = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", value)
    value = re.sub(r"```.*?```", " ", value, flags=re.DOTALL)
    lines = [re.sub(r"\s+", " ", line).strip(" #>*-\t") for line in value.splitlines()]
    return "\n".join(line for line in lines if len(line) >= 20)


def fetch_source_material(item: dict) -> str:
    fallback = str(item.get("sourceMaterial") or item.get("summary") or "")
    url = item.get("sourceUrl", "")
    if not url.startswith("http"):
        return fallback
    parsed = urllib.parse.urlsplit(url)
    parts = [part for part in parsed.path.split("/") if part]
    is_github_release = parsed.netloc.lower() == "github.com" and "releases" in parts
    candidates = [] if is_github_release else [url]
    if parsed.netloc.lower() == "github.com" and len(parts) >= 2:
        branch = item.get("defaultBranch", "main")
        readme_url = f"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/{branch}/README.md"
        if is_github_release:
            try:
                decoded = fetch(readme_url, timeout=12).decode("utf-8", errors="ignore")
                readme = clean_document_text(decoded)
                if readme:
                    return (fallback[:3500] + "\n\n?????README??\n" + readme[:6500])[:12000]
            except Exception:  # noqa: BLE001
                return fallback[:12000]
        else:
            candidates.insert(0, readme_url)
    for candidate in candidates:
        try:
            payload = fetch(candidate, timeout=12)
            if payload.startswith(b"%PDF"):
                continue
            decoded = payload.decode("utf-8", errors="ignore")
            cleaned = clean_document_text(decoded)
            if len(cleaned) >= max(500, len(fallback)):
                return cleaned[:12000]
        except Exception:  # noqa: BLE001
            continue
    return fallback[:12000]


def attach_source_material(items: list[dict]) -> None:
    """Fetch selected source pages concurrently; summaries remain the offline fallback."""
    with ThreadPoolExecutor(max_workers=6) as executor:
        pending = {executor.submit(fetch_source_material, item): item for item in items}
        for future in as_completed(pending):
            item = pending[future]
            try:
                item["sourceMaterial"] = future.result()
            except Exception:  # noqa: BLE001
                item["sourceMaterial"] = item.get("sourceMaterial") or item.get("summary", "")


def parse_date(value: str) -> dt.datetime:
    value = value.strip().replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(value)
    except ValueError:
        from email.utils import parsedate_to_datetime

        try:
            parsed = parsedate_to_datetime(value)
        except (TypeError, ValueError):
            # Invalid or missing publication times are old, never silently "now".
            parsed = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed


def infer_topics(
    title: str,
    description: str,
    content_type: str,
    candidates: list[str] | None = None,
) -> list[str]:
    """Classify subject matter without changing the kind of source material."""
    text = f"{title} {description}".lower()
    groups = {
        "Agent": ("agent", "agentic", "multi-agent", "tool use", "computer use", "???", "????", "???"),
        "???": ("large language model", "foundation model", " llm", "gpt-", "gemini", "claude", "qwen", "deepseek", "???", "????", "?????", "????"),
        "????": ("paper", "research", "benchmark", "dataset", "arxiv", "??", "??", "??", "???", "??"),
        "????": ("open source", "github", "repository", "sdk", "framework", "??", "??", "??"),
        "????": ("launch", "release", "api", "product", "platform", "??", "??", "??", "??"),
    }
    intrinsic = {
        "??": "????",
        "????": "????",
        "????": "???",
        "Agent??": "Agent",
        "????": "????",
    }.get(content_type, "????")
    topics = [intrinsic]
    topics.extend(topic for topic, terms in groups.items() if any(term in text for term in terms))
    # Configured topics are discovery hints only. They neither add a topic without
    # evidence nor suppress an explicitly detected cross-topic.
    _ = candidates
    return sorted(dict.fromkeys(topics), key=lambda topic: TOPIC_ORDER.index(topic))[:4]


def compatibility_category(content_type: str, topics: list[str]) -> str:
    """Keep schema-v1 clients usable while schema-v2 readers use both dimensions."""
    if content_type == "??":
        return "????"
    if content_type == "????":
        return "????"
    if content_type == "????":
        return "???"
    if content_type == "Agent??":
        return "Agent"
    return next((topic for topic in topics if topic in TOPIC_ORDER), "????")


def fallback_analysis(summary: str) -> dict:
    """Internal-only placeholder; entries are never published until AI copy passes review."""
    return {
        "keyFacts": [summary],
        "context": "",
        "beginnerExplainer": "",
        "impact": "",
        "limitations": "",
        "whatToWatch": "",
        "briefSections": [],
        "fullBrief": "",
        "technicalRelevanceScore": 0.0,
        "innovationScore": 0.0,
    }


def parse_feed(source: dict) -> list[dict]:
    try:
        root = ET.fromstring(fetch(source["url"]))
    except Exception as exc:  # noqa: BLE001
        print(f"warning: {source['name']}: {exc}", file=sys.stderr)
        return []

    atom = local_name(root.tag) == "feed"
    nodes = [node for node in root.iter() if local_name(node.tag) == ("entry" if atom else "item")]
    items: list[dict] = []
    for node in nodes[:24]:
        title = html.unescape(child_text(node, "title"))
        link = child_text(node, "link")
        if atom:
            link_node = next((child for child in node if local_name(child.tag) == "link" and child.attrib.get("href")), None)
            link = link_node.attrib["href"] if link_node is not None else link
        published = child_text(node, "published", "updated", "pubDate", "date")
        description = clean_text(child_text(node, "summary", "description", "content"))
        if not title or not link.startswith("http"):
            continue
        date = parse_date(published)
        summary = description[:260].rstrip() + ("?" if len(description) > 260 else "")
        if not summary:
            summary = "????????????????????????????"
        content_type = source.get("contentType", "????")
        if content_type not in CONTENT_TYPES:
            continue
        topics = infer_topics(title, description, content_type, source.get("topics", []))
        category = compatibility_category(content_type, topics)
        analysis = fallback_analysis(summary)
        item = {
            "id": "feed-" + hashlib.sha256(link.encode()).hexdigest()[:16],
            "category": category,
            "contentType": content_type,
            "contentTypeLocked": bool(source.get("contentTypeLocked", False)),
            "topics": topics,
            "brand": source["brand"],
            "brandColor": source["brandColor"],
            "logoAsset": source.get("logoAsset", ""),
            "title": title.strip(),
            "summary": summary,
            "sourceMaterial": description[:2200],
            "publishedAt": date.astimezone().date().isoformat(),
            "sourceName": source["name"],
            "sourceUrl": link,
            "readMinutes": max(2, min(8, len(description) // 350 + 2)),
            "confidence": source.get("trust", "????"),
            "whyItMatters": "",
            "details": [summary],
            "tags": [*topics, content_type, source["brand"], "????"],
            "sourceTrail": [f"{source['name']}: {link}"],
            **analysis,
        }
        items.append(item)
    return items


def collect_github(config: dict) -> list[dict]:
    discovery = config.get("githubDiscovery", {})
    if not discovery.get("enabled", True):
        return []
    since = (dt.date.today() - dt.timedelta(days=int(config.get("supplementDays", 14)))).isoformat()
    query_specs = discovery.get("queries") or [{
        "query": discovery.get("query", "topic:artificial-intelligence stars:>500"),
        "topics": ["????"],
        "minimumStars": discovery.get("minimumStars", 500),
        "maxItems": discovery.get("maxItems", 4),
    }]
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    github_token = os.getenv("GITHUB_TOKEN", "").strip()
    if github_token:
        headers["Authorization"] = f"Bearer {github_token}"

    def collect_spec(spec: dict) -> list[dict]:
        output: list[dict] = []
        query = f"{spec['query']} pushed:>={since}"
        url = "https://api.github.com/search/repositories?" + urllib.parse.urlencode({
            "q": query,
            "sort": "stars",
            "order": "desc",
            "per_page": min(10, int(spec.get("maxItems", 4))),
        })
        request = urllib.request.Request(url, headers=headers)
        try:
            payload = json.loads(urllib.request.urlopen(request, timeout=10).read().decode("utf-8"))
        except Exception as exc:  # noqa: BLE001
            print(f"warning: GitHub project discovery ({spec.get('topics')}): {exc}", file=sys.stderr)
            return []
        minimum_stars = int(spec.get("minimumStars", 100))
        accepted = 0
        for repository in payload.get("items", []):
            if accepted >= int(spec.get("maxItems", 4)):
                break
            stars = int(repository.get("stargazers_count", 0))
            if stars < minimum_stars:
                continue
            license_name = (repository.get("license") or {}).get("spdx_id") or ""
            if license_name not in OPEN_SOURCE_LICENSES:
                continue
            full_name = repository.get("full_name", "")
            if not full_name:
                continue
            release_url = f"https://api.github.com/repos/{full_name}/releases/latest"
            try:
                release = json.loads(urllib.request.urlopen(
                    urllib.request.Request(release_url, headers=headers), timeout=6
                ).read().decode("utf-8"))
            except Exception:  # noqa: BLE001 - repositories without a release are intentionally skipped
                continue
            released_at = release.get("published_at") or release.get("created_at") or ""
            if not released_at or released_at[:10] < since:
                continue
            link = release.get("html_url") or repository.get("html_url", "")
            description = clean_text(repository.get("description") or "")
            release_notes = clean_text(release.get("body") or "")
            if len(release_notes) < 80:
                continue
            release_name = clean_text(release.get("name") or release.get("tag_name") or "???")
            title = f"{full_name} {release_name}"
            topics = infer_topics(
                title,
                f"{description} {release_notes}",
                "????",
                spec.get("topics", ["????"]),
            )
            category = compatibility_category("????", topics)
            language = repository.get("language") or "???"
            summary = f"{description} {release_notes[:500]}"
            analysis = fallback_analysis(summary)
            analysis["keyFacts"] = [
                f"???{release.get('tag_name', release_name)}??????{released_at[:10]}?",
                f"??? {stars:,} ? Star??????{language}?????{license_name}?",
                release_notes,
            ]
            output.append({
                "id": "github-" + hashlib.sha256(link.encode()).hexdigest()[:16],
                "category": category,
                "contentType": "????",
                "contentTypeLocked": True,
                "topics": topics,
                "brand": "GitHub",
                "brandColor": "#24292F",
                "logoAsset": "Assets/Brands/github.svg",
                "title": title,
                "summary": summary,
                "sourceMaterial": (
                    f"???{full_name}\n???{release_name}\n?????{released_at}\n"
                    f"?????{description}\nRelease notes?{release_notes}\n"
                    f"Star?{stars:,}??????{language}?????{license_name}?"
                ),
                "defaultBranch": repository.get("default_branch") or "main",
                "publishedAt": released_at[:10],
                "sourceName": "GitHub Release",
                "sourceUrl": link,
                "readMinutes": 4,
                "confidence": "????",
                "whyItMatters": "",
                "details": analysis["keyFacts"],
                "tags": [*topics, "????", "GitHub", "????", "????"],
                "sourceTrail": [f"GitHub Release: {link}"],
                **analysis,
            })
            accepted += 1
        return output

    output: list[dict] = []
    with ThreadPoolExecutor(max_workers=min(6, len(query_specs))) as executor:
        futures = [executor.submit(collect_spec, spec) for spec in query_specs]
        for future in as_completed(futures):
            output.extend(future.result())
    deduplicated = {item["sourceUrl"]: item for item in output}
    return list(deduplicated.values())


def normalize_title(title: str) -> str:
    return re.sub(r"[^a-z0-9\u4e00-\u9fff]+", "", title.lower())


def chinese_char_count(value: object) -> int:
    return len(re.findall(r"[\u4e00-\u9fff]", str(value or "")))


def contains_english_sentence(value: object) -> bool:
    text = str(value or "")
    return bool(re.search(r"\b(?:[A-Za-z][A-Za-z0-9+.#/'-]*\s+){7,}[A-Za-z][A-Za-z0-9+.#/'-]*[.!?]?", text))


def reader_copy(item: dict) -> list[str]:
    values = [item.get("title", ""), item.get("summary", "")]
    values.extend(str(value) for value in item.get("keyFacts", []))
    for key in ("context", "beginnerExplainer", "impact", "limitations", "whatToWatch"):
        values.append(str(item.get(key, "")))
    for section in item.get("briefSections", []):
        values.extend((str(section.get("title", "")), str(section.get("body", ""))))
    return values


def source_entity_anchors(title: object) -> list[str]:
    text = str(title or "")
    repository_names = re.findall(r"\b[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\b", text)
    known_names = (
        "Codex", "ChatGPT", "Claude", "Gemini", "Qwen", "DeepSeek", "Transformer",
        "SymCrypt", "Rust", "Lean", "NVIDIA", "Apple", "Google", "Microsoft", "OpenAI",
        "Spotify", "Uber", "Siri", "Copilot", "GPT", "cBottle", "JAX", "Whisper",
    )
    return list(dict.fromkeys([
        *repository_names,
        *(name for name in known_names if re.search(rf"\b{re.escape(name)}\b", text, re.IGNORECASE)),
    ]))


def source_entity_consistent(item: dict) -> bool:
    anchors = source_entity_anchors(item.get("_sourceTitle") or item.get("sourceTitle"))
    if not anchors:
        return True
    reader_text = f"{item.get('title', '')} {item.get('summary', '')}".lower()
    return any(anchor.lower() in reader_text for anchor in anchors)


def chinese_report_issues(item: dict) -> list[str]:
    sections = item.get("briefSections", [])
    copy = reader_copy(item)
    issues: list[str] = []
    if item.get("contentType") not in CONTENT_TYPES:
        issues.append("contentType??")
    if not isinstance(item.get("topics"), list) or not item.get("topics"):
        issues.append("topics??")
    if chinese_char_count(item.get("title")) < 2:
        issues.append("??????")
    if chinese_char_count(str(item.get("title", ""))[:16]) < 4:
        issues.append("??????????")
    if chinese_char_count(item.get("summary")) < 50:
        issues.append("??????50?")
    if not 3 <= len(sections) <= 5:
        issues.append("????3?5?")
    if sum(chinese_char_count(section.get("body")) for section in sections) < 275:
        issues.append("??????275?")
    if not sections or chinese_char_count(sections[0].get("body")) < 60:
        issues.append("??????60?")
    if sections and not all(chinese_char_count(section.get("body")) >= 45 for section in sections):
        issues.append("????45??????")
    if any(contains_english_sentence(value) for value in copy):
        issues.append("?????????????")
    matched_process_phrases = sorted({phrase for value in copy for phrase in PROCESS_PHRASES if phrase in value})
    if matched_process_phrases:
        issues.append("??????????:" + "?".join(matched_process_phrases))
    if not source_entity_consistent(item):
        issues.append("????????????????")
    return issues


def is_chinese_report(item: dict) -> bool:
    return not chinese_report_issues(item)


def is_low_priority(item: dict) -> bool:
    text = f"{item.get('title', '')} {item.get('summary', '')} {item.get('sourceMaterial', '')}".lower()
    return any(term in text for term in LOW_VALUE_TERMS)


def event_key(item: dict) -> str:
    existing = str(item.get("eventKey") or "")
    if existing and not existing.startswith("title:"):
        return existing
    text = f"{item.get('sourceUrl', '')} {item.get('title', '')} {item.get('sourceMaterial', '')}"
    patterns = (
        (r"(?:doi\.org/|doi:\s*)(10\.\d{4,9}/[-._;()/:a-z0-9]+)", "doi"),
        (r"arxiv\.org/(?:abs|pdf)/(\d{4}\.\d{4,5})(?:v\d+)?", "arxiv"),
        (r"github\.com/([^/\s]+/[^/\s#?]+)(?:/releases/tag/([^/\s#?]+))?", "github"),
    )
    for pattern, kind in patterns:
        match = re.search(pattern, text, flags=re.IGNORECASE)
        if match:
            parts = [part.strip("./").lower() for part in match.groups() if part]
            return f"{kind}:" + ":".join(parts)
    return ""


def title_tokens(title: str) -> set[str]:
    normalized = re.sub(r"[^a-z0-9\u4e00-\u9fff]+", " ", title.lower())
    words = {word for word in normalized.split() if len(word) >= 3}
    chinese = "".join(re.findall(r"[\u4e00-\u9fff]", normalized))
    words.update(chinese[index:index + 2] for index in range(max(0, len(chinese) - 1)))
    return words


def titles_refer_to_same_event(left: dict, right: dict) -> bool:
    left_event_key = event_key(left)
    right_event_key = event_key(right)
    if left_event_key and right_event_key:
        return left_event_key == right_event_key
    left_title = normalize_title(left.get("title", ""))
    right_title = normalize_title(right.get("title", ""))
    if not left_title or not right_title:
        return False
    sequence = SequenceMatcher(None, left_title, right_title).ratio()
    left_tokens = title_tokens(left.get("title", ""))
    right_tokens = title_tokens(right.get("title", ""))
    union = left_tokens | right_tokens
    jaccard = len(left_tokens & right_tokens) / len(union) if union else 0.0
    same_type = left.get("contentType") == right.get("contentType")
    topic_overlap = bool(set(left.get("topics", [])) & set(right.get("topics", [])))
    return (same_type or topic_overlap) and (sequence >= 0.76 or (sequence >= 0.56 and jaccard >= 0.48))


def deduplicate_events(items: list[dict]) -> list[dict]:
    selected: list[dict] = []
    for item in sorted(items, key=lambda row: row.get("publishedAt", ""), reverse=True):
        duplicate = next((kept for kept in selected if titles_refer_to_same_event(item, kept)), None)
        if duplicate:
            duplicate["sourceTrail"] = list(dict.fromkeys([
                *duplicate.get("sourceTrail", []), *item.get("sourceTrail", [])
            ]))
            continue
        item["eventKey"] = event_key(item) or "title:" + normalize_title(item.get("title", ""))[:80]
        selected.append(item)
    return selected


def diversify(collected: list[dict], maximum: int, per_source: int) -> list[dict]:
    buckets: dict[str, list[dict]] = {}
    for item in collected:
        buckets.setdefault(item.get("sourceName", "????"), []).append(item)
    queues = [
        sorted(items, key=lambda item: item.get("publishedAt", ""), reverse=True)[:per_source]
        for items in buckets.values()
    ]
    selected: list[dict] = []
    while len(selected) < maximum and any(queues):
        for queue in queues:
            if queue and len(selected) < maximum:
                selected.append(queue.pop(0))
    return selected


def matches_coverage_dimension(item: dict, dimension: str) -> bool:
    if dimension in CONTENT_TYPES:
        return item.get("contentType") == dimension
    return (
        item.get("category") == dimension
        or dimension in item.get("topics", [])
    )


def balance_categories(items: list[dict], maximum: int, minimums: dict[str, int]) -> list[dict]:
    ordered = sorted(items, key=lambda item: item.get("publishedAt", ""), reverse=True)
    remaining = list(ordered)
    selected: list[dict] = []
    for category, count in minimums.items():
        matches = [item for item in remaining if matches_coverage_dimension(item, category)][:int(count)]
        selected.extend(matches)
        remaining = [item for item in remaining if item not in matches]
    selected.extend(remaining[:max(0, maximum - len(selected))])
    return selected[:maximum]


def batch_has_core_coverage(batch: list[dict]) -> bool:
    return len(batch) == BATCH_SIZE and all(
        sum(matches_coverage_dimension(item, dimension) for item in batch) >= minimum
        for dimension, minimum in CORE_DIMENSION_MINIMUMS.items()
    )


def compose_fixed_batches(items: list[dict], maximum: int) -> list[dict]:
    """Build complete ten-item reading pages while reserving coverage for later pages."""
    target = min(maximum, len(items)) // BATCH_SIZE * BATCH_SIZE
    if target < MINIMUM_EDITION_ITEMS:
        return []
    batch_count = target // BATCH_SIZE
    remaining = list(items)
    output: list[dict] = []
    for batch_index in range(batch_count):
        batch: list[dict] = []
        for dimension, minimum in CORE_DIMENSION_MINIMUMS.items():
            while sum(matches_coverage_dimension(item, dimension) for item in batch) < minimum:
                match = next((item for item in remaining if matches_coverage_dimension(item, dimension)), None)
                if match is None:
                    return []
                batch.append(match)
                remaining.remove(match)

        future_batches = batch_count - batch_index - 1
        while len(batch) < BATCH_SIZE:
            candidate = next((item for item in remaining if all(
                sum(matches_coverage_dimension(other, dimension) for other in remaining if other is not item)
                >= future_batches * CORE_DIMENSION_MINIMUMS[dimension]
                for dimension in CORE_DIMENSIONS
                if matches_coverage_dimension(item, dimension)
            )), None)
            if candidate is None:
                return []
            batch.append(candidate)
            remaining.remove(candidate)
        if not batch_has_core_coverage(batch):
            return []
        output.extend(batch)
    return output


def merge(existing: list[dict], collected: list[dict], maximum: int, per_source: int, minimums: dict[str, int]) -> list[dict]:
    output: list[dict] = []
    urls: set[str] = set()
    titles: set[str] = set()
    curated = [item for item in existing if "????" not in item.get("tags", [])]
    candidates = [*curated, *diversify(collected, maximum, per_source)]
    for item in candidates:
        url = item.get("sourceUrl", "")
        title = normalize_title(item.get("title", ""))
        if (url and url in urls) or not title or title in titles:
            continue
        urls.add(url)
        titles.add(title)
        output.append(item)
    return balance_categories(deduplicate_events(output), maximum, minimums)


def apply_freshness(
    items: list[dict], fresh_hours: int, supplement_days: int, target: int, minimums: dict[str, int]
) -> list[dict]:
    today = dt.date.today()
    fresh_cutoff = today - dt.timedelta(days=max(1, fresh_hours // 24))
    supplement_cutoff = today - dt.timedelta(days=max(1, supplement_days))

    def published_date(item: dict) -> dt.date:
        try:
            return dt.date.fromisoformat(item.get("publishedAt", ""))
        except ValueError:
            return dt.date(1970, 1, 1)

    fresh = [item for item in items if published_date(item) >= fresh_cutoff]
    supplements = [item for item in items if supplement_cutoff <= published_date(item) < fresh_cutoff]
    selected = list(fresh)
    for category, minimum in minimums.items():
        missing = max(0, int(minimum) - sum(matches_coverage_dimension(item, category) for item in selected))
        additions = [item for item in supplements if matches_coverage_dimension(item, category) and item not in selected][:missing]
        for item in additions:
            item["tags"] = [tag for tag in item.get("tags", []) if tag != "????"] + ["????"]
        selected.extend(additions)
    if len(selected) < target:
        for item in supplements:
            if item in selected:
                continue
            item["tags"] = [tag for tag in item.get("tags", []) if tag != "????"] + ["????"]
            selected.append(item)
            if len(selected) >= target:
                break
    return selected


def enrich_with_ai(
    items: list[dict],
    checkpoint: Callable[[list[dict], int, int], None] | None = None,
) -> tuple[list[dict], int]:
    """Optionally enrich entries through any OpenAI-compatible chat endpoint."""
    api_key = os.getenv("AI_API_KEY", "").strip()
    api_base = os.getenv("AI_API_BASE", "").strip().rstrip("/")
    model = os.getenv("AI_MODEL", "").strip()
    if not (api_key and api_base and model):
        return items, 0

    endpoint = api_base if api_base.endswith("/chat/completions") else f"{api_base}/chat/completions"
    allowed = {"title", "summary", "contentType", "briefSections", "keyFacts", "context", "beginnerExplainer", "impact", "limitations", "whatToWatch", "technicalRelevanceScore", "innovationScore", "topics"}
    enriched_count = 0
    for start in range(0, len(items), WRITER_BATCH_SIZE):
        batch = items[start:start + WRITER_BATCH_SIZE]
        material = [
            {
                "id": item["id"],
                "category": item["category"],
                "contentType": item["contentType"],
                "contentTypeLocked": item.get("contentTypeLocked", False),
                "topics": item["topics"],
                "sourceTitle": item.get("_sourceTitle") or item["title"],
                "title": item["title"],
                "source": item["sourceName"],
                "publishedAt": item.get("publishedAt", ""),
                "sourceMaterial": str(item.get("sourceMaterial") or {
                    "summary": item.get("summary", ""),
                    "keyFacts": item.get("keyFacts", []),
                    "context": item.get("context", ""),
                })[:6000],
            }
            for item in batch
        ]
        prompt = (
            "??????????? AI ?????????????????????????????????????????????????????"
            "??????????contentType??????????????Agent?????????topics?????Agent????????????????Agent??????Agent???contentTypeLocked?true?????????false?????????????????????"
            "sourceTitle???????????????????????????????????????????Codex???ChatGPT??????????????????????"
            "???????????????????????????????title?summary??????????????Star?????????"
            "summary?70-140?????????????????????????????"
            "briefSections???JSON??????????3-5????500-900????????275?????????????????60???????????45?????????????????????????????????????????????????????????????????????????"
            "??????????????????????????????????????????????????Agent????????????????????????????????????????????????????????????????"
            "???????????????????????????????????????????????????????????????????????????????"
            "?????keyFacts(3-5?)?context?beginnerExplainer?impact?limitations?whatToWatch???????????????????????????????"
            "???topics?contentTypeLocked?true?????contentType??false?????????????technicalRelevanceScore?innovationScore?0?1?????????????????????????????????????0.45?"
            "???? JSON ??????????????????? \"briefSections\":[{\"title\":\"?????\",\"body\":\"????\"}]???????????????? {\"items\":[{\"id\":..., ...}]}?\n\n"
            + json.dumps(material, ensure_ascii=False)
        )
        payload = json.dumps({
            "model": model,
            "temperature": 0.2,
            "max_tokens": 8000,
            "response_format": {"type": "json_object"},
            "messages": [
                {"role": "system", "content": "?????????????? JSON?"},
                {"role": "user", "content": prompt},
            ],
        }, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            endpoint,
            data=payload,
            headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json", "User-Agent": USER_AGENT},
            method="POST",
        )
        try:
            response = None
            for attempt in range(4):
                try:
                    response = json.loads(urllib.request.urlopen(request, timeout=90).read().decode("utf-8"))
                    break
                except urllib.error.HTTPError as exc:
                    if exc.code != 429 or attempt == 3:
                        raise
                    delay = 8 * (attempt + 1)
                    print(f"warning: AI rate limited; retrying in {delay}s", file=sys.stderr)
                    time.sleep(delay)
                except (urllib.error.URLError, TimeoutError) as exc:
                    if attempt == 3:
                        raise
                    delay = 4 * (attempt + 1)
                    print(f"warning: AI writer connection failed ({exc}); retrying in {delay}s", file=sys.stderr)
                    time.sleep(delay)
            if response is None:
                raise RuntimeError("AI enrichment returned no response")
            content = response["choices"][0]["message"]["content"].strip()
            content = re.sub(r"^```(?:json)?\s*|\s*```$", "", content, flags=re.IGNORECASE)
            enriched = json.loads(content).get("items", [])
            by_id = {row.get("id"): row for row in enriched if isinstance(row, dict)}
            for item in batch:
                changed = False
                writer_row = by_id.get(item["id"], {})
                if "briefSections" not in writer_row:
                    print(
                        f"writer omitted briefSections {item['id']}: keys={sorted(writer_row.keys())}",
                        file=sys.stderr,
                    )
                for key, value in writer_row.items():
                    if key == "briefSections":
                        valid_sections = []
                        if isinstance(value, list):
                            for section in value:
                                if not isinstance(section, dict):
                                    continue
                                title = section.get("title") or section.get("????") or section.get("??")
                                body = section.get("body") or section.get("??") or section.get("??")
                                if title and body:
                                    valid_sections.append({"title": str(title), "body": str(body)})
                        total_chinese = sum(chinese_char_count(section["body"]) for section in valid_sections)
                        if 3 <= len(valid_sections) <= 5 and total_chinese >= 275 and all(chinese_char_count(section["body"]) >= 45 for section in valid_sections):
                            item[key] = valid_sections
                            changed = True
                        else:
                            print(
                                f"writer incomplete {item['id']}: sections={len(valid_sections)} "
                                f"chinese={total_chinese} raw={str(value)[:500]}",
                                file=sys.stderr,
                            )
                    elif key == "contentType":
                        if not item.get("contentTypeLocked", False) and value in CONTENT_TYPES:
                            item[key] = value
                            changed = True
                    elif key == "topics" and isinstance(value, list):
                        topics = [topic for topic in value if topic in TOPIC_ORDER]
                        if topics:
                            item[key] = list(dict.fromkeys(topics))[:4]
                            changed = True
                    elif key in {"technicalRelevanceScore", "innovationScore"} and isinstance(value, (int, float)):
                        item[key] = max(0.0, min(1.0, float(value)))
                        changed = True
                    elif key in allowed and value:
                        minimum_chinese = 2 if key == "title" else 45 if key == "summary" else 0
                        if not isinstance(value, str) or chinese_char_count(value) >= minimum_chinese:
                            item[key] = value
                            changed = True
                if changed:
                    enriched_count += 1
                item["category"] = compatibility_category(item["contentType"], item.get("topics", []))
                item["whyItMatters"] = item.get("impact", item["whyItMatters"])
                item["details"] = item.get("keyFacts", item["details"])
                item["fullBrief"] = "\n\n".join(
                    f"{section['title']}\n{section['body']}" for section in item.get("briefSections", [])
                )
                item["tags"] = [tag for tag in item["tags"] if tag != "AI ??"] + ["AI ??"]
        except Exception as exc:  # noqa: BLE001
            print(f"warning: optional AI enrichment failed: {exc}", file=sys.stderr)
        finally:
            if checkpoint is not None:
                checkpoint(items, enriched_count, min(start + len(batch), len(items)))
    return items, enriched_count


def review_with_ai(items: list[dict]) -> tuple[list[dict], int]:
    """Run a separate, fail-closed editorial review over the finished Chinese copy."""
    api_key = os.getenv("AI_API_KEY", "").strip()
    api_base = os.getenv("AI_API_BASE", "").strip().rstrip("/")
    model = os.getenv("AI_REVIEW_MODEL", "").strip() or os.getenv("AI_MODEL", "").strip()
    if not (api_key and api_base and model):
        return [], 0
    endpoint = api_base if api_base.endswith("/chat/completions") else f"{api_base}/chat/completions"
    passed: list[dict] = []
    reviewed = 0
    for start in range(0, len(items), REVIEW_BATCH_SIZE):
        batch = items[start:start + REVIEW_BATCH_SIZE]
        material = [{
            "id": item["id"],
            "sourceTitle": item.get("_sourceTitle") or item.get("title"),
            "sourceMaterial": str(item.get("sourceMaterial", ""))[:6000],
            "draft": {
                "title": item.get("title"),
                "summary": item.get("summary"),
                "contentType": item.get("contentType"),
                "contentTypeLocked": item.get("contentTypeLocked", False),
                "topics": item.get("topics"),
                "briefSections": item.get("briefSections"),
                "keyFacts": item.get("keyFacts"),
                "context": item.get("context"),
                "beginnerExplainer": item.get("beginnerExplainer"),
                "impact": item.get("impact"),
                "limitations": item.get("limitations"),
                "whatToWatch": item.get("whatToWatch"),
                "technicalRelevanceScore": item.get("technicalRelevanceScore"),
                "innovationScore": item.get("innovationScore"),
            },
        } for item in batch]
        prompt = (
            "?????????AI????????????sourceMaterial?draft?????????????????pass?"
            "(1)???????????????????????(2)??????????????"
            "(3)???????????????????????????(4)contentType????????contentTypeLocked?true????????topics???"
            "sourceTitle????????????????????????????????????GitHub?????????????????????"
            "(5)?????3-5????????????????(6)??????????????????????????"
            "(7)???????????????????????????????"
            "??????technicalRelevanceScore?innovationScore?0?1??"
            "???JSON?{\"items\":[{\"id\":\"...\",\"pass\":true,\"technicalRelevanceScore\":0.8,\"innovationScore\":0.7,\"issues\":[]}]}?\n\n"
            + json.dumps(material, ensure_ascii=False)
        )
        payload = json.dumps({
            "model": model,
            "temperature": 0,
            "max_tokens": 3000,
            "response_format": {"type": "json_object"},
            "messages": [
                {"role": "system", "content": "?????????????????????????JSON?"},
                {"role": "user", "content": prompt},
            ],
        }, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            endpoint,
            data=payload,
            headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json", "User-Agent": USER_AGENT},
            method="POST",
        )
        try:
            response = None
            for attempt in range(5):
                try:
                    response = json.loads(urllib.request.urlopen(request, timeout=90).read().decode("utf-8"))
                    break
                except urllib.error.HTTPError as exc:
                    if exc.code != 429 or attempt == 4:
                        raise
                    delay = (15, 30, 60, 90)[attempt]
                    print(f"warning: AI reviewer rate limited; retrying in {delay}s", file=sys.stderr)
                    time.sleep(delay)
                except (urllib.error.URLError, TimeoutError) as exc:
                    if attempt == 4:
                        raise
                    delay = 4 * (attempt + 1)
                    print(f"warning: AI reviewer connection failed ({exc}); retrying in {delay}s", file=sys.stderr)
                    time.sleep(delay)
            if response is None:
                raise RuntimeError("AI review returned no response")
            content = response["choices"][0]["message"]["content"].strip()
            content = re.sub(r"^```(?:json)?\s*|\s*```$", "", content, flags=re.IGNORECASE)
            verdicts = json.loads(content).get("items", [])
            by_id = {row.get("id"): row for row in verdicts if isinstance(row, dict)}
            for item in batch:
                verdict = by_id.get(item["id"], {})
                if not isinstance(verdict.get("pass"), bool):
                    continue
                reviewed += 1
                if not verdict["pass"]:
                    print(
                        f"review rejected {item['id']}: {verdict.get('issues', [])}",
                        file=sys.stderr,
                    )
                    continue
                reviewer_technical = verdict.get("technicalRelevanceScore")
                reviewer_innovation = verdict.get("innovationScore")
                if not isinstance(reviewer_technical, (int, float)) or not isinstance(reviewer_innovation, (int, float)):
                    continue
                # A generous writer score cannot override a skeptical reviewer.
                item["technicalRelevanceScore"] = min(float(item.get("technicalRelevanceScore", 0)), float(reviewer_technical))
                item["innovationScore"] = min(float(item.get("innovationScore", 0)), float(reviewer_innovation))
                report_issues = chinese_report_issues(item)
                if not report_issues:
                    passed.append(item)
                else:
                    print(
                        f"local gate rejected {item['id']}: {report_issues}",
                        file=sys.stderr,
                    )
        except Exception as exc:  # noqa: BLE001
            print(f"warning: independent AI review failed closed: {exc}", file=sys.stderr)
    return passed, reviewed


def atomic_write_json(path: Path, document: dict) -> None:
    """Write a complete JSON document without exposing a partially written file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def configuration_fingerprint(config_path: Path) -> str:
    payload = config_path.read_bytes() + PIPELINE_CONTRACT_VERSION.encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def load_draft_cache(path: Path, expected_fingerprint: str, maximum_age_hours: int = 24) -> dict:
    if not path.exists():
        raise ValueError(f"draft cache not found: {path}")
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("pipelineContractVersion") != PIPELINE_CONTRACT_VERSION:
        raise ValueError("draft cache pipeline contract is stale")
    if document.get("configurationFingerprint") != expected_fingerprint:
        raise ValueError("draft cache configuration does not match the current feed config")
    created_value = document.get("createdAt")
    if not isinstance(created_value, str) or not created_value.strip():
        raise ValueError("draft cache has no creation timestamp")
    created_at = parse_date(created_value)
    age = dt.datetime.now(dt.timezone.utc) - created_at.astimezone(dt.timezone.utc)
    if age > dt.timedelta(hours=maximum_age_hours) or age < -dt.timedelta(minutes=5):
        raise ValueError(f"draft cache age is outside the allowed window: {age}")
    items = document.get("items")
    if not isinstance(items, list) or not items:
        raise ValueError("draft cache contains no items")
    return document


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="Data/source-feeds.json")
    parser.add_argument("--output", default="Data/news.json")
    parser.add_argument("--drop-existing", action="store_true", help="discard previously curated entries")
    parser.add_argument("--max-candidates", type=int, default=0, help="limit candidates for local editorial testing")
    parser.add_argument("--reuse-draft", action="store_true", help="reuse the last enriched draft and rerun review only")
    parser.add_argument("--repair-draft", action="store_true", help="rewrite only incomplete cached drafts, then review")
    parser.add_argument("--draft-cache", default="", help="path for the enriched local draft cache")
    parser.add_argument("--fail-on-preserve", action="store_true", help="return non-zero when last-known-good is preserved")
    args = parser.parse_args()
    if args.reuse_draft and args.repair_draft:
        parser.error("--reuse-draft and --repair-draft are mutually exclusive")

    config_path = Path(args.config)
    config = json.loads(config_path.read_text(encoding="utf-8"))
    config_fingerprint = configuration_fingerprint(config_path)
    output_path = Path(args.output)
    draft_path = Path(args.draft_cache) if args.draft_cache else output_path.with_suffix(".draft.json")
    current = json.loads(output_path.read_text(encoding="utf-8")) if output_path.exists() else {"items": []}
    minimums = {key: int(value) for key, value in config.get("categoryMinimums", {}).items()}
    candidate_limit = args.max_candidates if args.max_candidates > 0 else int(config.get("maxItems", 18))
    selection_minimums = {key: min(value, 2) for key, value in minimums.items()} if args.max_candidates > 0 else minimums
    ai_requested = all(os.getenv(name, "").strip() for name in ("AI_API_KEY", "AI_API_BASE", "AI_MODEL"))
    if args.reuse_draft or args.repair_draft:
        try:
            draft = load_draft_cache(draft_path, config_fingerprint)
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            print(f"error: cannot reuse draft cache: {exc}", file=sys.stderr)
            return 1
        items = draft["items"]
        enriched_count = int(draft.get("enrichedCount", len(items)))
        candidate_limit = int(draft.get("candidateLimit", candidate_limit))
        print(f"reusing {len(items)} enriched items from {draft_path}", file=sys.stderr)
        if args.repair_draft:
            repair_targets = [
                item for item in items
                if chinese_report_issues(item) or not all(
                    isinstance(item.get(name), (int, float))
                    for name in ("technicalRelevanceScore", "innovationScore")
                )
            ]
            print(f"repairing {len(repair_targets)} incomplete cached drafts", file=sys.stderr)
            def save_repair_checkpoint(_targets: list[dict], repaired: int, processed: int) -> None:
                draft["createdAt"] = dt.datetime.now().astimezone().isoformat(timespec="seconds")
                draft["repairProcessedCount"] = processed
                draft["repairEnrichedCount"] = repaired
                draft["items"] = items
                atomic_write_json(draft_path, draft)

            _, repaired_count = enrich_with_ai(repair_targets, save_repair_checkpoint)
            enriched_count += repaired_count
            draft["createdAt"] = dt.datetime.now().astimezone().isoformat(timespec="seconds")
            draft["enrichedCount"] = enriched_count
            draft["items"] = items
            atomic_write_json(draft_path, draft)
    else:
        collected: list[dict] = []
        with ThreadPoolExecutor(max_workers=min(8, len(config["feeds"]) + 1)) as executor:
            futures = [executor.submit(parse_feed, source) for source in config["feeds"]]
            futures.append(executor.submit(collect_github, config))
            for future in as_completed(futures):
                collected.extend(future.result())
        collected = [item for item in collected if not is_low_priority(item)]
        collected = deduplicate_events(collected)
        collected = apply_freshness(
            collected,
            int(config.get("freshHours", 72)),
            int(config.get("supplementDays", 7)),
            int(config.get("maxItems", 18)),
            minimums,
        )
        existing = [] if args.drop_existing or int(current.get("schemaVersion", 1)) < SCHEMA_VERSION else current.get("items", [])
        items = merge(
            existing,
            collected,
            candidate_limit,
            int(config.get("maxItemsPerSource", 4)),
            selection_minimums,
        )
        for item in items:
            item["_sourceTitle"] = item.get("_sourceTitle") or item.get("title", "")
        attach_source_material(items)
        draft_document = {
            "schemaVersion": SCHEMA_VERSION,
            "pipelineContractVersion": PIPELINE_CONTRACT_VERSION,
            "configurationFingerprint": config_fingerprint,
            "createdAt": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
            "candidateLimit": candidate_limit,
            "enrichedCount": 0,
            "writerProcessedCount": 0,
            "items": items,
        }

        def save_writer_checkpoint(current_items: list[dict], enriched: int, processed: int) -> None:
            draft_document["createdAt"] = dt.datetime.now().astimezone().isoformat(timespec="seconds")
            draft_document["enrichedCount"] = enriched
            draft_document["writerProcessedCount"] = processed
            draft_document["items"] = current_items
            atomic_write_json(draft_path, draft_document)

        items, enriched_count = enrich_with_ai(items, save_writer_checkpoint if ai_requested else None)
        if ai_requested:
            draft_document["enrichedCount"] = enriched_count
            draft_document["writerProcessedCount"] = len(items)
            draft_document["items"] = items
            atomic_write_json(draft_path, draft_document)
            print(f"saved enriched draft to {draft_path}", file=sys.stderr)
    if ai_requested:
        submitted_count = len(items)
        writer_ready: list[dict] = []
        for item in items:
            issues = chinese_report_issues(item)
            scores_valid = all(
                isinstance(item.get(name), (int, float))
                for name in ("technicalRelevanceScore", "innovationScore")
            )
            if not issues and scores_valid:
                writer_ready.append(item)
            else:
                if not scores_valid:
                    issues.append("??????")
                print(f"writer gate rejected {item.get('id')}: {issues}", file=sys.stderr)
        items = writer_ready
        writer_ready_count = len(items)
        cooldown = max(0, int(os.getenv("AI_REVIEW_COOLDOWN_SECONDS", "30")))
        if items and cooldown:
            print(f"waiting {cooldown}s before independent review", file=sys.stderr)
            time.sleep(cooldown)
        items, reviewed_count = review_with_ai(items)
        items = [item for item in deduplicate_events(items) if (
            is_chinese_report(item)
            and not is_low_priority(item)
            and isinstance(item.get("technicalRelevanceScore"), (int, float))
            and isinstance(item.get("innovationScore"), (int, float))
            and float(item["technicalRelevanceScore"]) >= 0.55
            and float(item["innovationScore"]) >= 0.35
        )]
        items = balance_categories(items, candidate_limit, selection_minimums)
        items = compose_fixed_batches(items, candidate_limit)
        core_coverage_ok = bool(items) and all(
            batch_has_core_coverage(items[start:start + BATCH_SIZE])
            for start in range(0, len(items), BATCH_SIZE)
        )
        coverage_counts = {
            "???": sum("???" in item.get("topics", []) for item in items),
            "Agent": sum("Agent" in item.get("topics", []) for item in items),
            "??": sum(item.get("contentType") == "??" for item in items),
            "????": sum(item.get("contentType") == "????" for item in items),
        }
        print(
            f"editorial audit: submitted={submitted_count} enriched={enriched_count} writer_ready={writer_ready_count} "
            f"reviewed={reviewed_count} passed={len(items)} coverage={coverage_counts}",
            file=sys.stderr,
        )
        if len(items) < MINIMUM_EDITION_ITEMS or len(items) % BATCH_SIZE or not core_coverage_ok:
            print("warning: Chinese technical edition did not meet coverage; preserving the previous edition", file=sys.stderr)
            return 1 if args.fail_on_preserve or not current.get("items") else 0
    else:
        print("warning: no AI editorial service; preserving the previous Chinese edition", file=sys.stderr)
        return 1 if args.fail_on_preserve or not current.get("items") else 0
    for item in items:
        item.pop("_sourceTitle", None)
        item.pop("sourceMaterial", None)
        item.pop("defaultBranch", None)
        item.pop("contentTypeLocked", None)
    edition = {
        "schemaVersion": SCHEMA_VERSION,
        "editionDate": dt.date.today().isoformat(),
        "windowHours": 72,
        "generatedAt": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "items": items,
    }
    atomic_write_json(output_path, edition)
    print(f"wrote {len(items)} items to {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
