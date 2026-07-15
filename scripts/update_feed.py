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
CONTENT_TYPES = {"论文", "开源项目", "模型发布", "Agent产品", "产业事件"}
TOPIC_ORDER = ("大模型", "Agent", "重要研究", "开源项目", "产业动态")
BATCH_SIZE = 10
MINIMUM_EDITION_ITEMS = 20
CORE_DIMENSIONS = ("大模型", "Agent", "论文", "开源项目")
CORE_DIMENSION_MINIMUMS = {"大模型": 2, "Agent": 2, "论文": 1, "开源项目": 1}
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
    "原始信息来自", "当前来源", "来源摘要", "建议打开原文", "回到原文", "以官方文档为准",
    "自动采集", "采集器", "筛选逻辑", "置信度", "人工核查", "跨来源验证", "报道边界",
    "无法替代", "未披露的内容不作补写", "需要结合原文", "需要核对",
)
LOW_VALUE_TERMS = (
    "funding round", "raises $", "valuation", "celebrity", "singer", "influencer",
    "融资", "估值", "明星", "歌手", "网红", "吐槽", "绯闻", "粉丝", "饭圈",
    "successful people", "return to tech", "book excerpt", "木偶", "性感", "不性感",
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
                    return (fallback[:3500] + "\n\n项目说明（README）：\n" + readme[:6500])[:12000]
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
        "Agent": ("agent", "agentic", "multi-agent", "tool use", "computer use", "智能体", "代理系统", "工作流"),
        "大模型": ("large language model", "foundation model", " llm", "gpt-", "gemini", "claude", "qwen", "deepseek", "大模型", "基础模型", "多模态模型", "推理模型"),
        "重要研究": ("paper", "research", "benchmark", "dataset", "arxiv", "论文", "研究", "基准", "数据集", "实验"),
        "开源项目": ("open source", "github", "repository", "sdk", "framework", "开源", "仓库", "框架"),
        "产业动态": ("launch", "release", "api", "product", "platform", "发布", "产品", "平台", "企业"),
    }
    intrinsic = {
        "论文": "重要研究",
        "开源项目": "开源项目",
        "模型发布": "大模型",
        "Agent产品": "Agent",
        "产业事件": "产业动态",
    }.get(content_type, "产业动态")
    topics = [intrinsic]
    topics.extend(topic for topic, terms in groups.items() if any(term in text for term in terms))
    # Configured topics are discovery hints only. They neither add a topic without
    # evidence nor suppress an explicitly detected cross-topic.
    _ = candidates
    return sorted(dict.fromkeys(topics), key=lambda topic: TOPIC_ORDER.index(topic))[:4]


def compatibility_category(content_type: str, topics: list[str]) -> str:
    """Keep schema-v1 clients usable while schema-v2 readers use both dimensions."""
    if content_type == "论文":
        return "重要研究"
    if content_type == "开源项目":
        return "开源项目"
    if content_type == "模型发布":
        return "大模型"
    if content_type == "Agent产品":
        return "Agent"
    return next((topic for topic in topics if topic in TOPIC_ORDER), "产业动态")


