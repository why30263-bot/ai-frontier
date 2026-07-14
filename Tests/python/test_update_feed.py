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
        f"{seed}???????????????????????????????????????"
        "??????????????????????????"
    )
    return (sentence * ((length // len(sentence)) + 2))[:length]


def report_item(
    item_id: str,
    *,
    content_type: str = "????",
    topics: list[str] | None = None,
) -> dict:
    topics = list(topics or ["????"])
    return {
        "id": item_id,
        "category": update_feed.compatibility_category(content_type, topics),
        "contentType": content_type,
        "contentTypeLocked": False,
        "topics": topics,
        "brand": "????",
        "brandColor": "#123456",
        "logoAsset": "",
        "title": "????????????????",
        "summary": chinese_text("??", 90),
        "sourceMaterial": chinese_text("??", 500),
        "publishedAt": "2026-07-14",
        "sourceName": "????",
        "sourceUrl": f"https://example.test/{item_id}",
        "readMinutes": 4,
        "confidence": "????",
        "whyItMatters": chinese_text("??", 55),
        "details": [chinese_text("??", 55)],
        "tags": [*topics, content_type],
        "sourceTrail": [f"????: https://example.test/{item_id}"],
        "keyFacts": [chinese_text("????", 55) for _ in range(3)],
        "context": chinese_text("??", 55),
        "beginnerExplainer": chinese_text("??", 55),
        "impact": chinese_text("??", 55),
        "limitations": chinese_text("??", 55),
        "whatToWatch": chinese_text("??", 55),
        "briefSections": [
            {"title": "????", "body": chinese_text("??", 125)},
            {"title": "????", "body": chinese_text("??", 115)},
            {"title": "?????", "body": chinese_text("??", 115)},
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
            any("??" in issue for issue in update_feed.chinese_report_issues(item))
        )

    def test_rejects_short_or_meta_process_copy(self) -> None:
        item = report_item("gate-meta")
        item["summary"] = "??"
        item["briefSections"][0]["body"] += "???????"
        issues = update_feed.chinese_report_issues(item)
        self.assertTrue(any("50" in issue for issue in issues))
        self.assertTrue(any("???" in issue for issue in issues))


class WriterNormalizationTests(unittest.TestCase):
    def test_normalizes_mixed_english_and_chinese_nested_section_keys(self) -> None:
        item = report_item("mixed-sections", content_type="??", topics=["????"])
        item["contentTypeLocked"] = True
        mixed_sections = [
            {"title": "????", "body": chinese_text("???", 125)},
            {"????": "????", "??": chinese_text("???", 115)},
            {"??": "????", "??": chinese_text("???", 115)},
        ]
        writer_row = {
            "id": item["id"],
            "contentType": "Agent??",
            "topics": ["????", "Agent"],
            "title": "?????????????????",
            "summary": chinese_text("??", 90),
            "briefSections": mixed_sections,
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
        self.assertEqual("??", enriched[0]["contentType"], "locked source type must win")
        self.assertEqual(
            ["title", "body"],
            list(enriched[0]["briefSections"][1].keys()),
        )
        self.assertEqual("????", enriched[0]["briefSections"][1]["title"])
        self.assertEqual("????", enriched[0]["briefSections"][2]["title"])


class TopicEvidenceTests(unittest.TestCase):
    def test_invalid_source_date_is_never_promoted_to_now(self) -> None:
        parsed = update_feed.parse_date("not-a-date")
        self.assertEqual(1970, parsed.year)

    def test_configured_topic_hints_cannot_create_unsupported_topics(self) -> None:
        topics = update_feed.infer_topics(
            "??????????",
            "???????????????",
            "????",
            candidates=["???", "Agent", "????"],
        )
        self.assertEqual(["????"], topics)

    def test_explicit_source_evidence_adds_cross_topic(self) -> None:
        topics = update_feed.infer_topics(
            "???? multi-agent ??",
            "The framework adds tool use for an agentic workflow.",
            "????",
            candidates=[],
        )
        self.assertIn("????", topics)
        self.assertIn("Agent", topics)


class GitHubReleaseMaterialTests(unittest.TestCase):
    def test_release_notes_remain_primary_and_readme_is_only_context(self) -> None:
        item = report_item("release-material", content_type="????", topics=["????"])
        item.update(
            {
                "sourceUrl": "https://github.com/example/project/releases/tag/v2.0.0",
                "defaultBranch": "main",
                "sourceMaterial": "Release notes?????????????????????",
            }
        )
        requested_urls: list[str] = []

        def fake_fetch(url: str, timeout: int = 20) -> bytes:
            del timeout
            requested_urls.append(url)
            return ("# Project\n" + chinese_text("????", 700)).encode("utf-8")

        with mock.patch.object(update_feed, "fetch", side_effect=fake_fetch):
            material = update_feed.fetch_source_material(item)

        self.assertEqual(
            ["https://raw.githubusercontent.com/example/project/main/README.md"],
            requested_urls,
        )
        self.assertTrue(material.startswith("Release notes??????????"))
        self.assertIn("?????README?", material)
        self.assertLess(material.index("Release notes"), material.index("README"))


class DraftPublicationAndCoverageTests(unittest.TestCase):
    def _coverage_items(self) -> list[dict]:
        definitions = [
            ("paper-model-1", "??", ["????", "???"], "????????????????"),
            ("paper-agent-1", "??", ["????", "Agent"], "???????????????"),
            ("project-model-1", "????", ["????", "???"], "???????????????"),
            ("project-agent-1", "????", ["????", "Agent"], "???????????????"),
            ("model-1", "????", ["???"], "???????????????"),
            ("agent-1", "Agent??", ["Agent"], "???????????????"),
            ("industry-1", "????", ["????"], "???????????????"),
            ("industry-2", "????", ["????"], "???????????????"),
            ("industry-3", "????", ["????"], "???????????????"),
            ("industry-4", "????", ["????"], "??????????????"),
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
            report_item(f"model-only-{index}", content_type="????", topics=["???"])
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
            report_item(f"model-only-ci-{index}", content_type="????", topics=["???"])
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
