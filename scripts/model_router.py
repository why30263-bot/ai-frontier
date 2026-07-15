"""Small, dependency-free router for OpenAI-compatible chat providers.

The preferred configuration is ``AI_PROVIDER_POOL_JSON``::

    {
      "writer": [
        {"name": "qwen", "baseUrl": "https://example/v1",
         "model": "model-name", "apiKey": "secret", "maxAttempts": 2}
      ],
      "reviewer": [...]
    }

When a role is absent from that pool, the legacy ``AI_*`` variables are used.
The module intentionally has no third-party dependencies and never logs API
keys or response bodies.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from email.utils import parsedate_to_datetime
import json
import os
import time
from typing import Any, Callable, Mapping, Sequence
import urllib.error
import urllib.request


DEFAULT_TIMEOUT_SECONDS = 90.0
DEFAULT_MAX_RETRY_DELAY_SECONDS = 5.0
USER_AGENT = "AIFrontier/1.0 (+https://github.com/why30263-bot/ai-frontier)"


class ModelRouterError(RuntimeError):
    """Raised when no configured provider can return valid JSON content."""


class ProviderConfigurationError(ModelRouterError):
    """Raised when provider configuration is malformed or incomplete."""


@dataclass(frozen=True)
class Provider:
    """One OpenAI-compatible model endpoint."""

    name: str
    base_url: str
    model: str
    api_key: str = field(repr=False)
    max_attempts: int = 2

    @property
    def endpoint(self) -> str:
        base = self.base_url.rstrip("/")
        if base.endswith("/chat/completions"):
            return base
        return f"{base}/chat/completions"


def _provider_from_mapping(value: Mapping[str, Any], role: str, index: int) -> Provider:
    location = f"{role}[{index}]"
    required = ("name", "baseUrl", "model", "apiKey")
    missing = [key for key in required if not str(value.get(key, "")).strip()]
    if missing:
        raise ProviderConfigurationError(
            f"Invalid provider {location}: missing {', '.join(missing)}"
        )

    raw_attempts = value.get("maxAttempts", 2)
    try:
        max_attempts = int(raw_attempts)
    except (TypeError, ValueError) as exc:
        raise ProviderConfigurationError(
            f"Invalid provider {location}: maxAttempts must be an integer"
        ) from exc
    if max_attempts < 1:
        raise ProviderConfigurationError(
            f"Invalid provider {location}: maxAttempts must be positive"
        )

    # A provider is allowed at most one short retry, regardless of configuration.
    return Provider(
        name=str(value["name"]).strip(),
        base_url=str(value["baseUrl"]).strip(),
        model=str(value["model"]).strip(),
        api_key=str(value["apiKey"]).strip(),
        max_attempts=min(max_attempts, 2),
    )


def _legacy_provider(role: str, environ: Mapping[str, str]) -> Provider | None:
    if role == "reviewer":
        api_key = (
            environ.get("AI_REVIEW_API_KEY", "").strip()
            or environ.get("AI_API_KEY", "").strip()
        )
        base_url = (
            environ.get("AI_REVIEW_API_BASE", "").strip()
            or environ.get("AI_API_BASE", "").strip()
        )
        model = (
            environ.get("AI_REVIEW_MODEL", "").strip()
            or environ.get("AI_MODEL", "").strip()
        )
    else:
        api_key = environ.get("AI_API_KEY", "").strip()
        base_url = environ.get("AI_API_BASE", "").strip()
        model = environ.get("AI_MODEL", "").strip()

    if not (api_key and base_url and model):
        return None
    return Provider(
        name=f"legacy-{role}",
        base_url=base_url,
        model=model,
        api_key=api_key,
        max_attempts=2,
    )


def load_provider_pool(
    environ: Mapping[str, str] | None = None,
) -> dict[str, list[Provider]]:
    """Load writer/reviewer providers, with per-role legacy fallback."""

    env = os.environ if environ is None else environ
    configured: Mapping[str, Any] = {}
    raw_pool = env.get("AI_PROVIDER_POOL_JSON", "").strip()
    if raw_pool:
        try:
            decoded = json.loads(raw_pool)
        except json.JSONDecodeError as exc:
            raise ProviderConfigurationError(
                "AI_PROVIDER_POOL_JSON is not valid JSON"
            ) from exc
        if not isinstance(decoded, dict):
            raise ProviderConfigurationError(
                "AI_PROVIDER_POOL_JSON must contain a JSON object"
            )
        configured = decoded

    result: dict[str, list[Provider]] = {}
    for role in ("writer", "reviewer"):
        raw_providers = configured.get(role)
        if raw_providers is not None:
            if not isinstance(raw_providers, list):
                raise ProviderConfigurationError(f"{role} provider pool must be an array")
            providers: list[Provider] = []
            for index, raw_provider in enumerate(raw_providers):
                if not isinstance(raw_provider, dict):
                    raise ProviderConfigurationError(
                        f"Invalid provider {role}[{index}]: expected an object"
                    )
                providers.append(_provider_from_mapping(raw_provider, role, index))
            if providers:
                result[role] = providers
                continue

        legacy = _legacy_provider(role, env)
        result[role] = [legacy] if legacy is not None else []
    return result


def _retry_after_seconds(value: str | None, now: Callable[[], float]) -> float:
    if not value:
        return 0.0
    try:
        return max(0.0, float(value.strip()))
    except ValueError:
        try:
            retry_at = parsedate_to_datetime(value)
            return max(0.0, retry_at.timestamp() - now())
        except (TypeError, ValueError, OverflowError):
            return 0.0


def _extract_json_content(response: Any) -> Any:
    try:
        content = response["choices"][0]["message"]["content"]
    except (KeyError, IndexError, TypeError) as exc:
        raise ValueError("response does not contain choices[0].message.content") from exc

    if isinstance(content, (dict, list)):
        return content
    if not isinstance(content, str):
        raise ValueError("message content is not JSON text")

    text = content.strip()
    if text.startswith("```") and text.endswith("```"):
        lines = text.splitlines()
        if len(lines) >= 3:
            text = "\n".join(lines[1:-1]).strip()
            if text.lower().startswith("json\n"):
                text = text[5:].lstrip()
    return json.loads(text)


class ModelRouter:
    """Route JSON chat requests through an ordered pool of providers."""

    def __init__(
        self,
        providers: Mapping[str, Sequence[Provider]] | None = None,
        *,
        environ: Mapping[str, str] | None = None,
        opener: Callable[..., Any] | None = None,
        sleep: Callable[[float], None] = time.sleep,
        now: Callable[[], float] = time.time,
        timeout: float = DEFAULT_TIMEOUT_SECONDS,
        max_retry_delay: float = DEFAULT_MAX_RETRY_DELAY_SECONDS,
    ) -> None:
        loaded = load_provider_pool(environ) if providers is None else providers
        self.providers = {
            role: list(loaded.get(role, ())) for role in ("writer", "reviewer")
        }
        self._opener = opener or urllib.request.urlopen
        self._sleep = sleep
        self._now = now
        self.timeout = timeout
        self.max_retry_delay = max(0.0, max_retry_delay)
        self._open_circuits: dict[str, set[tuple[str, str, str]]] = {
            "writer": set(),
            "reviewer": set(),
        }

    def chat_json(
        self,
        role: str,
        messages: Sequence[Mapping[str, Any]],
        *,
        temperature: float = 0.2,
        response_format: Mapping[str, Any] | None = None,
        extra_body: Mapping[str, Any] | None = None,
    ) -> Any:
        """Return parsed JSON from the first provider that succeeds.

        Authentication errors open that provider's circuit immediately.  A
        429, 5xx, timeout, or network failure gets at most one short retry; if
        its requested wait is too long, the router switches providers instead.
        """

        if role not in ("writer", "reviewer"):
            raise ValueError("role must be 'writer' or 'reviewer'")
        providers = self.providers.get(role, [])
        if not providers:
            raise ProviderConfigurationError(f"No providers configured for {role}")

        failures: list[str] = []
        for provider in providers:
            circuit_key = (provider.name, provider.endpoint, provider.model)
            if circuit_key in self._open_circuits[role]:
                failures.append(f"{provider.name}: circuit open")
                continue
            body: dict[str, Any] = {
                "model": provider.model,
                "messages": list(messages),
                "temperature": temperature,
            }
            if response_format is not None:
                body["response_format"] = dict(response_format)
            if extra_body:
                body.update(extra_body)
            encoded = json.dumps(body, ensure_ascii=False).encode("utf-8")

            # Keep the safety bound even for Provider objects supplied directly
            # by callers instead of parsed from AI_PROVIDER_POOL_JSON.
            attempt_limit = min(max(1, provider.max_attempts), 2)
            for attempt in range(attempt_limit):
                request = urllib.request.Request(
                    provider.endpoint,
                    data=encoded,
                    headers={
                        "Authorization": f"Bearer {provider.api_key}",
                        "Content-Type": "application/json",
                        "Accept": "application/json",
                        "User-Agent": USER_AGENT,
                    },
                    method="POST",
                )
                try:
                    with self._opener(request, timeout=self.timeout) as response:
                        payload = json.loads(response.read().decode("utf-8"))
                    return _extract_json_content(payload)
                except urllib.error.HTTPError as exc:
                    status = int(exc.code)
                    if status in (401, 403):
                        failures.append(f"{provider.name}: authentication ({status})")
                        self._open_circuits[role].add(circuit_key)
                        break
                    retryable = status == 429 or 500 <= status <= 599
                    label = "rate limit" if status == 429 else f"HTTP {status}"
                    if not retryable:
                        failures.append(f"{provider.name}: {label}")
                        break

                    retry_after = _retry_after_seconds(
                        exc.headers.get("Retry-After") if exc.headers else None,
                        self._now,
                    )
                    can_retry = attempt + 1 < attempt_limit
                    if retry_after > self.max_retry_delay:
                        can_retry = False
                    if can_retry:
                        if retry_after > 0:
                            self._sleep(retry_after)
                        continue
                    failures.append(f"{provider.name}: {label}")
                    self._open_circuits[role].add(circuit_key)
                    break
                except (urllib.error.URLError, TimeoutError, OSError):
                    if attempt + 1 < attempt_limit:
                        continue
                    failures.append(f"{provider.name}: network failure")
                    self._open_circuits[role].add(circuit_key)
                    break
                except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
                    failures.append(f"{provider.name}: invalid JSON response")
                    self._open_circuits[role].add(circuit_key)
                    break

        detail = "; ".join(failures) or "unknown provider failure"
        raise ModelRouterError(f"All {role} providers failed: {detail}")


def chat_json(
    role: str,
    messages: Sequence[Mapping[str, Any]],
    **kwargs: Any,
) -> Any:
    """Convenience wrapper using providers loaded from the process environment."""

    return ModelRouter().chat_json(role, messages, **kwargs)


__all__ = [
    "ModelRouter",
    "ModelRouterError",
    "Provider",
    "ProviderConfigurationError",
    "chat_json",
    "load_provider_pool",
]
