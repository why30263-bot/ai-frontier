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
CONTENT_TYPES = {"论文", "开源项目", "模型发布", "Agent产品", "产业事件"}
ALLOWED_TOPICS = {"大模型", "Agent", "重要研究", "开源项目", "产业动态"}
ENGLISH_SENTENCE_PATTERN = re.compile(
    r"\b(?:[A-Za-z][A-Za-z0-9+.#/'-]*\s+){7,}[A-Za-z][A-Za-z0-9+.#/'-]*[.!?]?"
)
PROCESS_PHRASES = (
    "原始信息来自", "当前来源", "来源摘要", "建议打开原文", "回到原文", "以官方文档为准",
    "自动采集", "采集器", "筛选逻辑", "置信度", "人工核查", "跨来源验证", "报道边界",
    "无法替代", "未披露的内容不作补写", "需要结合原文", "需要核对",
)
SENTENCE_END_PATTERN = re.compile(r"[。！？!?]")
EVENT_CONTEXT_MARKERS = (
    "这项", "本文", "本次", "这里", "该研究", "该项目", "该模型", "该系统",
    "这个版本", "这套方法", "这项政策", "在这个", "在本条", "在这条",
)


def chinese_character_count(value: Any) -> int:
    return len(HAN_PATTERN.findall(value if isinstance(value, str) else ""))


def is_single_reader_context_sentence(value: Any) -> bool:
    text = value.strip() if isinstance(value, str) else ""
    return (
        16 <= chinese_character_count(text) <= 80
        and "\n" not in text
        and len(SENTENCE_END_PATTERN.findall(text)) == 1
        and bool(SENTENCE_END_PATTERN.search(text[-1:]))
    )


def validate_reader_help(item: dict[str, Any], label: str, reader_text: list[str], errors: list[str]) -> None:
    context = item.get("readerContext")
    if not is_single_reader_context_sentence(context):
        errors.append(f"{label}.readerContext 必须是一句 16-80 个中文字符的领域与问题说明")
    else:
        reader_text.append(context)

    explanations = item.get("termExplanations")
    if not isinstance(explanations, list) or not 2 <= len(explanations) <= 4:
        errors.append(f"{label}.termExplanations 必须有 2-4 项")
        return
    story_text = " ".join([
        str(item.get("title", "")),
        str(item.get("summary", "")),
        *(str(value) for value in item.get("keyFacts", [])),
        *(f"{section.get('title', '')} {section.get('body', '')}" for section in item.get("briefSections", []) if isinstance(section, dict)),
    ]).lower()
    seen: set[str] = set()
    for explanation_index, entry in enumerate(explanations):
        entry_label = f"{label}.termExplanations[{explanation_index}]"
        if not isinstance(entry, dict):
            errors.append(f"{entry_label} 必须是对象")
            continue
        term = str(entry.get("term") or "").strip()
        explanation = str(entry.get("explanation") or "").strip()
        normalized_term = term.lower()
        if not term or len(term) > 40:
            errors.append(f"{entry_label}.term 无效")
        elif normalized_term in seen:
            errors.append(f"{entry_label}.term 重复")
        elif normalized_term not in story_text:
            errors.append(f"{entry_label}.term 未出现在资讯正文")
        seen.add(normalized_term)
        if not 12 <= chinese_character_count(explanation) <= 90:
            errors.append(f"{entry_label}.explanation 必须有 12-90 个中文字符")
        elif not any(marker in explanation for marker in EVENT_CONTEXT_MARKERS):
            errors.append(f"{entry_label}.explanation 必须结合当前事件解释")
        reader_text.extend((term, explanation))


def load_json(path: Path, errors: list[str]) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append(f"无法读取资讯数据 {path}: {exc}")
        return {}
    if not isinstance(value, dict):
        errors.append("资讯数据根节点必须是 JSON 对象")
        return {}
    return value