def fallback_analysis(summary: str) -> dict:
    """Internal-only placeholder; entries are never published until AI copy passes review."""
    return {
        "keyFacts": [summary],
        "context": "",
        "beginnerExplainer": "",
        "readerContext": "",
        "termExplanations": [],
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
        summary = description[:260].rstrip() + ("…" if len(description) > 260 else "")
        if not summary:
            summary = "由独立采集器从官方信息源发现，建议打开原文查看完整内容。"
        content_type = source.get("contentType", "产业事件")
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
            "confidence": source.get("trust", "官方来源"),
            "whyItMatters": "",
            "details": [summary],
            "tags": [*topics, content_type, source["brand"], "自动采集"],
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
        "topics": ["开源项目"],
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
            release_name = clean_text(release.get("name") or release.get("tag_name") or "新版本")
            title = f"{full_name} {release_name}"
            topics = infer_topics(
                title,
                f"{description} {release_notes}",
                "开源项目",
                spec.get("topics", ["开源项目"]),
            )
            category = compatibility_category("开源项目", topics)
            language = repository.get("language") or "未标注"
            summary = f"{description} {release_notes[:500]}"
            analysis = fallback_analysis(summary)
            analysis["keyFacts"] = [
                f"版本：{release.get('tag_name', release_name)}；发布时间：{released_at[:10]}。",
                f"项目约 {stars:,} 个 Star；主要语言：{language}；许可证：{license_name}。",
                release_notes,
            ]
            output.append({
                "id": "github-" + hashlib.sha256(link.encode()).hexdigest()[:16],
                "category": category,
                "contentType": "开源项目",
                "contentTypeLocked": True,
                "topics": topics,
                "brand": "GitHub",
                "brandColor": "#24292F",
                "logoAsset": "Assets/Brands/github.svg",
                "title": title,
                "summary": summary,
                "sourceMaterial": (
                    f"项目：{full_name}\n版本：{release_name}\n发布时间：{released_at}\n"
                    f"项目简介：{description}\nRelease notes：{release_notes}\n"
                    f"Star：{stars:,}；主要语言：{language}；许可证：{license_name}。"
                ),
                "defaultBranch": repository.get("default_branch") or "main",
                "publishedAt": released_at[:10],
                "sourceName": "GitHub Release",
                "sourceUrl": link,
                "readMinutes": 4,
                "confidence": "项目仓库",
                "whyItMatters": "",
                "details": analysis["keyFacts"],
                "tags": [*topics, "开源项目", "GitHub", "正式发布", "自动采集"],
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


SENTENCE_END_PATTERN = re.compile(r"[。！？!?]")
EVENT_CONTEXT_MARKERS = (
    "这项", "本文", "本次", "这里", "该研究", "该项目", "该模型", "该系统",
    "这个版本", "这套方法", "这项政策", "在这个", "在本条", "在这条",
)


def is_single_reader_context_sentence(value: object) -> bool:
    """Keep the domain orientation short enough to read before the article."""
    text = str(value or "").strip()
    return (
        16 <= chinese_char_count(text) <= 80
        and "\n" not in text
        and len(SENTENCE_END_PATTERN.findall(text)) == 1
        and bool(SENTENCE_END_PATTERN.search(text[-1:]))
    )


def normalize_term_explanations(value: object) -> list[dict[str, str]]:
    """Accept only compact term/explanation pairs emitted by the writer."""
    if not isinstance(value, list):
        return []
    normalized: list[dict[str, str]] = []
    for entry in value:
        if not isinstance(entry, dict):
            continue
        term = str(entry.get("term") or "").strip()
        explanation = str(entry.get("explanation") or "").strip()
        if term and explanation:
            normalized.append({"term": term, "explanation": explanation})
    return normalized


def term_explanation_issues(item: dict) -> list[str]:
    explanations = normalize_term_explanations(item.get("termExplanations"))
    issues: list[str] = []
    if not 2 <= len(explanations) <= 4:
        issues.append("术语解释必须有2至4个")

    story_text = " ".join([
        str(item.get("title", "")),
        str(item.get("summary", "")),
        *(str(value) for value in item.get("keyFacts", [])),
        *(f"{section.get('title', '')} {section.get('body', '')}" for section in item.get("briefSections", [])),
    ]).lower()
    seen: set[str] = set()
    for index, entry in enumerate(explanations):
        term = entry["term"]
        explanation = entry["explanation"]
        normalized_term = term.lower()
        if normalized_term in seen:
            issues.append(f"术语解释[{index}]重复")
        seen.add(normalized_term)
        if len(term) > 40 or normalized_term not in story_text:
            issues.append(f"术语解释[{index}]未出现在资讯正文")
        if not 12 <= chinese_char_count(explanation) <= 90:
            issues.append(f"术语解释[{index}]长度无效")
        if not any(marker in explanation for marker in EVENT_CONTEXT_MARKERS):
            issues.append(f"术语解释[{index}]没有结合当前事件")
    return issues


def reader_copy(item: dict) -> list[str]:
    values = [item.get("title", ""), item.get("summary", "")]
    values.extend(str(value) for value in item.get("keyFacts", []))
    for key in ("context", "beginnerExplainer", "impact", "limitations", "whatToWatch"):
        values.append(str(item.get(key, "")))
    for section in item.get("briefSections", []):
        values.extend((str(section.get("title", "")), str(section.get("body", ""))))
    values.append(str(item.get("readerContext", "")))
    for explanation in normalize_term_explanations(item.get("termExplanations")):
        values.extend((explanation["term"], explanation["explanation"]))
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
        issues.append("contentType无效")
    if not isinstance(item.get("topics"), list) or not item.get("topics"):
        issues.append("topics为空")
    if chinese_char_count(item.get("title")) < 2:
        issues.append("标题中文不足")
    if chinese_char_count(str(item.get("title", ""))[:16]) < 4:
        issues.append("标题没有先给中文结论")
    if chinese_char_count(item.get("summary")) < 50:
        issues.append("摘要中文不足50字")
    if not is_single_reader_context_sentence(item.get("readerContext")):
        issues.append("领域定位必须是一句16至80字的中文说明")
    issues.extend(term_explanation_issues(item))
    if not 3 <= len(sections) <= 5:
        issues.append("正文不是3至5段")
    if sum(chinese_char_count(section.get("body")) for section in sections) < 275:
        issues.append("正文中文不足275字")
    if not sections or chinese_char_count(sections[0].get("body")) < 60:
        issues.append("首段中文不足60字")
    if sections and not all(chinese_char_count(section.get("body")) >= 45 for section in sections):
        issues.append("存在不足45字的正文段落")
    if any(contains_english_sentence(value) for value in copy):
        issues.append("读者可见字段含完整英文句子")
    matched_process_phrases = sorted({phrase for value in copy for phrase in PROCESS_PHRASES if phrase in value})
    if matched_process_phrases:
        issues.append("读者可见字段含元话语:" + "、".join(matched_process_phrases))
    if not source_entity_consistent(item):
        issues.append("标题或摘要改变了来源中的核心实体")
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
        buckets.setdefault(item.get("sourceName", "未知来源"), []).append(item)
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
    curated = [item for item in existing if "自动采集" not in item.get("tags", [])]
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
            item["tags"] = [tag for tag in item.get("tags", []) if tag != "补充阅读"] + ["补充阅读"]
        selected.extend(additions)
    if len(selected) < target:
        for item in supplements:
            if item in selected:
                continue
            item["tags"] = [tag for tag in item.get("tags", []) if tag != "补充阅读"] + ["补充阅读"]
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
    allowed = {"title", "summary", "contentType", "briefSections", "keyFacts", "context", "beginnerExplainer", "readerContext", "termExplanations", "impact", "limitations", "whatToWatch", "technicalRelevanceScore", "innovationScore", "topics"}
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
            "你是面向普通读者的中文 AI 科技记者。所有标题、摘要和正文必须是自然中文，产品名、模型名和必要术语可保留英文，但不能复制完整英文句子。"
            "只依据材料写事实。把contentType（论文、开源项目、模型发布、Agent产品、产业事件）与topics（大模型、Agent等主题）分开；绝不能因为论文讨论Agent就把论文改成Agent产品。contentTypeLocked为true时不得修改类型；为false时必须按本条事件而不是信息源名称修正类型。"
            "sourceTitle中的产品名、模型名、项目名或论文方法名是实体锚点，标题或摘要必须保留正确实体，绝不能把Codex改写成ChatGPT、把公开仓库误写成开源项目，或自行替换主体。"
            "读者第一眼只需要知道：它做到了什么、核心贡献或关键变化是什么。title和summary结论先行，不要用日期、来源、Star数或背景铺垫开头。"
            "summary为70-140个中文字符，用一段话交代成果、贡献和关键依据，不复述标题。"
            "briefSections必须是JSON数组，按信息量动态写3-5段，总计500-900个中文字符，少于275个中文字会被直接丢弃；首段正文至少60个中文字，每段正文至少45个中文字。第一段必须直接完整回答‘做到了什么’，其余段落从贡献、方法、关键结果、使用方式、适用对象、影响、具体限制中按材料选择，不能为凑模板重复。"
            "论文优先说明研究问题、方法、实验结果和贡献；项目优先说明能做什么、关键机制、上手方式和成熟度；模型或Agent发布优先说明新增能力、实现路径、效果和限制；产业事件按结果、变化、影响组织。段落标题要具体，避免机械使用‘发布信息与适用范围’。"
            "不得出现编辑流程、筛选逻辑、置信度、核查提醒、来源声明、‘建议查看原文’、‘以官方文档为准’等面向编辑的元话语；不得用‘值得关注’‘行业正在发展’等空话凑字。"
            "另写readerContext：只用一句16-80个中文字说明本条属于什么领域、正在解决什么问题，句末使用中文句号。"
            "再写termExplanations：从本条标题、摘要或正文确实出现且会妨碍普通读者理解的术语中选2-4个，格式为[{\"term\":\"术语\",\"explanation\":\"解释\"}]。解释必须用‘在这项研究中’‘这里’‘这个版本’等方式结合当前事件说明它具体起什么作用，不得复制百科定义，不得引入材料外结论。"
            "另生成中文keyFacts(3-5条)、context、beginnerExplainer、impact、limitations、whatToWatch；这些字段必须是事件内容，不得写工作流声明，且避免与正文重复。"
            "可修正topics；contentTypeLocked为true时不得修改contentType，为false时可依据本条材料修正。再给technicalRelevanceScore和innovationScore（0到1）；娱乐、人物花边、纯营销、普通代码推送或只有融资信息的技术相关性必须低于0.45。"
            "返回严格 JSON 对象，字段名必须保持英文；正文必须写成 \"briefSections\":[{\"title\":\"具体小标题\",\"body\":\"中文正文\"}]，不得改成中文字段名。整体格式为 {\"items\":[{\"id\":..., ...}]}。\n\n"
            + json.dumps(material, ensure_ascii=False)
        )
        payload = json.dumps({
            "model": model,
            "temperature": 0.2,
            "max_tokens": 8000,
            "response_format": {"type": "json_object"},
            "messages": [
                {"role": "system", "content": "输出严谨、克制、可核查的中文 JSON。"},
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
                                title = section.get("title") or section.get("段落标题") or section.get("标题")
                                body = section.get("body") or section.get("内容") or section.get("正文")
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
                    elif key == "readerContext":
                        if is_single_reader_context_sentence(value):
                            item[key] = str(value).strip()
                            changed = True
                    elif key == "termExplanations":
                        explanations = normalize_term_explanations(value)
                        if 2 <= len(explanations) <= 4:
                            item[key] = explanations
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
                item["tags"] = [tag for tag in item["tags"] if tag != "AI 增强"] + ["AI 增强"]
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
                "readerContext": item.get("readerContext"),
                "termExplanations": item.get("termExplanations"),
                "impact": item.get("impact"),
                "limitations": item.get("limitations"),
                "whatToWatch": item.get("whatToWatch"),
                "technicalRelevanceScore": item.get("technicalRelevanceScore"),
                "innovationScore": item.get("innovationScore"),
            },
        } for item in batch]
        prompt = (
            "你是独立于撰稿人的AI科技新闻审稿人。逐条比较sourceMaterial与draft，宁缺毋滥。只有同时满足以下条件才pass："
            "(1)标题、摘要、正文为自然中文且没有完整英文句子；(2)首段立即说清做到什么及贡献；"
            "(3)所有具体数字、能力、方法和因果表述均能从材料得到支持；(4)contentType与本条事件相符，contentTypeLocked为true时没有被修改，且topics准确；"
            "sourceTitle中的核心产品、模型、仓库或方法实体必须在标题或摘要中保持一致；公开可见的GitHub仓库若无明确开源许可证，不得称为开源项目。"
            "(5)正文为动态3-5段且信息密集，不重复、不凑模板；(6)没有编辑流程、来源声明、核查提醒或筛选方法等元话语；"
            "(7)readerContext用一句话准确说明领域与问题；termExplanations有2-4项，术语确实出现在本条正文，解释使用普通中文并结合本事件中的具体作用，而不是百科定义；"
            "(8)不是人物花边、娱乐、纯融资、普通代码推送或无实质变化的营销稿。"
            "分别独立重评technicalRelevanceScore与innovationScore（0到1）。"
            "只返回JSON：{\"items\":[{\"id\":\"...\",\"pass\":true,\"technicalRelevanceScore\":0.8,\"innovationScore\":0.7,\"issues\":[]}]}。\n\n"
            + json.dumps(material, ensure_ascii=False)
        )
        payload = json.dumps({
            "model": model,
            "temperature": 0,
            "max_tokens": 3000,
            "response_format": {"type": "json_object"},
            "messages": [
                {"role": "system", "content": "执行严格、独立、失败关闭的中文科技新闻审稿，只输出JSON。"},
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
                    issues.append("撰稿评分缺失")
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
            "大模型": sum("大模型" in item.get("topics", []) for item in items),
            "Agent": sum("Agent" in item.get("topics", []) for item in items),
            "论文": sum(item.get("contentType") == "论文" for item in items),
            "开源项目": sum(item.get("contentType") == "开源项目" for item in items),
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
