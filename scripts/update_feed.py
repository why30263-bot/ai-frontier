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
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET
from pathlib import Path


USER_AGENT = "AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)"


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


def parse_date(value: str) -> dt.datetime:
    value = value.strip().replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(value)
    except ValueError:
        from email.utils import parsedate_to_datetime

        try:
            parsed = parsedate_to_datetime(value)
        except (TypeError, ValueError):
            parsed = dt.datetime.now(dt.timezone.utc)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed


def fallback_analysis(category: str, summary: str, source_name: str) -> dict:
    explainers = {
        "Agent": "Agent 是会围绕目标连续规划、调用工具并检查结果的 AI 系统。",
        "开源项目": "开源项目允许公开检查和二次开发，但仍要核对许可证、维护频率和真实部署。",
        "重要研究": "论文描述的是方法和实验结论，不等于技术已经成为成熟产品。",
        "大模型": "大模型是处理文本、图像、音频或代码的通用 AI 基础系统。",
    }
    contexts = {
        "大模型": "大模型竞争已从单纯比较参数量，转向推理能力、多模态、工具调用、成本和可部署性的综合竞争。",
        "Agent": "Agent 正从演示型对话走向可检查的多步骤工作流，可靠性、权限控制和失败恢复是落地关键。",
        "开源项目": "开源 AI 生态更新很快，代码可见不代表容易复现；许可证、维护状态和部署成本同样重要。",
        "重要研究": "前沿论文常先给出受控实验结果，能否推广到真实场景仍需要复现、对照实验和后续工作验证。",
        "产业动态": "产业发布需要区分产品可用性、预览计划和宣传性表述，并观察真实客户是否持续采用。",
    }
    impacts = {
        "大模型": "如果原文披露的能力、价格或开放范围可复现，可能改变现有 AI 产品的能力边界与使用成本。",
        "Agent": "它可能影响 AI 能否从回答问题进一步走向完成真实任务，但稳定性和权限边界比演示效果更重要。",
        "开源项目": "它为开发者提供了可检查、可修改的实现路径，价值取决于复现难度、维护活跃度和生产适配。",
        "重要研究": "这项工作可能改变对模型能力或限制的理解，但论文结果在独立复现前应视为研究证据而非成熟结论。",
        "产业动态": "它反映厂商对 AI 产品化方向的投入；实际影响要看功能是否普遍开放、成本是否可控以及客户采用。",
    }
    return {
        "keyFacts": [f"原始信息来自 {source_name}。", summary],
        "context": contexts.get(category, "该条目处于模型能力、Agent 工程化和开源生态持续演进的背景中。"),
        "beginnerExplainer": explainers.get(category, "这条信息反映 AI 产品、研究或开发生态的一次公开更新。"),
        "impact": impacts.get(category, "实际影响取决于开放范围、成本、可复现性和真实用户采用，不能只根据发布标题判断。"),
        "limitations": "自动采集能确认原始来源发布了内容，但无法替代人工核查、跨来源验证或专业测评。",
        "whatToWatch": "继续观察官方文档或代码是否完整、是否出现独立评测，以及真实部署和失败案例能否验证原文主张。",
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
        analysis = fallback_analysis(source["category"], summary, source["name"])
        item = {
            "id": "feed-" + hashlib.sha256(link.encode()).hexdigest()[:16],
            "category": source["category"],
            "brand": source["brand"],
            "brandColor": source["brandColor"],
            "logoAsset": source.get("logoAsset", ""),
            "title": title.strip(),
            "summary": summary,
            "publishedAt": date.astimezone().date().isoformat(),
            "sourceName": source["name"],
            "sourceUrl": link,
            "readMinutes": max(2, min(8, len(description) // 350 + 2)),
            "confidence": source.get("trust", "官方来源"),
            "whyItMatters": analysis["impact"],
            "details": [summary],
            "tags": [source["category"], source["brand"], "自动采集"],
            "sourceTrail": [f"{source['name']}: {link}"],
            **analysis,
        }
        items.append(item)
    return items


def collect_github(config: dict) -> list[dict]:
    discovery = config.get("githubDiscovery", {})
    if not discovery.get("enabled", True):
        return []
    since = (dt.date.today() - dt.timedelta(days=int(config.get("supplementDays", 7)))).isoformat()
    query = f"{discovery.get('query', 'topic:artificial-intelligence stars:>500')} pushed:>={since}"
    url = "https://api.github.com/search/repositories?" + urllib.parse.urlencode({
        "q": query,
        "sort": "stars",
        "order": "desc",
        "per_page": min(10, int(discovery.get("maxItems", 4)) * 2),
    })
    request = urllib.request.Request(url, headers={
        "User-Agent": USER_AGENT,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    try:
        payload = json.loads(urllib.request.urlopen(request, timeout=20).read().decode("utf-8"))
    except Exception as exc:  # noqa: BLE001
        print(f"warning: GitHub project discovery: {exc}", file=sys.stderr)
        return []

    output: list[dict] = []
    for repository in payload.get("items", []):
        stars = int(repository.get("stargazers_count", 0))
        if stars < int(discovery.get("minimumStars", 500)):
            continue
        title = repository.get("full_name", "")
        link = repository.get("html_url", "")
        description = clean_text(repository.get("description") or "仓库近期保持活跃，建议打开 README 与提交记录进一步判断用途。")
        language = repository.get("language") or "未标注"
        license_name = (repository.get("license") or {}).get("spdx_id") or "未标注"
        summary = f"{description}（★ {stars:,}，主要语言：{language}）"
        analysis = fallback_analysis("开源项目", summary, "GitHub 仓库")
        analysis["keyFacts"] = [
            f"GitHub 当前显示约 {stars:,} 个 Star，主要语言为 {language}。",
            f"仓库许可证标识：{license_name}；最近推送时间：{repository.get('pushed_at', '未知')}。",
            description,
        ]
        analysis["limitations"] = "Star 数和近期推送只能反映关注度与活跃信号，不证明项目安全、稳定或适合生产环境。"
        analysis["whatToWatch"] = "检查最近提交、Release、Issue 响应、许可证、安装复现和实际资源消耗，再决定是否投入学习。"
        output.append({
            "id": "github-" + hashlib.sha256(link.encode()).hexdigest()[:16],
            "category": "开源项目",
            "brand": "GitHub",
            "brandColor": "#24292F",
            "logoAsset": "Assets/Brands/github.svg",
            "title": title,
            "summary": summary,
            "publishedAt": (repository.get("pushed_at") or dt.datetime.now(dt.timezone.utc).isoformat())[:10],
            "sourceName": "GitHub 项目发现",
            "sourceUrl": link,
            "readMinutes": 4,
            "confidence": "项目仓库",
            "whyItMatters": analysis["impact"],
            "details": analysis["keyFacts"],
            "tags": ["开源项目", "GitHub", "近期活跃", "自动采集"],
            "sourceTrail": [f"GitHub repository API: {link}"],
            **analysis,
        })
    return output[:int(discovery.get("maxItems", 4))]


def normalize_title(title: str) -> str:
    return re.sub(r"[^a-z0-9\u4e00-\u9fff]+", "", title.lower())


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


def merge(existing: list[dict], collected: list[dict], maximum: int, per_source: int) -> list[dict]:
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
    output.sort(key=lambda item: item.get("publishedAt", ""), reverse=True)
    return output[:maximum]


def apply_freshness(items: list[dict], fresh_hours: int, supplement_days: int, target: int) -> list[dict]:
    today = dt.date.today()
    fresh_cutoff = today - dt.timedelta(days=max(1, fresh_hours // 24))
    supplement_cutoff = today - dt.timedelta(days=max(1, supplement_days))

    def published_date(item: dict) -> dt.date:
        try:
            return dt.date.fromisoformat(item.get("publishedAt", ""))
        except ValueError:
            return today

    fresh = [item for item in items if published_date(item) >= fresh_cutoff]
    if len(fresh) >= target:
        return fresh
    supplements = [item for item in items if supplement_cutoff <= published_date(item) < fresh_cutoff]
    for item in supplements:
        item["tags"] = [tag for tag in item.get("tags", []) if tag != "补充阅读"] + ["补充阅读"]
    return [*fresh, *supplements]


def enrich_with_ai(items: list[dict]) -> tuple[list[dict], int]:
    """Optionally enrich entries through any OpenAI-compatible chat endpoint."""
    api_key = os.getenv("AI_API_KEY", "").strip()
    api_base = os.getenv("AI_API_BASE", "").strip().rstrip("/")
    model = os.getenv("AI_MODEL", "").strip()
    if not (api_key and api_base and model):
        return items, 0

    endpoint = api_base if api_base.endswith("/chat/completions") else f"{api_base}/chat/completions"
    allowed = {"title", "summary", "keyFacts", "context", "beginnerExplainer", "impact", "limitations", "whatToWatch"}
    enriched_count = 0
    for start in range(0, len(items), 6):
        batch = items[start:start + 6]
        material = [
            {"id": item["id"], "category": item["category"], "title": item["title"], "source": item["sourceName"], "sourceSummary": item["summary"]}
            for item in batch
        ]
        prompt = (
            "你是面向 AI 初学者的严谨中文科技编辑。只依据给定标题、来源摘要和来源身份，不补造事实。"
            "把 title 改写为自然、准确的中文标题，并生成：summary(60-120字)、keyFacts(2-4条)、context、beginnerExplainer、impact、"
            "limitations、whatToWatch。避免重复同一句话，明确区分事实、解释和判断。"
            "返回严格 JSON 对象，格式为 {\"items\":[{\"id\":..., ...}]}。\n\n"
            + json.dumps(material, ensure_ascii=False)
        )
        payload = json.dumps({
            "model": model,
            "temperature": 0.2,
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
            response = json.loads(urllib.request.urlopen(request, timeout=90).read().decode("utf-8"))
            content = response["choices"][0]["message"]["content"].strip()
            content = re.sub(r"^```(?:json)?\s*|\s*```$", "", content, flags=re.IGNORECASE)
            enriched = json.loads(content).get("items", [])
            by_id = {row.get("id"): row for row in enriched if isinstance(row, dict)}
            for item in batch:
                changed = False
                for key, value in by_id.get(item["id"], {}).items():
                    if key in allowed and value:
                        item[key] = value
                        changed = True
                if changed:
                    enriched_count += 1
                item["whyItMatters"] = item.get("impact", item["whyItMatters"])
                item["details"] = item.get("keyFacts", item["details"])
                item["tags"] = [tag for tag in item["tags"] if tag != "AI 增强"] + ["AI 增强"]
        except Exception as exc:  # noqa: BLE001
            print(f"warning: optional AI enrichment failed: {exc}", file=sys.stderr)
    return items, enriched_count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default="Data/source-feeds.json")
    parser.add_argument("--output", default="Data/news.json")
    parser.add_argument("--drop-existing", action="store_true", help="discard previously curated entries")
    args = parser.parse_args()

    config = json.loads(Path(args.config).read_text(encoding="utf-8"))
    output_path = Path(args.output)
    current = json.loads(output_path.read_text(encoding="utf-8")) if output_path.exists() else {"items": []}
    collected = [item for source in config["feeds"] for item in parse_feed(source)]
    collected.extend(collect_github(config))
    collected = apply_freshness(
        collected,
        int(config.get("freshHours", 72)),
        int(config.get("supplementDays", 7)),
        int(config.get("maxItems", 18)),
    )
    existing = [] if args.drop_existing else current.get("items", [])
    items = merge(
        existing,
        collected,
        int(config.get("maxItems", 18)),
        int(config.get("maxItemsPerSource", 4)),
    )
    items, enriched_count = enrich_with_ai(items)
    ai_requested = all(os.getenv(name, "").strip() for name in ("AI_API_KEY", "AI_API_BASE", "AI_MODEL"))
    if ai_requested and enriched_count < max(1, len(items) // 2) and current.get("items"):
        print("warning: Chinese editorial enrichment was incomplete; preserving the previous edition", file=sys.stderr)
        return 0
    edition = {
        "schemaVersion": 1,
        "editionDate": dt.date.today().isoformat(),
        "windowHours": 72,
        "generatedAt": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "items": items,
    }
    output_path.write_text(json.dumps(edition, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(items)} items to {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
