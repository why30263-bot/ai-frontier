"""Offline tests for the dependency-free multi-provider model router."""

from __future__ import annotations

import importlib.util
import io
import json
from pathlib import Path
import sys
import unittest
import urllib.error


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = REPOSITORY_ROOT / "scripts" / "model_router.py"
SPEC = importlib.util.spec_from_file_location("ai_frontier_model_router", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
model_router = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = model_router
SPEC.loader.exec_module(model_router)


class FakeResponse:
    def __init__(self, content: object):
        payload = {"choices": [{"message": {"content": json.dumps(content)}}]}
        self._data = json.dumps(payload).encode("utf-8")

    def read(self) -> bytes:
        return self._data

    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *_args: object) -> None:
        return None


def http_error(request: object, status: int, retry_after: str | None = None) -> urllib.error.HTTPError:
    headers = {} if retry_after is None else {"Retry-After": retry_after}
    return urllib.error.HTTPError(
        getattr(request, "full_url", "https://offline.test"),
        status,
        "offline failure",
        headers,
        io.BytesIO(b"sensitive upstream body"),
    )


class ModelRouterTests(unittest.TestCase):
    def test_falls_back_after_first_provider_network_failure(self) -> None:
        providers = {
            "writer": [
                model_router.Provider("first", "https://first.test/v1", "m1", "secret-1", 2),
                model_router.Provider("second", "https://second.test/v1", "m2", "secret-2", 2),
            ]
        }
        calls: list[str] = []

        def opener(request: object, timeout: float) -> FakeResponse:
            del timeout
            calls.append(request.full_url)
            if "first.test" in request.full_url:
                raise urllib.error.URLError("offline")
            return FakeResponse({"provider": "second"})

        router = model_router.ModelRouter(providers, opener=opener)
        result = router.chat_json("writer", [{"role": "user", "content": "hello"}])

        self.assertEqual({"provider": "second"}, result)
        self.assertEqual(2, sum("first.test" in url for url in calls))
        self.assertEqual(1, sum("second.test" in url for url in calls))

    def test_429_honors_retry_after_then_switches_provider(self) -> None:
        providers = {
            "writer": [
                model_router.Provider("limited", "https://limited.test/v1", "m1", "top-secret", 2),
                model_router.Provider("backup", "https://backup.test/v1", "m2", "backup-secret", 2),
            ]
        }
        sleeps: list[float] = []
        limited_calls = 0

        def opener(request: object, timeout: float) -> FakeResponse:
            nonlocal limited_calls
            del timeout
            if "limited.test" in request.full_url:
                limited_calls += 1
                raise http_error(request, 429, "2")
            return FakeResponse([{"ok": True}])

        router = model_router.ModelRouter(providers, opener=opener, sleep=sleeps.append)
        result = router.chat_json("writer", [{"role": "user", "content": "hello"}])

        self.assertEqual([{"ok": True}], result)
        self.assertEqual(2, limited_calls, "a provider gets at most one retry")
        self.assertEqual([2.0], sleeps)

        second_result = router.chat_json("writer", [{"role": "user", "content": "again"}])
        self.assertEqual([{"ok": True}], second_result)
        self.assertEqual(2, limited_calls, "an opened circuit must stay open for the run")

    def test_legacy_environment_supports_reviewer_specific_endpoint(self) -> None:
        env = {
            "AI_API_KEY": "writer-key",
            "AI_API_BASE": "https://writer.test/v1",
            "AI_MODEL": "writer-model",
            "AI_REVIEW_API_KEY": "review-key",
            "AI_REVIEW_API_BASE": "https://review.test/v1",
            "AI_REVIEW_MODEL": "review-model",
        }
        seen: dict[str, object] = {}

        def opener(request: object, timeout: float) -> FakeResponse:
            del timeout
            seen["url"] = request.full_url
            seen["authorization"] = request.get_header("Authorization")
            seen["body"] = json.loads(request.data.decode("utf-8"))
            return FakeResponse({"approved": True})

        router = model_router.ModelRouter(environ=env, opener=opener)
        result = router.chat_json("reviewer", [{"role": "user", "content": "review"}])

        self.assertEqual({"approved": True}, result)
        self.assertEqual("https://review.test/v1/chat/completions", seen["url"])
        self.assertEqual("Bearer review-key", seen["authorization"])
        self.assertEqual("review-model", seen["body"]["model"])

    def test_pool_role_missing_falls_back_to_legacy_environment(self) -> None:
        env = {
            "AI_PROVIDER_POOL_JSON": json.dumps(
                {
                    "writer": [
                        {
                            "name": "pool-writer",
                            "baseUrl": "https://pool.test/v1",
                            "model": "pool-model",
                            "apiKey": "pool-key",
                        }
                    ]
                }
            ),
            "AI_API_KEY": "legacy-key",
            "AI_API_BASE": "https://legacy.test/v1",
            "AI_MODEL": "legacy-model",
            "AI_REVIEW_MODEL": "legacy-review-model",
        }

        pool = model_router.load_provider_pool(env)

        self.assertEqual("pool-writer", pool["writer"][0].name)
        self.assertEqual("legacy-reviewer", pool["reviewer"][0].name)
        self.assertEqual("legacy-review-model", pool["reviewer"][0].model)

    def test_error_never_contains_api_key_or_upstream_body(self) -> None:
        secret = "must-never-appear"
        providers = {
            "writer": [
                model_router.Provider("bad-auth", "https://bad.test/v1", "m", secret, 2)
            ]
        }

        def opener(request: object, timeout: float) -> FakeResponse:
            del timeout
            raise http_error(request, 401)

        router = model_router.ModelRouter(providers, opener=opener)
        with self.assertRaises(model_router.ModelRouterError) as caught:
            router.chat_json("writer", [{"role": "user", "content": "hello"}])

        message = str(caught.exception)
        self.assertNotIn(secret, message)
        self.assertNotIn("sensitive upstream body", message)
        self.assertIn("authentication (401)", message)
        self.assertNotIn(secret, repr(providers["writer"][0]))


if __name__ == "__main__":
    unittest.main()
