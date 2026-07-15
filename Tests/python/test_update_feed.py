"""Offline regression tests for the AI Frontier editorial pipeline.

The suite deliberately mocks every network/model boundary.  It exercises the
public pipeline functions and ``main`` as contracts, so it can run unchanged on
GitHub Actions with only the Python standard library.
"""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = REPOSITORY_ROOT / "scripts" / "update_feed.py"
SPEC = importlib.util.spec_from_file_location("ai_frontier_update_feed", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
update_feed = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(update_feed)


def chinese_text(seed: str, length: int) -> str:
    """Return natural-enough Chinese fixture copy with a predictable length."""
    sentence = (
        f"{seed}团队完成了可复现的技术改进，并公开说明实现方法、关键结果、适用条件与已知限制，"
        "让普通读者能够直接理解这项工作的能力边界和实际贡献。"
    )
    return (sentence * ((length // len(sentence)) + 2))[:length]


def report_item(
    item_id: str,
    *,
    content_type: str = "产业事件",
    topics: list[str] | None = None,
) -> dict:
    topics = list(topics or ["产业动态"])
    return {
        "id": item_id,
        "category": update_feed.compatibility_category(content_type, topics),
        "contentType": content_type,
        "contentTypeLocked": False,
        "topics": topics,
        "brand": "测试来源",
        "brandColor": "#123456",
        "logoAsset": "",
        "title": "新系统显著提升复杂任务执行稳定性",
        "summary": chinese_text("摘要", 90),
        "sourceMaterial": chinese_text("材料", 500),
        "publishedAt": "2026-07-14",
        "sourceName": "测试来源",
        "sourceUrl": f"https://example.test/{item_id}",
        "readMinutes": 4,
        "confidence": "官方来源",
        "whyItMatters": chinese_text("影响", 55),
        "details": [chinese_text("事实", 55)],
        "tags": [*topics, content_type],
        "sourceTrail": [f"测试来源: https://example.test/{item_id}"],
        "keyFacts": [chinese_text("关键事实", 55) for _ in range(3)],
        "context": chinese_text("背景", 55),
        "beginnerExplainer": chinese_text("解释", 55),
        "readerContext": "这项工作属于智能系统可靠性研究，重点解决复杂任务执行时结果不稳定的问题。",
        "termExplanations": [
            {"term": "实现方法", "explanation": "在这项工作中，它指团队为完成技术改进而采用的具体处理步骤。"},
            {"term": "适用条件", "explanation": "这里指该系统能够保持可靠结果时需要满足的输入与运行范围。"},
        ],
        "impact": chinese_text("影响", 55),
        "limitations": chinese_text("限制", 55),
        "whatToWatch": chinese_text("后续", 55),
        "briefSections": [
            {"title": "直接成果", "body": chinese_text("成果", 125)},
            {"title": "实现方法", "body": chinese_text("方法", 115)},
            {"title": "结果与边界", "body": chinese_text("结果", 115)},
        ],
        "fullBrief": "",
        "technicalRelevanceScore": 0.82,
        "innovationScore": 0.68,
    }


class FakeHttpResponse:
    def __init__(self, payload: bytes):
        self._payload = payload

    def read(self) -> bytes:
        return self._payload

    def __enter__(self) -> "FakeHttpResponse":
        return self

    def __exit__(self, *_args: object) -> None:
        return None


class ChineseReportGateTests(unittest.TestCase):
    def test_accepts_dense_chinese_report_and_rejects_reader_facing_english(self) -> None:
        item = report_item("gate-valid")
        self.assertTrue(update_feed.is_chinese_report(item))
        self.assertEqual([], update_feed.chinese_report_issues(item))

        item["briefSections"][1]["body"] += (
            " This sentence contains enough English words to be rejected by the reader copy gate."
        )
        self.assertFalse(update_feed.is_chinese_report(item))
        self.assertTrue(
            any("英文" in issue for issue in update_feed.chinese_report_issues(item))
        )

    def test_rejects_short_or_meta_process_copy(self) -> None:
        item = report_item("gate-meta")
        item["summary"] = "太短"
        item["briefSections"][0]["body"] += "建议打开原文。"
        issues = update_feed.chinese_report_issues(item)
        self.assertTrue(any("50" in issue for issue in issues))
        self.assertTrue(any("元话语" in issue for issue in issues))

    def test_requires_one_reader_context_sentence_and_event_specific_terms(self) -> None:
        item = report_item("reader-help")
        item["readerContext"] = "这是第一句。这里又写了第二句。"
        item["termExplanations"][0]["explanation"] = "实现方法是一个常见的计算机术语。"
        issues = update_feed.chinese_report_issues(item)
        self.assertTrue(any("领域定位" in issue for issue in issues))
        self.assertTrue(any("结合当前事件" in issue for issue in issues))

    def test_rejects_missing_or_unmentioned_term_explanations(self) -> None:
        item = report_item("reader-terms")
        item["termExplanations"] = [
            {"term": "不存在的术语", "explanation": "在这项工作中，它表示材料里并没有出现的概念。"}
        ]
        issues = update_feed.chinese_report_issues(item)
        self.assertTrue(any("2至4个" in issue for issue in issues))
        self.assertTrue(any("未出现在资讯正文" in issue for issue in issues))


class WriterNormalizationTests(unittest.TestCase):
    def test_normalizes_mixed_english_and_chinese_nested_section_keys(self) -> None:
        item = report_item("mixed-sections", content_type="论文", topics=["重要研究"])
        item["contentTypeLocked"] = True
        mixed_sections = [
            {"title": "直接成果", "body": chinese_text("第一段", 125)},
            {"段落标题": "研究方法", "内容": chinese_text("第二段", 115)},
            {"标题": "实验结果", "正文": chinese_text("第三段", 115)},
        ]
        writer_row = {
            "id": item["id"],
            "contentType": "Agent产品",
            "topics": ["重要研究", "Agent"],
            "title": "研究团队提出更可靠的智能体评测方法",
            "summary": chinese_text("摘要", 90),
            "briefSections": mixed_sections,
            "readerContext": "这项研究属于智能体评测领域，重点解决复杂任务中方法效果难以可靠比较的问题。",
            "termExplanations": [
                {"term": "智能体评测", "explanation": "在这项研究中，它指用统一任务比较不同智能体的实际完成效果。"},
                {"term": "复杂任务", "explanation": "这里指需要连续执行多个步骤并根据反馈调整行动的任务。"},
            ],
            "technicalRelevanceScore": 0.9,
            "innovationScore": 0.8,
        }
        api_payload = json.dumps(
            {"choices": [{"message": {"content": json.dumps({"items": [writer_row]}, ensure_ascii=False)}}]},
            ensure_ascii=False,
        ).encode("utf-8")

        env = {
            "AI_API_KEY": "offline-token",
            "AI_API_BASE": "https://offline.test/v1",
            "AI_MODEL": "offline-model",
        }
        with mock.patch.dict(os.environ, env, clear=False), mock.patch.object(
            update_feed.urllib.request,
            "urlopen",
            return_value=FakeHttpResponse(api_payload),
        ):
            enriched, count = update_feed.enrich_with_ai([item])

        self.assertEqual(1, count)
        self.assertEqual("论文", enriched[0]["contentType"], "locked source type must win")
        self.assertEqual(
            ["title", "body"],
            list(enriched[0]["briefSections"][1].keys()),
        )
        self.assertEqual("研究方法", enriched[0]["briefSections"][1]["title"])
        self.assertEqual("实验结果", enriched[0]["briefSections"][2]["title"])
        self.assertEqual("智能体评测", enriched[0]["termExplanations"][0]["term"])


class TopicEvidenceTests(unittest.TestCase):
    def test_invalid_source_date_is_never_promoted_to_now(self) -> None:
        parsed = update_feed.parse_date("not-a-date")
        self.assertEqual(1970, parsed.year)

    def test_configured_topic_hints_cannot_create_unsupported_topics(self) -> None:
        topics = update_feed.infer_topics(
            "企业发布季度技术路线",
            "平台改进了部署流程和服务管理。",
            "产业事件",
            candidates=["大模型", "Agent", "重要研究"],
        )
        self.assertEqual(["产业动态"], topics)

    def test_explicit_source_evidence_adds_cross_topic(self) -> None:
        topics = update_feed.infer_topics(
            "工具支持 multi-agent 协作",
            "The framework adds tool use for an agentic workflow.",
            "开源项目",
            candidates=[],
        )
        self.assertIn("开源项目", topics)
        self.assertIn("Agent", topics)


class IncrementalSelectionTests(unittest.TestCase):
    def test_recognizes_published_event_without_rewriting_it(self) -> None:
        published = report_item("published-1")
        same_url = report_item("new-id")
        same_url["sourceUrl"] = published["sourceUrl"]
        same_title = report_item("new-title-id")
        same_title["sourceUrl"] = "https://example.test/different"
        same_title["title"] = published["title"]
        unseen = report_item("unseen")
        unseen["title"] = "另一项研究公开全新的智能体恢复方法"

        self.assertTrue(update_feed.is_existing_event(same_url, [published]))
        self.assertTrue(update_feed.is_existing_event(same_title, [published]))
        self.assertFalse(update_feed.is_existing_event(unseen, [published]))


class GitHubReleaseMaterialTests(unittest.TestCase):
    def test_release_notes_remain_primary_and_readme_is_only_context(self) -> None:
        item = report_item("release-material", content_type="开源项目", topics=["开源项目"])
        item.update(
            {
                "sourceUrl": "https://github.com/example/project/releases/tag/v2.0.0",
                "defaultBranch": "main",
                "sourceMaterial": "Release notes：新增结构化工具调用、恢复机制与错误报告。",
            }
        )
        requested_urls: list[str] = []

        def fake_fetch(url: str, timeout: int = 20) -> bytes:
            del timeout
            requested_urls.append(url)
            return ("# Project\n" + chinese_text("项目说明", 700)).encode("utf-8")

        with mock.patch.object(update_feed, "fetch", side_effect=fake_fetch):
            material = update_feed.fetch_source_material(item)

        self.assertEqual(
            ["https://raw.githubusercontent.com/example/project/main/README.md"],
            requested_urls,
        )
        self.assertTrue(material.startswith("Release notes：新增结构化工具调用"))
        self.assertIn("项目说明（README）", material)
        self.assertLess(material.index("Release notes"), material.index("README"))


class DraftPublicationAndCoverageTests(unittest.TestCase):
    def _coverage_items(self) -> list[dict]:
        definitions = [
            ("paper-model-1", "论文", ["重要研究", "大模型"], "新方法降低长文本推理中的记忆误差"),
            ("paper-agent-1", "论文", ["重要研究", "Agent"], "评测揭示多智能体协作的失效条件"),
            ("project-model-1", "开源项目", ["开源项目", "大模型"], "推理框架加入跨设备并行调度能力"),
            ("project-agent-1", "开源项目", ["开源项目", "Agent"], "智能体工具库实现任务中断后恢复"),
            ("model-1", "模型发布", ["大模型"], "多模态模型提升复杂图表理解精度"),
            ("agent-1", "Agent产品", ["Agent"], "桌面智能体新增可控操作确认机制"),
            ("industry-1", "产业事件", ["产业动态"], "云平台开放模型部署成本分析工具"),
            ("industry-2", "产业事件", ["产业动态"], "芯片厂商公布新一代互连技术路线"),
            ("industry-3", "产业事件", ["产业动态"], "数据服务商推出训练语料治理方案"),
            ("industry-4", "产业事件", ["产业动态"], "安全机构建立生成内容检测标准"),
        ]
        items = []
        for batch in range(2):
            for item_id, kind, topics, title in definitions:
                item = report_item(f"{item_id}-b{batch}", content_type=kind, topics=topics)
                item["title"] = title
                item["eventKey"] = f"fixture:{item_id}:b{batch}"
                items.append(item)
        return items

    def _run_main_from_draft(
        self,
        temporary_directory: str,
        items: list[dict],
        *,
        fail_on_preserve: bool = False,
    ) -> tuple[int, Path, list[tuple[Path, Path]]]:
        root = Path(temporary_directory)
        config_path = root / "source-feeds.json"
        output_path = root / "news.json"
        draft_path = root / "news.draft.json"
        config_path.write_text(
            json.dumps({"categoryMinimums": {}, "maxItems": 20, "feeds": []}),
            encoding="utf-8",
        )
        original = {"schemaVersion": 1, "items": [{"id": "last-known-good"}]}
        output_path.write_text(json.dumps(original), encoding="utf-8")
        draft_path.write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "pipelineContractVersion": update_feed.PIPELINE_CONTRACT_VERSION,
                    "configurationFingerprint": update_feed.configuration_fingerprint(config_path),
                    "createdAt": update_feed.dt.datetime.now().astimezone().isoformat(timespec="seconds"),
                    "candidateLimit": 20,
                    "enrichedCount": len(items),
                    "items": items,
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )

        replacements: list[tuple[Path, Path]] = []
        real_replace = os.replace

        def recording_replace(source: os.PathLike[str] | str, target: os.PathLike[str] | str) -> None:
            replacements.append((Path(source), Path(target)))
            real_replace(source, target)

        argv = [
            str(SCRIPT_PATH),
            "--config",
            str(config_path),
            "--output",
            str(output_path),
            "--reuse-draft",
            "--draft-cache",
            str(draft_path),
        ]
        if fail_on_preserve:
            argv.append("--fail-on-preserve")
        env = {
            "AI_API_KEY": "offline-token",
            "AI_API_BASE": "https://offline.test/v1",
            "AI_MODEL": "offline-model",
            "AI_REVIEW_MODEL": "offline-reviewer",
            "AI_REVIEW_COOLDOWN_SECONDS": "0",
        }
        with (
            mock.patch.object(sys, "argv", argv),
            mock.patch.dict(os.environ, env, clear=False),
            mock.patch.object(update_feed, "review_with_ai", return_value=(items, len(items))),
            mock.patch.object(update_feed, "parse_feed", side_effect=AssertionError("collector must not run")),
            mock.patch.object(update_feed, "collect_github", side_effect=AssertionError("collector must not run")),
            mock.patch.object(update_feed, "enrich_with_ai", side_effect=AssertionError("writer must not run")),
            mock.patch.object(update_feed.os, "replace", side_effect=recording_replace),
        ):
            return_code = update_feed.main()
        return return_code, output_path, replacements

    def test_reuses_draft_and_atomically_publishes_only_after_all_gates_pass(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            return_code, output_path, replacements = self._run_main_from_draft(
                temporary_directory,
                self._coverage_items(),
            )
            edition = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual(0, return_code)
        self.assertEqual(2, edition["schemaVersion"])
        self.assertEqual(20, len(edition["items"]))
        self.assertTrue(all(
            update_feed.batch_has_core_coverage(edition["items"][start:start + 10])
            for start in range(0, 20, 10)
        ))
        self.assertEqual(1, len(replacements))
        source, target = replacements[0]
        self.assertEqual("news.json.tmp", source.name)
        self.assertEqual("news.json", target.name)
        self.assertFalse(source.exists())
        self.assertTrue(all("sourceMaterial" not in item for item in edition["items"]))

    def test_failed_coverage_preserves_last_known_good_edition(self) -> None:
        items = [
            report_item(f"model-only-{index}", content_type="模型发布", topics=["大模型"])
            for index in range(20)
        ]
        with tempfile.TemporaryDirectory() as temporary_directory:
            return_code, output_path, replacements = self._run_main_from_draft(
                temporary_directory,
                items,
            )
            edition = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual(0, return_code, "an existing good edition makes gate failure non-fatal")
        self.assertEqual({"schemaVersion": 1, "items": [{"id": "last-known-good"}]}, edition)
        self.assertEqual([], replacements)

    def test_ci_mode_preserves_last_known_good_but_returns_failure(self) -> None:
        items = [
            report_item(f"model-only-ci-{index}", content_type="模型发布", topics=["大模型"])
            for index in range(20)
        ]
        with tempfile.TemporaryDirectory() as temporary_directory:
            return_code, output_path, replacements = self._run_main_from_draft(
                temporary_directory,
                items,
                fail_on_preserve=True,
            )
            edition = json.loads(output_path.read_text(encoding="utf-8"))

        self.assertEqual(1, return_code)
        self.assertEqual({"schemaVersion": 1, "items": [{"id": "last-known-good"}]}, edition)
        self.assertEqual([], replacements)


if __name__ == "__main__":
    unittest.main()