def project_version(path: Path, errors: list[str]) -> str:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        errors.append(f"无法读取项目文件 {path}: {exc}")
        return ""
    versions = [
        (element.text or "").strip()
        for element in root.iter()
        if element.tag.rsplit("}", 1)[-1] == "Version" and (element.text or "").strip()
    ]
    if len(set(versions)) != 1:
        errors.append(f"AIFrontier.csproj 必须且只能声明一个一致的 Version，当前为 {versions or '空'}")
        return ""
    return versions[0]


def validate_item(item: Any, index: int, errors: list[str]) -> None:
    label = f"items[{index}]"
    if not isinstance(item, dict):
        errors.append(f"{label} 必须是对象")
        return

    item_id = item.get("id")
    if not isinstance(item_id, str) or not item_id.strip():
        errors.append(f"{label}.id 不能为空")

    content_type = item.get("contentType")
    if content_type not in CONTENT_TYPES:
        errors.append(f"{label}.contentType 无效: {content_type!r}")

    topics = item.get("topics")
    if not isinstance(topics, list) or not topics or not all(isinstance(topic, str) and topic.strip() for topic in topics):
        errors.append(f"{label}.topics 必须是非空字符串数组")
    elif any(topic not in ALLOWED_TOPICS for topic in topics):
        errors.append(f"{label}.topics 含不支持的主题: {topics!r}")

    title = item.get("title")
    summary = item.get("summary")
    if not isinstance(title, str) or chinese_character_count(title) < 2:
        errors.append(f"{label}.title 至少需要 2 个中文字符")
    elif chinese_character_count(title[:16]) < 4:
        errors.append(f"{label}.title 必须先给出中文结论")
    if not isinstance(summary, str) or chinese_character_count(summary) < 50:
        errors.append(f"{label}.summary 至少需要 50 个中文字符")

    source_url = item.get("sourceUrl")
    if not isinstance(source_url, str) or not source_url.startswith(("https://", "http://")):
        errors.append(f"{label}.sourceUrl 必须是 HTTP(S) 链接")
    if not isinstance(item.get("publishedAt"), str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", item["publishedAt"]):
        errors.append(f"{label}.publishedAt 必须是 YYYY-MM-DD")
    for score_name, minimum in (("technicalRelevanceScore", 0.55), ("innovationScore", 0.35)):
        score = item.get(score_name)
        if not isinstance(score, (int, float)) or isinstance(score, bool) or not minimum <= float(score) <= 1:
            errors.append(f"{label}.{score_name} 必须在 {minimum} 到 1 之间")

    sections = item.get("briefSections")
    if not isinstance(sections, list) or not 3 <= len(sections) <= 5:
        length = len(sections) if isinstance(sections, list) else "非数组"
        errors.append(f"{label}.briefSections 必须有 3-5 段，当前为 {length}")
        return

    total_chinese = 0
    reader_text = [str(title or ""), str(summary or "")]
    for section_index, section in enumerate(sections):
        section_label = f"{label}.briefSections[{section_index}]"
        if not isinstance(section, dict):
            errors.append(f"{section_label} 必须是对象")
            continue
        title = section.get("title")
        body = section.get("body")
        if not isinstance(title, str) or chinese_character_count(title) < 2:
            errors.append(f"{section_label}.title 至少需要 2 个中文字符")
        if not isinstance(body, str) or not body.strip():
            errors.append(f"{section_label}.body 不能为空")
            continue
        total_chinese += chinese_character_count(body)
        reader_text.extend((str(title or ""), body))
        if chinese_character_count(body) < 45:
            errors.append(f"{section_label}.body 至少需要 45 个中文字符")

    if sections and isinstance(sections[0], dict) and chinese_character_count(sections[0].get("body")) < 60:
        errors.append(f"{label}.briefSections[0].body 至少需要 60 个中文字符")

    if total_chinese < 275:
        errors.append(f"{label} 正文至少需要 275 个中文字符，当前为 {total_chinese}")

    for field in ("keyFacts", "context", "beginnerExplainer", "impact", "limitations", "whatToWatch"):
        value = item.get(field, [])
        if isinstance(value, list):
            reader_text.extend(str(part) for part in value)
        else:
            reader_text.append(str(value or ""))
    validate_reader_help(item, label, reader_text, errors)
    if any(ENGLISH_SENTENCE_PATTERN.search(text) for text in reader_text):
        errors.append(f"{label} 的读者可见字段含完整英文句子")
    leaked = sorted({phrase for text in reader_text for phrase in PROCESS_PHRASES if phrase in text})
    if leaked:
        errors.append(f"{label} 的读者可见字段含编辑元话语: {'、'.join(leaked)}")


def validate_content(document: dict[str, Any], errors: list[str], maximum_age_hours: int | None) -> None:
    if document.get("schemaVersion") != 2:
        errors.append(f"schemaVersion 必须严格等于 2，当前为 {document.get('schemaVersion')!r}")
        return

    items = document.get("items")
    if not isinstance(items, list):
        errors.append("items 必须是数组")
        return
    if len(items) < 20:
        errors.append(f"每期至少需要 20 条资讯，当前为 {len(items)}")
    if len(items) % 10:
        errors.append(f"资讯总数必须是 10 的整倍数，当前为 {len(items)}")

    generated_at = document.get("generatedAt")
    try:
        generated = dt.datetime.fromisoformat(str(generated_at).replace("Z", "+00:00"))
        if generated.tzinfo is None:
            raise ValueError("missing timezone")
        now = dt.datetime.now(dt.timezone.utc)
        if generated.astimezone(dt.timezone.utc) > now + dt.timedelta(minutes=5):
            errors.append("generatedAt 不得晚于当前时间超过 5 分钟")
        if maximum_age_hours is not None and now - generated.astimezone(dt.timezone.utc) > dt.timedelta(hours=maximum_age_hours):
            errors.append(f"资讯版本不得早于 {maximum_age_hours} 小时")
    except (TypeError, ValueError):
        errors.append("generatedAt 必须是带时区的 ISO 8601 时间")

    edition_date = document.get("editionDate")
    try:
        dt.date.fromisoformat(str(edition_date))
    except ValueError:
        errors.append("editionDate 必须是 YYYY-MM-DD")
    window_hours = document.get("windowHours")
    if not isinstance(window_hours, int) or not 24 <= window_hours <= 336:
        errors.append("windowHours 必须在 24 到 336 之间")

    for index, item in enumerate(items):
        validate_item(item, index, errors)

    ids = [item.get("id") for item in items if isinstance(item, dict) and item.get("id")]
    if len(ids) != len(set(ids)):
        errors.append("items 中存在重复 id")
    source_urls = [item.get("sourceUrl") for item in items if isinstance(item, dict) and item.get("sourceUrl")]
    if len(source_urls) != len(set(source_urls)):
        errors.append("items 中存在重复 sourceUrl")

    for start in range(0, len(items), 10):
        batch = items[start:start + 10]
        if len(batch) != 10:
            continue
        coverage = {
            "大模型": sum(isinstance(item, dict) and "大模型" in item.get("topics", []) for item in batch) >= 2,
            "Agent": sum(isinstance(item, dict) and "Agent" in item.get("topics", []) for item in batch) >= 2,
            "论文": any(isinstance(item, dict) and item.get("contentType") == "论文" for item in batch),
            "开源项目": any(isinstance(item, dict) and item.get("contentType") == "开源项目" for item in batch),
        }
        for category, present in coverage.items():
            if not present:
                errors.append(f"第 {start // 10 + 1} 批缺少{category}")


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
        errors.append(f"发布版本必须为 MAJOR.MINOR.PATCH，当前为 {expected_version!r}")
    if declared_version and expected_version != declared_version:
        errors.append(
            f"版本不一致：工作流请求 {expected_version}，AIFrontier.csproj 声明 {declared_version}"
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
