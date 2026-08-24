# MCP Report Template Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Научить MCP-сервер OzmaMCPExternal выгружать, править и анализировать шаблоны отчётов через REST API отчётного генератора.

**Architecture:** Новый модуль `ozma_mcp/reports.py` с собственным HTTP-клиентом поверх существующей сессии (тот же Bearer-токен ozmadb), подключаемый в `list_tools` и `_dispatch` сервера. Конфигурация RG приезжает заголовками с фолбэком на вывод из `X-Ozma-URL`, поэтому существующие конфигурации клиентов продолжают работать без правок. Перед любой записью инструмент сам делает бэкап текущего состояния шаблона.

**Tech Stack:** Python 3.11+, mcp SDK <2, httpx <1, pytest 9 (без pytest-asyncio – асинхронные проверки запускаются через `asyncio.run`).

**Spec:** `docs/superpowers/specs/2026-08-24-report-generator-api-design.md` (в репозитории `ozma-report-generator`)

**Зависимость:** этот план реализуется после `docs/superpowers/plans/2026-08-24-report-generator-api.md`; справочник по эндпоинтам – `docs/api.md` того же репозитория.

## Global Constraints

- Рабочая директория: `/Users/vientooscuro/PythonProjects/OzmaMCPExternal`. Интерпретатор – `.venv/bin/python`, тесты – `.venv/bin/python -m pytest`.
- Новых зависимостей не добавляем: только `httpx`, `mcp`, стандартная библиотека. Асинхронные тесты пишутся как обычные функции с `asyncio.run(...)` внутри.
- `ozma_mcp/server.py` уже 6100 строк: вся новая логика живёт в `ozma_mcp/reports.py`, в `server.py` допускаются только точки подключения.
- Существующие конфигурации клиентов (`~/.claude.json`, codex) не должны сломаться: все новые заголовки и переменные окружения необязательны, при их отсутствии работает вывод по конвенции.
- Ошибки инструментов возвращаются как исключения с JSON-строкой в сообщении – `call_tool` в `server.py` разберёт их через `_exception_payload` и отдаст клиенту как `{"error": ..., "type": ...}`.
- Изменяющие инструменты обязаны вызывать `_require_write()` до любой записи.
- Сообщения коммитов – на английском, одной строкой, без тела и без `Co-Authored-By`.

---

### Task 1: Конфигурация подключения к отчётному генератору

**Files:**
- Modify: `ozma_mcp/session.py`
- Modify: `ozma_mcp/app.py:30-42`
- Modify: `ozma_mcp/server.py:6125-6135`
- Create: `tests/test_report_config.py`

**Interfaces:**
- Produces:
  - `OzmaCredentials.report_url: str` и `OzmaCredentials.instance: str` (оба со значением по умолчанию `""`)
  - вывод `report_url` из `api_base` в `__post_init__`, если он не задан явно
  - `OzmaSession.report_request(method: str, path: str, _retry: bool = True, **kwargs) -> httpx.Response`

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/test_report_config.py`:

```python
# tests/test_report_config.py
import asyncio
import time

import httpx
import pytest

from ozma_mcp.session import OzmaCredentials, OzmaSession


def make_creds(**kwargs):
    base = dict(
        api_base="https://ozma.gogol.school/api/",
        auth_url="https://ozma.gogol.school/auth/realms/ozma/protocol/openid-connect/token",
        client_id="ozmadb",
        client_secret="secret",
        username="user@example.com",
        password="pass",
    )
    base.update(kwargs)
    return OzmaCredentials(**base)


def test_report_url_derived_from_api_base():
    creds = make_creds()
    assert creds.report_url == "https://ozma.gogol.school/report-generator/"


def test_report_url_derived_when_api_base_has_no_trailing_slash():
    creds = make_creds(api_base="https://crm.gelfand.dev/api")
    assert creds.report_url == "https://crm.gelfand.dev/report-generator/"


def test_explicit_report_url_wins_and_gets_trailing_slash():
    creds = make_creds(report_url="https://reports.example.com/rg")
    assert creds.report_url == "https://reports.example.com/rg/"


def test_report_url_empty_when_api_base_is_not_an_api_path():
    creds = make_creds(api_base="https://example.com/something/")
    assert creds.report_url == ""


def test_instance_defaults_to_empty():
    assert make_creds().instance == ""


def test_report_request_uses_report_base_and_bearer_token():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["auth"] = request.headers.get("authorization")
        return httpx.Response(200, json={"instances": ["gogol"]})

    session = OzmaSession(make_creds())
    session._access_token = "tok"
    session._token_exp = time.time() + 3600
    session._http_client = httpx.AsyncClient(transport=httpx.MockTransport(handler))

    response = asyncio.run(session.report_request("GET", "api/instances"))

    assert response.status_code == 200
    assert seen["url"] == "https://ozma.gogol.school/report-generator/api/instances"
    assert seen["auth"] == "Bearer tok"


def test_report_request_without_report_url_raises():
    session = OzmaSession(make_creds(api_base="https://example.com/something/"))
    session._access_token = "tok"
    session._token_exp = time.time() + 3600

    with pytest.raises(RuntimeError):
        asyncio.run(session.report_request("GET", "api/instances"))
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_config.py -q`
Expected: FAIL – `OzmaCredentials.__init__() got an unexpected keyword argument 'report_url'`.

- [ ] **Step 3: Расширить креды и сессию**

В `ozma_mcp/session.py` заменить класс `OzmaCredentials` на:

```python
@dataclass
class OzmaCredentials:
    api_base: str
    auth_url: str
    client_id: str
    client_secret: str
    username: str
    password: str
    # Report generator (optional): derived from api_base when not given.
    report_url: str = ""
    instance: str = ""

    def __post_init__(self):
        if not self.api_base.endswith("/"):
            self.api_base += "/"
        if not self.report_url and self.api_base.endswith("/api/"):
            self.report_url = self.api_base[: -len("api/")] + "report-generator/"
        if self.report_url and not self.report_url.endswith("/"):
            self.report_url += "/"
```

В тот же файл, в класс `OzmaSession`, после `http_post` добавить:

```python
    async def report_request(self, method: str, path: str, _retry: bool = True, **kwargs) -> httpx.Response:
        """Call the report generator with the same OzmaDB access token."""
        if not self.creds.report_url:
            raise RuntimeError("Report generator URL is not configured")
        await self.ensure_token()
        client = self._get_http_client()
        url = self.creds.report_url + path.lstrip("/")
        r = await client.request(method, url, headers=self.auth_headers(), **kwargs)
        if r.status_code == 401 and _retry:
            self._access_token = None  # force refresh
            await self.ensure_token()
            r = await client.request(method, url, headers=self.auth_headers(), **kwargs)
        return r
```

- [ ] **Step 4: Пробросить конфигурацию из транспортов**

В `ozma_mcp/app.py` в `_extract_credentials` добавить два поля в конструктор `OzmaCredentials` (после `password`):

```python
        report_url=get("x-ozma-report-url", "ozma_report_url"),
        instance=get("x-ozma-instance", "ozma_instance"),
```

В `ozma_mcp/server.py` в `_run_stdio` добавить в конструктор `OzmaCredentials`:

```python
        report_url=os.environ.get("OZMA_REPORT_URL", ""),
        instance=os.environ.get("OZMA_INSTANCE", ""),
```

- [ ] **Step 5: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_config.py -q`
Expected: PASS, семь тестов.

- [ ] **Step 6: Прогнать весь набор тестов**

Run: `.venv/bin/python -m pytest -q`
Expected: PASS, 102 существующих теста плюс новые.

- [ ] **Step 7: Коммит**

```bash
git add ozma_mcp/session.py ozma_mcp/app.py ozma_mcp/server.py tests/test_report_config.py
git commit -m "Add report generator connection settings"
```

---

### Task 2: Каркас модуля reports.py – клиент, ошибки, выбор инстанса

**Files:**
- Create: `ozma_mcp/reports.py`
- Create: `tests/report_helpers.py`
- Create: `tests/test_report_client.py`

**Interfaces:**
- Consumes: `OzmaSession.report_request`, `server.SESSION_CTX`.
- Produces:
  - `reports.ReportError(payload: dict)` – исключение, сообщение которого является JSON-строкой
  - `await reports._report_json(method: str, path: str, **kwargs) -> Any`
  - `await reports._report_bytes(method: str, path: str, **kwargs) -> tuple[bytes, str]` – содержимое и имя файла из `Content-Disposition`
  - `await reports.resolve_instance(explicit: Optional[str] = None) -> str`
  - `tests/report_helpers.py`: `make_session(handler, **cred_kwargs) -> OzmaSession` (устанавливает `SESSION_CTX`) и `json_route(routes) -> handler`

- [ ] **Step 1: Написать тестовый хелпер**

Создать `tests/report_helpers.py`:

```python
# tests/report_helpers.py
import time

import httpx

from ozma_mcp import server as mcp_server
from ozma_mcp.session import OzmaCredentials, OzmaSession


def make_session(handler, **cred_kwargs) -> OzmaSession:
    """Session whose HTTP calls are served by `handler`, with a pre-set access token."""
    creds_kwargs = dict(
        api_base="https://ozma.gogol.school/api/",
        auth_url="https://ozma.gogol.school/auth/token",
        client_id="ozmadb",
        client_secret="secret",
        username="user@example.com",
        password="pass",
    )
    creds_kwargs.update(cred_kwargs)
    session = OzmaSession(OzmaCredentials(**creds_kwargs))
    session._access_token = "tok"
    session._token_exp = time.time() + 3600
    session._http_client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
    mcp_server.SESSION_CTX.set(session)
    return session


def json_route(routes: dict, default_status: int = 404):
    """Build a MockTransport handler from a {(method, path): (status, payload)} mapping."""

    def handler(request: httpx.Request) -> httpx.Response:
        key = (request.method, request.url.path)
        if key not in routes:
            return httpx.Response(default_status, json={"error": "not_found", "message": str(request.url.path)})
        status, payload = routes[key]
        if isinstance(payload, (bytes, bytearray)):
            return httpx.Response(status, content=payload)
        return httpx.Response(status, json=payload)

    return handler
```

- [ ] **Step 2: Написать падающие тесты клиента**

Создать `tests/test_report_client.py`:

```python
# tests/test_report_client.py
import asyncio
import json

import httpx
import pytest

from ozma_mcp import reports
from tests.report_helpers import json_route, make_session


def test_resolve_instance_prefers_explicit_argument():
    make_session(json_route({}))
    assert asyncio.run(reports.resolve_instance("explicit")) == "explicit"


def test_resolve_instance_uses_credentials():
    make_session(json_route({}), instance="from-creds")
    assert asyncio.run(reports.resolve_instance()) == "from-creds"


def test_resolve_instance_queries_api_when_single_instance():
    calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(request.url.path)
        return httpx.Response(200, json={"instances": ["gogol"]})

    session = make_session(handler)
    assert asyncio.run(reports.resolve_instance()) == "gogol"
    assert calls == ["/report-generator/api/instances"]
    # Second call is served from the session cache.
    assert asyncio.run(reports.resolve_instance()) == "gogol"
    assert len(calls) == 1


def test_resolve_instance_raises_when_several_instances():
    make_session(json_route({("GET", "/report-generator/api/instances"): (200, {"instances": ["a", "b"]})}))

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.resolve_instance())

    payload = json.loads(str(excinfo.value))
    assert payload["type"] == "validation"
    assert payload["instances"] == ["a", "b"]


def test_resolve_instance_raises_when_no_instances():
    make_session(json_route({("GET", "/report-generator/api/instances"): (200, {"instances": []})}))

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.resolve_instance())

    assert json.loads(str(excinfo.value))["type"] == "not_found"


def test_report_json_maps_api_error():
    make_session(json_route({
        ("GET", "/report-generator/api/gogol/schemas"): (403, {"error": "forbidden", "message": "no rights"}),
    }))

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports._report_json("GET", "api/gogol/schemas"))

    payload = json.loads(str(excinfo.value))
    assert payload["type"] == "forbidden"
    assert payload["status"] == 403
    assert "no rights" in payload["error"]


def test_report_json_reports_unreachable_generator():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    make_session(handler)

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports._report_json("GET", "api/instances"))

    payload = json.loads(str(excinfo.value))
    assert payload["type"] == "unreachable"
    assert "report-generator" in payload["error"]


def test_report_json_reports_missing_configuration():
    make_session(json_route({}), api_base="https://example.com/other/")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports._report_json("GET", "api/instances"))

    assert json.loads(str(excinfo.value))["type"] == "config"
```

- [ ] **Step 3: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_client.py -q`
Expected: FAIL – `ModuleNotFoundError: No module named 'ozma_mcp.reports'`.

- [ ] **Step 4: Реализовать каркас модуля**

Создать `ozma_mcp/reports.py`:

```python
"""
Report generator tools for the OzmaDB MCP server.

Talks to the report generator REST API with the same OzmaDB access token the
rest of the server uses. The generator URL is either configured explicitly or
derived from the OzmaDB API base (`<host>/api/` -> `<host>/report-generator/`).
"""

import json
import os
import re
from typing import Any, Optional

import httpx

REPORT_DISABLED = os.environ.get("OZMA_REPORT_DISABLED", "").lower() in ("1", "true", "yes")

# Maps report generator error codes onto the `type` field used by other tools.
_ERROR_TYPES = {
    "unauthorized": "unauthorized",
    "forbidden": "forbidden",
    "not_found": "not_found",
    "bad_request": "validation",
    "internal": "internal",
}


class ReportError(Exception):
    """Carries a JSON payload so `_exception_payload` in server.py can unwrap it."""

    def __init__(self, payload: dict):
        super().__init__(json.dumps(payload, ensure_ascii=False))
        self.payload = payload


def _server():
    # Imported lazily: server.py imports this module.
    from ozma_mcp import server

    return server


def _session():
    return _server()._session()


async def _report_request(method: str, path: str, **kwargs) -> httpx.Response:
    if REPORT_DISABLED:
        raise ReportError({
            "error": "Report generator tools are disabled by OZMA_REPORT_DISABLED",
            "type": "disabled",
        })
    session = _session()
    if not session.creds.report_url:
        raise ReportError({
            "error": (
                "Report generator URL is not configured. Pass the `X-Ozma-Report-URL` header "
                "(or OZMA_REPORT_URL for stdio); it is derived automatically only when the OzmaDB "
                "API base ends with `/api/`."
            ),
            "type": "config",
        })
    try:
        return await session.report_request(method, path, **kwargs)
    except httpx.RequestError as e:
        raise ReportError({
            "error": f"Report generator is unreachable at {session.creds.report_url}: {e}",
            "type": "unreachable",
        })


def _error_payload(response: httpx.Response) -> dict:
    code = "internal"
    message = response.text[:500]
    try:
        body = response.json()
        if isinstance(body, dict):
            code = body.get("error", code)
            message = body.get("message", message)
    except Exception:
        pass
    return {
        "error": message,
        "type": _ERROR_TYPES.get(code, code or "internal"),
        "status": response.status_code,
    }


async def _report_json(method: str, path: str, **kwargs) -> Any:
    response = await _report_request(method, path, **kwargs)
    if response.status_code >= 400:
        raise ReportError(_error_payload(response))
    if not response.content:
        return {}
    try:
        return response.json()
    except Exception:
        return {"raw": response.text}


async def _report_bytes(method: str, path: str, **kwargs) -> tuple[bytes, str]:
    response = await _report_request(method, path, **kwargs)
    if response.status_code >= 400:
        raise ReportError(_error_payload(response))
    disposition = response.headers.get("content-disposition", "")
    match = re.search(r'filename="?([^";]+)"?', disposition)
    filename = match.group(1) if match else "template.odt"
    return response.content, filename


async def resolve_instance(explicit: Optional[str] = None) -> str:
    if explicit:
        return explicit
    session = _session()
    if session.creds.instance:
        return session.creds.instance
    cached = session.cache_get("report:instance")
    if cached:
        return cached

    data = await _report_json("GET", "api/instances")
    instances = data.get("instances", []) if isinstance(data, dict) else []
    if len(instances) == 1:
        session.cache_set("report:instance", instances[0], ttl=600)
        return instances[0]
    if not instances:
        raise ReportError({
            "error": (
                "No report generator instances are available for this token. "
                "Pass `instance` explicitly or check admin rights in OzmaDB."
            ),
            "type": "not_found",
        })
    raise ReportError({
        "error": "Several instances are available, pass `instance` explicitly",
        "type": "validation",
        "instances": instances,
    })
```

- [ ] **Step 5: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_client.py -q`
Expected: PASS, восемь тестов.

- [ ] **Step 6: Коммит**

```bash
git add ozma_mcp/reports.py tests/report_helpers.py tests/test_report_client.py
git commit -m "Add report generator HTTP client and instance resolution"
```

---

### Task 3: Инструменты чтения и подключение к серверу

**Files:**
- Modify: `ozma_mcp/reports.py`
- Modify: `ozma_mcp/server.py` (в `list_tools` и `_dispatch`)
- Create: `tests/test_report_read_tools.py`

**Interfaces:**
- Consumes: `_report_json`, `resolve_instance`.
- Produces:
  - `reports.TOOL_NAMES: set[str]`
  - `reports.tool_defs() -> list[types.Tool]`
  - `await reports.dispatch(name: str, args: dict) -> Any`
  - `await reports.resolve_template(instance: str, template: Optional[str], schema: Optional[str], template_id: Optional[int]) -> dict` – возвращает summary шаблона (`id`, `name`, `schemaName`, ...)
  - инструменты `list_report_schemas`, `list_report_templates`, `get_report_template`, `search_in_report_templates`, `create_report_schema`

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/test_report_read_tools.py`:

```python
# tests/test_report_read_tools.py
import asyncio
import json

import pytest

from ozma_mcp import reports
from tests.report_helpers import json_route, make_session

TEMPLATES = [
    {"id": 1, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 2},
    {"id": 2, "schemaId": 7, "schemaName": "fin", "name": "act", "queryCount": 1},
    {"id": 3, "schemaId": 8, "schemaName": "hr", "name": "contract", "queryCount": 1},
]

TEMPLATE_ONE = {
    "id": 1,
    "name": "invoice",
    "schemaId": 7,
    "schemaName": "fin",
    "queries": [
        {"id": 10, "name": "hdr", "type": "SingleRow", "queryText": "select num as number from public.inv"},
        {"id": 11, "name": "rows", "type": "ManyRows", "queryText": "select s as sum from public.lines"},
    ],
}

TEMPLATE_THREE = {
    "id": 3,
    "name": "contract",
    "schemaId": 8,
    "schemaName": "hr",
    "queries": [
        {"id": 30, "name": "person", "type": "SingleRow", "queryText": "select name from public.people"},
    ],
}


def base_routes():
    return {
        ("GET", "/report-generator/api/gogol/schemas"): (200, [{"id": 7, "name": "fin"}, {"id": 8, "name": "hr"}]),
        ("GET", "/report-generator/api/gogol/templates"): (200, TEMPLATES),
        ("GET", "/report-generator/api/gogol/templates/1"): (200, TEMPLATE_ONE),
        ("GET", "/report-generator/api/gogol/templates/3"): (200, TEMPLATE_THREE),
    }


def test_tool_names_are_registered():
    assert "list_report_templates" in reports.TOOL_NAMES
    assert {t.name for t in reports.tool_defs()} == reports.TOOL_NAMES


def test_list_report_schemas():
    make_session(json_route(base_routes()), instance="gogol")
    result = asyncio.run(reports.dispatch("list_report_schemas", {}))
    assert result == [{"id": 7, "name": "fin"}, {"id": 8, "name": "hr"}]


def test_list_report_templates_filters_by_schema():
    make_session(json_route(base_routes()), instance="gogol")
    result = asyncio.run(reports.dispatch("list_report_templates", {"schema": "hr"}))
    assert [t["name"] for t in result] == ["contract"]


def test_get_report_template_by_name():
    make_session(json_route(base_routes()), instance="gogol")
    result = asyncio.run(reports.dispatch("get_report_template", {"template": "invoice"}))
    assert result["id"] == 1
    assert [q["name"] for q in result["queries"]] == ["hdr", "rows"]


def test_get_report_template_unknown_name_lists_candidates():
    make_session(json_route(base_routes()), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("get_report_template", {"template": "missing"}))

    payload = json.loads(str(excinfo.value))
    assert payload["type"] == "not_found"
    assert "invoice" in payload["error"]


def test_get_report_template_ambiguous_name_requires_schema():
    routes = base_routes()
    routes[("GET", "/report-generator/api/gogol/templates")] = (200, TEMPLATES + [
        {"id": 4, "schemaId": 8, "schemaName": "hr", "name": "invoice", "queryCount": 1},
    ])
    make_session(json_route(routes), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("get_report_template", {"template": "invoice"}))

    assert json.loads(str(excinfo.value))["type"] == "validation"


def test_search_in_report_templates_matches_query_text():
    make_session(json_route(base_routes()), instance="gogol")
    result = asyncio.run(reports.dispatch("search_in_report_templates", {"text": "public.lines"}))

    assert result["count"] == 1
    match = result["matches"][0]
    assert match["template"] == "invoice"
    assert match["query"] == "rows"


def test_create_report_schema_requires_write():
    session = make_session(json_route({
        ("POST", "/report-generator/api/gogol/schemas"): (200, {"id": 9, "name": "ops"}),
    }), instance="gogol")
    session.readonly = True

    with pytest.raises(PermissionError):
        asyncio.run(reports.dispatch("create_report_schema", {"name": "ops"}))
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_read_tools.py -q`
Expected: FAIL – у модуля `reports` нет атрибута `TOOL_NAMES`.

- [ ] **Step 3: Реализовать инструменты чтения**

Дописать в конец `ozma_mcp/reports.py`:

```python
# ---------------------------------------------------------------------------
# Template lookup
# ---------------------------------------------------------------------------


async def _list_templates(instance: str, schema: Optional[str] = None) -> list[dict]:
    params = {"schema": schema} if schema else None
    data = await _report_json("GET", f"api/{instance}/templates", params=params)
    return data if isinstance(data, list) else []


async def resolve_template(
    instance: str,
    template: Optional[str] = None,
    schema: Optional[str] = None,
    template_id: Optional[int] = None,
) -> dict:
    """Return the template summary addressed by id or by (schema, name)."""
    templates = await _list_templates(instance)
    if template_id is not None:
        for item in templates:
            if item.get("id") == template_id:
                return item
        raise ReportError({"error": f"Template with id={template_id} not found", "type": "not_found"})

    if not template:
        raise ReportError({"error": "Pass either `template` (name) or `template_id`", "type": "validation"})

    matches = [
        item for item in templates
        if item.get("name") == template and (not schema or item.get("schemaName") == schema)
    ]
    if len(matches) == 1:
        return matches[0]
    if not matches:
        known = ", ".join(f"{i.get('schemaName')}.{i.get('name')}" for i in templates) or "none"
        raise ReportError({
            "error": f"Template '{template}' not found. Known templates: {known}",
            "type": "not_found",
        })
    schemas = [item.get("schemaName") for item in matches]
    raise ReportError({
        "error": f"Template '{template}' exists in several schemas, pass `schema`",
        "type": "validation",
        "schemas": schemas,
    })


async def _get_template(instance: str, template_id: int) -> dict:
    return await _report_json("GET", f"api/{instance}/templates/{template_id}")


# ---------------------------------------------------------------------------
# Tools
# ---------------------------------------------------------------------------


async def _tool_list_report_schemas(instance: Optional[str]) -> Any:
    inst = await resolve_instance(instance)
    return await _report_json("GET", f"api/{inst}/schemas")


async def _tool_list_report_templates(schema: Optional[str], instance: Optional[str]) -> Any:
    inst = await resolve_instance(instance)
    return await _list_templates(inst, schema)


async def _tool_get_report_template(
    template: Optional[str],
    schema: Optional[str],
    template_id: Optional[int],
    instance: Optional[str],
) -> Any:
    inst = await resolve_instance(instance)
    summary = await resolve_template(inst, template, schema, template_id)
    return await _get_template(inst, summary["id"])


async def _tool_search_in_report_templates(text: str, instance: Optional[str]) -> Any:
    inst = await resolve_instance(instance)
    needle = text.lower()
    matches = []
    for summary in await _list_templates(inst):
        full = await _get_template(inst, summary["id"])
        for query in full.get("queries", []):
            query_text = query.get("queryText", "")
            if needle in query_text.lower():
                matches.append({
                    "template_id": full.get("id"),
                    "schema": full.get("schemaName"),
                    "template": full.get("name"),
                    "query": query.get("name"),
                    "query_id": query.get("id"),
                    "query_type": query.get("type"),
                    "excerpt": _excerpt(query_text, text),
                })
    return {"count": len(matches), "matches": matches}


def _excerpt(text: str, needle: str, context: int = 80) -> str:
    index = text.lower().find(needle.lower())
    if index < 0:
        return text[: context * 2]
    start = max(0, index - context)
    end = min(len(text), index + len(needle) + context)
    return ("..." if start > 0 else "") + text[start:end] + ("..." if end < len(text) else "")


async def _tool_create_report_schema(name: str, instance: Optional[str]) -> Any:
    _server()._require_write()
    inst = await resolve_instance(instance)
    return await _report_json("POST", f"api/{inst}/schemas", json={"name": name})
```

- [ ] **Step 4: Добавить определения инструментов и диспетчер**

Дописать в конец `ozma_mcp/reports.py`:

```python
# ---------------------------------------------------------------------------
# MCP wiring
# ---------------------------------------------------------------------------

_INSTANCE_ARG = {
    "instance": {
        "type": "string",
        "description": "Report generator instance name. Resolved automatically when omitted.",
    },
}

_TEMPLATE_ARGS = {
    "template": {"type": "string", "description": "Template name, e.g. `invoice`"},
    "schema": {"type": "string", "description": "Template schema, required only when the name is ambiguous"},
    "template_id": {"type": "integer", "description": "Template id, an alternative to `template`"},
}


def tool_defs() -> list:
    from mcp import types

    return [
        types.Tool(
            name="list_report_schemas",
            description="List report template schemas of the report generator instance.",
            inputSchema={"type": "object", "properties": dict(_INSTANCE_ARG)},
        ),
        types.Tool(
            name="list_report_templates",
            description="List report templates, optionally filtered by schema.",
            inputSchema={
                "type": "object",
                "properties": {
                    "schema": {"type": "string", "description": "Filter by template schema"},
                    **_INSTANCE_ARG,
                },
            },
        ),
        types.Tool(
            name="get_report_template",
            description="Get a report template with all of its FunQL queries.",
            inputSchema={"type": "object", "properties": {**_TEMPLATE_ARGS, **_INSTANCE_ARG}},
        ),
        types.Tool(
            name="search_in_report_templates",
            description="Search a substring across the FunQL queries of every template in the instance.",
            inputSchema={
                "type": "object",
                "properties": {
                    "text": {"type": "string", "description": "Substring to search for, case-insensitive"},
                    **_INSTANCE_ARG,
                },
                "required": ["text"],
            },
        ),
        types.Tool(
            name="create_report_schema",
            description="Create a new report template schema.",
            inputSchema={
                "type": "object",
                "properties": {
                    "name": {"type": "string", "description": "Schema name"},
                    **_INSTANCE_ARG,
                },
                "required": ["name"],
            },
        ),
    ]


TOOL_NAMES = {
    "list_report_schemas",
    "list_report_templates",
    "get_report_template",
    "search_in_report_templates",
    "create_report_schema",
}


async def dispatch(name: str, args: dict) -> Any:
    instance = args.get("instance")
    if name == "list_report_schemas":
        return await _tool_list_report_schemas(instance)
    if name == "list_report_templates":
        return await _tool_list_report_templates(args.get("schema"), instance)
    if name == "get_report_template":
        return await _tool_get_report_template(
            args.get("template"), args.get("schema"), args.get("template_id"), instance)
    if name == "search_in_report_templates":
        return await _tool_search_in_report_templates(args["text"], instance)
    if name == "create_report_schema":
        return await _tool_create_report_schema(args["name"], instance)
    raise ReportError({"error": f"Unknown report tool: {name}", "type": "validation"})
```

- [ ] **Step 5: Подключить модуль к серверу**

В `ozma_mcp/server.py`:

1. Рядом с `from ozma_mcp.session import OzmaSession, OzmaCredentials` добавить `from ozma_mcp import reports`.
2. В конце `list_tools` заменить `return _compact_tool_defs(tools)` на:

```python
    if not reports.REPORT_DISABLED:
        tools.extend(reports.tool_defs())
    return _compact_tool_defs(tools)
```

3. В начале `_dispatch`, перед `match name:`, добавить:

```python
    if name in reports.TOOL_NAMES:
        return await reports.dispatch(name, args)
```

- [ ] **Step 6: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_read_tools.py -q && .venv/bin/python -m pytest -q`
Expected: PASS, все тесты, включая восемь новых.

- [ ] **Step 7: Коммит**

```bash
git add ozma_mcp/reports.py ozma_mcp/server.py tests/test_report_read_tools.py
git commit -m "Add report template read tools"
```

---

### Task 4: Выгрузка, загрузка и бэкапы

**Files:**
- Modify: `ozma_mcp/reports.py`
- Create: `tests/test_report_file_tools.py`

**Interfaces:**
- Consumes: `resolve_template`, `_report_bytes`, `_report_json`.
- Produces:
  - `reports.backup_dir() -> Path` – `backups/report` в корне репозитория или `OZMA_REPORT_BACKUP_DIR`
  - `await reports.make_backup(instance: str, summary: dict) -> str` – путь к сохранённому `.odt`
  - инструменты `download_report_template`, `upload_report_template`, `delete_report_template`

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/test_report_file_tools.py`:

```python
# tests/test_report_file_tools.py
import asyncio
import base64
import json
from pathlib import Path

import httpx
import pytest

from ozma_mcp import reports
from tests.report_helpers import make_session

ODT_BYTES = b"PK\x03\x04fake-odt-content"

TEMPLATES = [{"id": 1, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 1}]
TEMPLATE_ONE = {
    "id": 1,
    "name": "invoice",
    "schemaId": 7,
    "schemaName": "fin",
    "queries": [{"id": 10, "name": "hdr", "type": "SingleRow", "queryText": "select 1 as a from public.x"}],
}


def handler_factory(recorder=None):
    def handler(request: httpx.Request) -> httpx.Response:
        path = request.url.path
        if recorder is not None:
            recorder.append((request.method, path, request.content))
        if path == "/report-generator/api/gogol/templates" and request.method == "GET":
            return httpx.Response(200, json=TEMPLATES)
        if path == "/report-generator/api/gogol/templates/1" and request.method == "GET":
            return httpx.Response(200, json=TEMPLATE_ONE)
        if path == "/report-generator/api/gogol/templates/1/file":
            if request.method == "GET":
                return httpx.Response(
                    200, content=ODT_BYTES,
                    headers={"content-disposition": 'attachment; filename="invoice.odt"'})
            return httpx.Response(200, json={"id": 1, "name": "invoice", "queryCount": 1})
        if path == "/report-generator/api/gogol/templates" and request.method == "POST":
            return httpx.Response(200, json={"id": 2, "name": "act", "schemaName": "fin", "queryCount": 0})
        if path == "/report-generator/api/gogol/templates/1" and request.method == "DELETE":
            return httpx.Response(200, json={})
        return httpx.Response(404, json={"error": "not_found", "message": path})

    return handler


def test_download_writes_file(tmp_path, monkeypatch):
    make_session(handler_factory(), instance="gogol")
    out = tmp_path / "invoice.odt"

    result = asyncio.run(reports.dispatch("download_report_template", {
        "template": "invoice", "out_path": str(out),
    }))

    assert out.read_bytes() == ODT_BYTES
    assert result["size_bytes"] == len(ODT_BYTES)
    assert result["path"] == str(out)


def test_download_as_base64():
    make_session(handler_factory(), instance="gogol")

    result = asyncio.run(reports.dispatch("download_report_template", {
        "template": "invoice", "as_base64": True,
    }))

    assert base64.b64decode(result["content_base64"]) == ODT_BYTES


def test_download_as_base64_rejects_large_files(monkeypatch):
    monkeypatch.setattr(reports, "MAX_BASE64_BYTES", 4)
    make_session(handler_factory(), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("download_report_template", {
            "template": "invoice", "as_base64": True,
        }))

    assert json.loads(str(excinfo.value))["type"] == "validation"


def test_upload_replace_makes_backup_first(tmp_path, monkeypatch):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path / "backups"))
    recorder = []
    make_session(handler_factory(recorder), instance="gogol")
    source = tmp_path / "new.odt"
    source.write_bytes(b"new-content")

    result = asyncio.run(reports.dispatch("upload_report_template", {
        "template": "invoice", "mode": "replace", "file_path": str(source),
    }))

    backup = Path(result["backup_path"])
    assert backup.exists()
    assert backup.read_bytes() == ODT_BYTES
    assert backup.with_suffix(".queries.json").exists()
    methods = [(m, p) for m, p, _ in recorder]
    assert ("GET", "/report-generator/api/gogol/templates/1/file") in methods
    assert ("PUT", "/report-generator/api/gogol/templates/1/file") in methods


def test_upload_create_does_not_need_existing_template(tmp_path, monkeypatch):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path / "backups"))
    make_session(handler_factory(), instance="gogol")
    source = tmp_path / "act.odt"
    source.write_bytes(b"content")

    result = asyncio.run(reports.dispatch("upload_report_template", {
        "name": "act", "schema": "fin", "mode": "create", "file_path": str(source),
    }))

    assert result["template"]["id"] == 2
    assert result.get("backup_path") is None


def test_upload_accepts_base64_content(tmp_path, monkeypatch):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path / "backups"))
    make_session(handler_factory(), instance="gogol")

    result = asyncio.run(reports.dispatch("upload_report_template", {
        "name": "act", "schema": "fin", "mode": "create",
        "content_base64": base64.b64encode(b"content").decode(),
    }))

    assert result["template"]["id"] == 2


def test_upload_requires_exactly_one_source(tmp_path):
    make_session(handler_factory(), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("upload_report_template", {
            "name": "act", "schema": "fin", "mode": "create",
        }))

    assert json.loads(str(excinfo.value))["type"] == "validation"


def test_upload_requires_write_mode():
    session = make_session(handler_factory(), instance="gogol")
    session.readonly = True

    with pytest.raises(PermissionError):
        asyncio.run(reports.dispatch("upload_report_template", {
            "name": "act", "schema": "fin", "mode": "create", "content_base64": "AA==",
        }))
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_file_tools.py -q`
Expected: FAIL – `download_report_template` неизвестен диспетчеру.

- [ ] **Step 3: Реализовать бэкапы и файловые инструменты**

Дописать в `ozma_mcp/reports.py` (перед секцией «MCP wiring»):

```python
# ---------------------------------------------------------------------------
# Backups and file transfer
# ---------------------------------------------------------------------------

MAX_BASE64_BYTES = 1024 * 1024


def backup_dir():
    from pathlib import Path

    override = os.environ.get("OZMA_REPORT_BACKUP_DIR")
    if override:
        return Path(override)
    return Path(__file__).resolve().parent.parent / "backups" / "report"


def _timestamp() -> str:
    from datetime import datetime

    return datetime.now().strftime("%Y%m%d_%H%M%S")


async def make_backup(instance: str, summary: dict) -> str:
    """Save the current ODT and its queries next to each other, return the ODT path."""
    from pathlib import Path

    content, _ = await _report_bytes("GET", f"api/{instance}/templates/{summary['id']}/file")
    full = await _get_template(instance, summary["id"])

    directory = Path(backup_dir()) / instance
    directory.mkdir(parents=True, exist_ok=True)
    stem = f"{summary.get('schemaName') or full.get('schemaName')}.{summary.get('name')}_{_timestamp()}"
    odt_path = directory / f"{stem}.odt"
    odt_path.write_bytes(content)
    (directory / f"{stem}.queries.json").write_text(
        json.dumps(full.get("queries", []), ensure_ascii=False, indent=2), encoding="utf-8")
    return str(odt_path)


async def _tool_download_report_template(
    template: Optional[str],
    schema: Optional[str],
    template_id: Optional[int],
    out_path: Optional[str],
    as_base64: bool,
    instance: Optional[str],
) -> Any:
    import base64 as _base64
    import hashlib
    from pathlib import Path

    inst = await resolve_instance(instance)
    summary = await resolve_template(inst, template, schema, template_id)
    content, filename = await _report_bytes("GET", f"api/{inst}/templates/{summary['id']}/file")
    digest = hashlib.sha256(content).hexdigest()

    if as_base64:
        if len(content) > MAX_BASE64_BYTES:
            raise ReportError({
                "error": (
                    f"Template is {len(content)} bytes, larger than the {MAX_BASE64_BYTES} byte inline limit. "
                    "Use `out_path` to write it to a file instead."
                ),
                "type": "validation",
            })
        return {
            "template": summary,
            "size_bytes": len(content),
            "sha256": digest,
            "content_base64": _base64.b64encode(content).decode(),
        }

    target = Path(out_path) if out_path else Path(backup_dir()) / inst / filename
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)
    return {"template": summary, "path": str(target), "size_bytes": len(content), "sha256": digest}


def _read_upload_source(file_path: Optional[str], content_base64: Optional[str]) -> bytes:
    import base64 as _base64
    from pathlib import Path

    if bool(file_path) == bool(content_base64):
        raise ReportError({
            "error": "Pass exactly one of `file_path` or `content_base64`",
            "type": "validation",
        })
    if file_path:
        path = Path(file_path)
        if not path.exists():
            raise ReportError({"error": f"File not found: {file_path}", "type": "not_found"})
        return path.read_bytes()
    try:
        return _base64.b64decode(content_base64, validate=True)
    except Exception as e:
        raise ReportError({"error": f"`content_base64` is not valid base64: {e}", "type": "validation"})


async def _tool_upload_report_template(args: dict) -> Any:
    _server()._require_write()
    inst = await resolve_instance(args.get("instance"))
    mode = args.get("mode", "replace")
    if mode not in ("create", "replace"):
        raise ReportError({"error": "`mode` must be 'create' or 'replace'", "type": "validation"})

    payload = _read_upload_source(args.get("file_path"), args.get("content_base64"))
    files = {"file": ("template.odt", payload, "application/vnd.oasis.opendocument.text")}

    if mode == "create":
        name = args.get("name")
        schema = args.get("schema")
        if not name or not schema:
            raise ReportError({"error": "`name` and `schema` are required to create a template", "type": "validation"})
        created = await _report_json(
            "POST", f"api/{inst}/templates",
            data={"schemaName": schema, "name": name}, files=files)
        return {"ok": True, "mode": "create", "template": created, "backup_path": None}

    summary = await resolve_template(inst, args.get("template"), args.get("schema"), args.get("template_id"))
    backup_path = await make_backup(inst, summary)
    updated = await _report_json("PUT", f"api/{inst}/templates/{summary['id']}/file", files=files)
    return {"ok": True, "mode": "replace", "template": updated, "backup_path": backup_path}


async def _tool_delete_report_template(
    template: Optional[str],
    schema: Optional[str],
    template_id: Optional[int],
    instance: Optional[str],
) -> Any:
    _server()._require_write()
    inst = await resolve_instance(instance)
    summary = await resolve_template(inst, template, schema, template_id)
    backup_path = await make_backup(inst, summary)
    await _report_json("DELETE", f"api/{inst}/templates/{summary['id']}")
    return {"ok": True, "deleted": summary, "backup_path": backup_path}
```

- [ ] **Step 4: Зарегистрировать новые инструменты**

В `tool_defs()` добавить три определения (перед `create_report_schema`):

```python
        types.Tool(
            name="download_report_template",
            description=(
                "Download a report template as ODT with its <query> blocks restored. "
                "Writes a file by default; pass as_base64 for inline content (1 MB limit)."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    **_TEMPLATE_ARGS,
                    "out_path": {"type": "string", "description": "Where to write the .odt file"},
                    "as_base64": {"type": "boolean", "description": "Return content inline instead of writing a file"},
                    **_INSTANCE_ARG,
                },
            },
        ),
        types.Tool(
            name="upload_report_template",
            description=(
                "Create a report template or replace the file of an existing one. "
                "Replacing backs the current template up first."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "mode": {"type": "string", "description": "`create` or `replace` (default `replace`)"},
                    "name": {"type": "string", "description": "Template name, required for `create`"},
                    **_TEMPLATE_ARGS,
                    "file_path": {"type": "string", "description": "Path to the .odt file to upload"},
                    "content_base64": {"type": "string", "description": "ODT content inline, base64"},
                    **_INSTANCE_ARG,
                },
            },
        ),
        types.Tool(
            name="delete_report_template",
            description="Delete a report template. Backs it up first.",
            inputSchema={"type": "object", "properties": {**_TEMPLATE_ARGS, **_INSTANCE_ARG}},
        ),
```

В `TOOL_NAMES` добавить `"download_report_template"`, `"upload_report_template"`, `"delete_report_template"`.

В `dispatch` добавить ветки:

```python
    if name == "download_report_template":
        return await _tool_download_report_template(
            args.get("template"), args.get("schema"), args.get("template_id"),
            args.get("out_path"), bool(args.get("as_base64")), instance)
    if name == "upload_report_template":
        return await _tool_upload_report_template(args)
    if name == "delete_report_template":
        return await _tool_delete_report_template(
            args.get("template"), args.get("schema"), args.get("template_id"), instance)
```

- [ ] **Step 5: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_file_tools.py -q && .venv/bin/python -m pytest -q`
Expected: PASS, все тесты.

- [ ] **Step 6: Исключить бэкапы отчётов из git**

Проверить `.gitignore`: если `backups/` там не игнорируется, добавить строку `backups/report/`.

- [ ] **Step 7: Коммит**

```bash
git add ozma_mcp/reports.py tests/test_report_file_tools.py .gitignore
git commit -m "Add report template download and upload tools"
```

---

### Task 5: safe_update_report_query

**Files:**
- Modify: `ozma_mcp/reports.py`
- Create: `tests/test_report_query_update.py`

**Interfaces:**
- Consumes: `resolve_template`, `_get_template`, `make_backup`, `server._tool_validate_funql`.
- Produces: инструмент `safe_update_report_query` с параметрами `template`/`template_id`, `schema`, `query_name`, `from_text`, `to_text`, `new_query`, `replace_count`, `dry_run`, `validate_before_commit`, `instance`.

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/test_report_query_update.py`:

```python
# tests/test_report_query_update.py
import asyncio
import json

import httpx
import pytest

from ozma_mcp import reports
from tests.report_helpers import make_session

TEMPLATES = [{"id": 1, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 1}]
QUERY_TEXT = "select num as number from public.inv where id = $id"
TEMPLATE_ONE = {
    "id": 1, "name": "invoice", "schemaId": 7, "schemaName": "fin",
    "queries": [{"id": 10, "name": "hdr", "type": "SingleRow", "queryText": QUERY_TEXT}],
}


def handler_factory(recorder):
    def handler(request: httpx.Request) -> httpx.Response:
        path = request.url.path
        recorder.append((request.method, path, request.content))
        if path == "/report-generator/api/gogol/templates" and request.method == "GET":
            return httpx.Response(200, json=TEMPLATES)
        if path == "/report-generator/api/gogol/templates/1" and request.method == "GET":
            return httpx.Response(200, json=TEMPLATE_ONE)
        if path == "/report-generator/api/gogol/templates/1/file" and request.method == "GET":
            return httpx.Response(200, content=b"odt", headers={"content-disposition": 'attachment; filename="invoice.odt"'})
        if path == "/report-generator/api/gogol/templates/1/queries/10" and request.method == "PUT":
            body = json.loads(request.content.decode())
            return httpx.Response(200, json={"id": 10, "name": "hdr", "type": "SingleRow", "queryText": body["queryText"]})
        return httpx.Response(404, json={"error": "not_found", "message": path})

    return handler


def patch_validation(monkeypatch, ok=True):
    async def fake_validate(query, params):
        return {"ok": ok, "columns": [{"name": "number"}]} if ok else {"ok": False, "error": "syntax error"}

    monkeypatch.setattr(reports._server(), "_tool_validate_funql", fake_validate)


def test_dry_run_does_not_write(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch)
    recorder = []
    make_session(handler_factory(recorder), instance="gogol")

    result = asyncio.run(reports.dispatch("safe_update_report_query", {
        "template": "invoice", "query_name": "hdr",
        "from_text": "public.inv", "to_text": "public.invoices",
        "dry_run": True,
    }))

    assert result["dry_run"] is True
    assert result["occurrences"] == 1
    assert result["planned_replacements"] == 1
    assert result.get("backup_path") is None
    assert not any(method == "PUT" for method, _, _ in recorder)


def test_partial_replace_writes_and_backs_up(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch)
    recorder = []
    make_session(handler_factory(recorder), instance="gogol")

    result = asyncio.run(reports.dispatch("safe_update_report_query", {
        "template": "invoice", "query_name": "hdr",
        "from_text": "public.inv", "to_text": "public.invoices",
    }))

    assert result["applied_replacements"] == 1
    assert result["query"]["queryText"].endswith("public.invoices where id = $id")
    assert result["backup_path"]
    assert any(method == "PUT" for method, _, _ in recorder)


def test_full_rewrite(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch)
    make_session(handler_factory([]), instance="gogol")

    result = asyncio.run(reports.dispatch("safe_update_report_query", {
        "template": "invoice", "query_name": "hdr",
        "new_query": "select 1 as number from public.inv",
    }))

    assert result["mode"] == "full_rewrite"
    assert result["query"]["queryText"] == "select 1 as number from public.inv"


def test_no_match_is_reported(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch)
    make_session(handler_factory([]), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("safe_update_report_query", {
            "template": "invoice", "query_name": "hdr",
            "from_text": "absent", "to_text": "x",
        }))

    assert json.loads(str(excinfo.value))["type"] == "no_match"


def test_invalid_funql_blocks_the_write(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch, ok=False)
    recorder = []
    make_session(handler_factory(recorder), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("safe_update_report_query", {
            "template": "invoice", "query_name": "hdr",
            "new_query": "select from",
        }))

    assert json.loads(str(excinfo.value))["type"] == "validation_failed"
    assert not any(method == "PUT" for method, _, _ in recorder)


def test_named_view_queries_skip_funql_validation(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch, ok=False)  # would fail if called
    make_session(handler_factory([]), instance="gogol")

    result = asyncio.run(reports.dispatch("safe_update_report_query", {
        "template": "invoice", "query_name": "hdr",
        "new_query": "/views/fin/invoice_header",
    }))

    assert result["validation"]["skipped"] is True


def test_unknown_query_name(monkeypatch, tmp_path):
    monkeypatch.setenv("OZMA_REPORT_BACKUP_DIR", str(tmp_path))
    patch_validation(monkeypatch)
    make_session(handler_factory([]), instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("safe_update_report_query", {
            "template": "invoice", "query_name": "missing", "new_query": "select 1 from public.x",
        }))

    payload = json.loads(str(excinfo.value))
    assert payload["type"] == "not_found"
    assert "hdr" in payload["error"]
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_query_update.py -q`
Expected: FAIL – `safe_update_report_query` неизвестен диспетчеру.

- [ ] **Step 3: Реализовать инструмент**

Дописать в `ozma_mcp/reports.py` (перед секцией «MCP wiring»):

```python
# ---------------------------------------------------------------------------
# Query editing
# ---------------------------------------------------------------------------


def _is_named_view(query_text: str) -> bool:
    return query_text.strip().startswith("/views/")


async def _tool_safe_update_report_query(args: dict) -> Any:
    _server()._require_write()
    inst = await resolve_instance(args.get("instance"))
    summary = await resolve_template(inst, args.get("template"), args.get("schema"), args.get("template_id"))
    full = await _get_template(inst, summary["id"])

    query_name = args.get("query_name")
    query = next((q for q in full.get("queries", []) if q.get("name") == query_name), None)
    if query is None:
        known = ", ".join(q.get("name", "") for q in full.get("queries", [])) or "none"
        raise ReportError({
            "error": f"Query '{query_name}' not found in template '{summary.get('name')}'. Known queries: {known}",
            "type": "not_found",
        })

    new_query = args.get("new_query")
    from_text = args.get("from_text")
    to_text = args.get("to_text")
    full_rewrite = new_query is not None
    if full_rewrite and (from_text is not None or to_text is not None):
        raise ReportError({
            "error": "Provide either `new_query` (full rewrite) or `from_text`+`to_text` (partial), not both",
            "type": "validation",
        })
    if not full_rewrite and (from_text is None or to_text is None):
        raise ReportError({
            "error": "Provide `from_text` and `to_text` for partial replace, or `new_query` for full rewrite",
            "type": "validation",
        })

    old_text = query.get("queryText", "")
    occurrences = None
    effective_count = None
    if full_rewrite:
        final_text = new_query
    else:
        occurrences = old_text.count(from_text)
        if occurrences == 0:
            raise ReportError({
                "error": f"No occurrences of the given text in query '{query_name}'",
                "type": "no_match",
                "query_name": query_name,
                "from_text": from_text,
            })
        replace_count = args.get("replace_count")
        effective_count = replace_count if replace_count is not None else occurrences
        final_text = old_text.replace(from_text, to_text, replace_count or -1)

    validate = args.get("validate_before_commit", True)
    if not validate or _is_named_view(final_text):
        validation: dict = {"ok": True, "skipped": True}
    else:
        validation = await _server()._tool_validate_funql(final_text, {})
        if not validation.get("ok", False):
            raise ReportError({
                "error": "Replacement produced invalid FunQL",
                "type": "validation_failed",
                "query_name": query_name,
                "validation": validation,
            })

    result: dict = {
        "ok": True,
        "template": summary,
        "query_name": query_name,
        "mode": "full_rewrite" if full_rewrite else "partial_replace",
        "validation": validation,
    }

    if args.get("dry_run"):
        result["dry_run"] = True
        result["backup_path"] = None
        result["new_query_text"] = final_text
        if not full_rewrite:
            result["occurrences"] = occurrences
            result["planned_replacements"] = effective_count
        return result

    backup_path = await make_backup(inst, summary)
    updated = await _report_json(
        "PUT", f"api/{inst}/templates/{summary['id']}/queries/{query['id']}",
        json={"queryText": final_text})

    result["dry_run"] = False
    result["backup_path"] = backup_path
    result["query"] = updated
    if not full_rewrite:
        result["occurrences"] = occurrences
        result["applied_replacements"] = effective_count
    return result
```

- [ ] **Step 4: Зарегистрировать инструмент**

В `tool_defs()` добавить:

```python
        types.Tool(
            name="safe_update_report_query",
            description=(
                "Update the FunQL text of a single template query. Mirrors safe_update_view_query: "
                "partial from_text/to_text replace or a full new_query rewrite, with dry_run and "
                "FunQL validation before the write. Backs the template up before committing."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    **_TEMPLATE_ARGS,
                    "query_name": {"type": "string", "description": "Name of the query inside the template"},
                    "from_text": {"type": "string", "description": "Substring to replace"},
                    "to_text": {"type": "string", "description": "Replacement text"},
                    "new_query": {"type": "string", "description": "Full replacement for the query text"},
                    "replace_count": {"type": "integer", "description": "Limit the number of replacements"},
                    "dry_run": {"type": "boolean", "description": "Validate and report without writing"},
                    "validate_before_commit": {"type": "boolean", "description": "Validate FunQL first (default true)"},
                    **_INSTANCE_ARG,
                },
                "required": ["query_name"],
            },
        ),
```

В `TOOL_NAMES` добавить `"safe_update_report_query"`, в `dispatch`:

```python
    if name == "safe_update_report_query":
        return await _tool_safe_update_report_query(args)
```

- [ ] **Step 5: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_query_update.py -q && .venv/bin/python -m pytest -q`
Expected: PASS, все тесты.

- [ ] **Step 6: Коммит**

```bash
git add ozma_mcp/reports.py tests/test_report_query_update.py
git commit -m "Add safe query update tool for report templates"
```

---

### Task 6: Анализ шаблона и пробная генерация

**Files:**
- Modify: `ozma_mcp/reports.py`
- Create: `tests/test_report_analysis.py`

**Interfaces:**
- Consumes: `_report_json`, `_report_request`, `server._tool_validate_funql`, `server._tool_list_view_columns`, `server._tool_list_user_views`.
- Produces: инструменты `analyze_report_template` и `test_report_template`.

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/test_report_analysis.py`:

```python
# tests/test_report_analysis.py
import asyncio
import json

import httpx
import pytest

from ozma_mcp import reports
from tests.report_helpers import make_session

TEMPLATES = [{"id": 1, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 2}]

ANALYSIS = {
    "templateId": 1,
    "schemaName": "fin",
    "name": "invoice",
    "queries": [
        {"name": "hdr", "type": "SingleRow", "kind": "funql", "namedView": None,
         "parameters": ["id"], "queryText": "select num as number from public.inv"},
        {"name": "ref", "type": "SingleRow", "kind": "namedView",
         "namedView": {"schema": "fin", "name": "invoice_header"},
         "parameters": [], "queryText": "/views/fin/invoice_header"},
    ],
    "expressions": [
        {"queryName": "hdr", "impliedType": "SingleRow", "subQueryName": None, "fields": ["number", "nomber"]},
        {"queryName": "ref", "impliedType": "SingleRow", "subQueryName": None, "fields": ["title"]},
    ],
    "findings": [
        {"severity": "warning", "code": "unused_query", "queryName": "orphan", "field": None,
         "message": "Query 'orphan' is never referenced by an expression"},
    ],
}


def handler(request: httpx.Request) -> httpx.Response:
    path = request.url.path
    if path == "/report-generator/api/gogol/templates" and request.method == "GET":
        return httpx.Response(200, json=TEMPLATES)
    if path == "/report-generator/api/gogol/templates/1/analyze" and request.method == "POST":
        return httpx.Response(200, json=ANALYSIS)
    if path == "/report-generator/api/gogol/fin/invoice/generate/preview.txt":
        return httpx.Response(200, text="INVOICE 42")
    return httpx.Response(404, json={"error": "not_found", "message": path})


def patch_ozma(monkeypatch, columns=None, views=None):
    server = reports._server()

    async def fake_validate(query, params):
        return {"ok": True, "columns": [{"name": "number"}]}

    async def fake_list_view_columns(schema, view_name):
        return {"columns": columns if columns is not None else [{"name": "title"}]}

    async def fake_list_user_views(schema_name=None, view_name_like=None):
        return views if views is not None else [{"schema": "fin", "name": "invoice_header"}]

    monkeypatch.setattr(server, "_tool_validate_funql", fake_validate)
    monkeypatch.setattr(server, "_tool_list_view_columns", fake_list_view_columns)
    monkeypatch.setattr(server, "_tool_list_user_views", fake_list_user_views)


def test_analysis_passes_through_server_findings(monkeypatch):
    patch_ozma(monkeypatch)
    make_session(handler, instance="gogol")

    result = asyncio.run(reports.dispatch("analyze_report_template", {"template": "invoice"}))

    codes = [f["code"] for f in result["findings"]]
    assert "unused_query" in codes


def test_analysis_flags_unknown_column(monkeypatch):
    patch_ozma(monkeypatch)
    make_session(handler, instance="gogol")

    result = asyncio.run(reports.dispatch("analyze_report_template", {"template": "invoice"}))

    unknown = [f for f in result["findings"] if f["code"] == "unknown_column"]
    assert any(f["field"] == "nomber" for f in unknown)
    assert all(f["field"] != "number" for f in unknown)


def test_analysis_flags_missing_named_view(monkeypatch):
    patch_ozma(monkeypatch, views=[])
    make_session(handler, instance="gogol")

    result = asyncio.run(reports.dispatch("analyze_report_template", {"template": "invoice"}))

    assert any(f["code"] == "unknown_named_view" for f in result["findings"])


def test_analysis_can_skip_ozma_validation(monkeypatch):
    patch_ozma(monkeypatch)
    make_session(handler, instance="gogol")

    result = asyncio.run(reports.dispatch("analyze_report_template", {
        "template": "invoice", "validate_queries": False,
    }))

    assert all(f["code"] != "unknown_column" for f in result["findings"])


def test_test_report_template_returns_text():
    make_session(handler, instance="gogol")

    result = asyncio.run(reports.dispatch("test_report_template", {
        "template": "invoice", "params": {"id": 42},
    }))

    assert result["ok"] is True
    assert "INVOICE 42" in result["text"]


def test_test_report_template_reports_generation_error():
    def failing(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/report-generator/api/gogol/templates":
            return httpx.Response(200, json=TEMPLATES)
        return httpx.Response(500, json={"Error": "internal", "Message": "Query hdr failed"})

    make_session(failing, instance="gogol")

    with pytest.raises(reports.ReportError) as excinfo:
        asyncio.run(reports.dispatch("test_report_template", {"template": "invoice"}))

    assert "Query hdr failed" in json.loads(str(excinfo.value))["error"]
```

- [ ] **Step 2: Запустить тесты и убедиться, что они падают**

Run: `.venv/bin/python -m pytest tests/test_report_analysis.py -q`
Expected: FAIL – `analyze_report_template` неизвестен диспетчеру.

- [ ] **Step 3: Реализовать инструменты**

Дописать в `ozma_mcp/reports.py` (перед секцией «MCP wiring»):

```python
# ---------------------------------------------------------------------------
# Analysis and test generation
# ---------------------------------------------------------------------------


def _similar_names(name: str, candidates: list[str], limit: int = 3) -> list[str]:
    import difflib

    return difflib.get_close_matches(name, candidates, n=limit, cutoff=0.6)


async def _query_columns(query: dict) -> tuple[Optional[list[str]], Optional[dict]]:
    """Return (column names, finding) for one analyzed query."""
    server = _server()
    if query.get("kind") == "namedView":
        ref = query.get("namedView") or {}
        views = await server._tool_list_user_views(ref.get("schema"), ref.get("name"))
        exists = any(v.get("name") == ref.get("name") for v in views) if isinstance(views, list) else False
        if not exists:
            return None, {
                "severity": "error",
                "code": "unknown_named_view",
                "queryName": query.get("name"),
                "field": None,
                "message": f"Named view {ref.get('schema')}.{ref.get('name')} does not exist in OzmaDB",
            }
        info = await server._tool_list_view_columns(ref.get("schema"), ref.get("name"))
        columns = [c.get("name") for c in info.get("columns", [])] if isinstance(info, dict) else []
        return columns, None

    validation = await server._tool_validate_funql(query.get("queryText", ""), {})
    if not validation.get("ok", False):
        return None, {
            "severity": "error",
            "code": "invalid_funql",
            "queryName": query.get("name"),
            "field": None,
            "message": f"Query '{query.get('name')}' is not valid FunQL: {validation.get('error', 'unknown error')}",
        }
    columns = [c.get("name") for c in validation.get("columns", [])]
    return columns, None


async def _tool_analyze_report_template(args: dict) -> Any:
    inst = await resolve_instance(args.get("instance"))
    summary = await resolve_template(inst, args.get("template"), args.get("schema"), args.get("template_id"))
    analysis = await _report_json("POST", f"api/{inst}/templates/{summary['id']}/analyze")

    findings = list(analysis.get("findings", []))
    if args.get("validate_queries", True):
        expressions = analysis.get("expressions", [])
        for query in analysis.get("queries", []):
            columns, finding = await _query_columns(query)
            if finding is not None:
                findings.append(finding)
                continue
            if columns is None:
                continue
            query["columns"] = columns
            for expression in expressions:
                if expression.get("queryName") != query.get("name"):
                    continue
                for field in expression.get("fields", []):
                    if field in columns:
                        continue
                    hint = _similar_names(field, columns)
                    message = f"Query '{query.get('name')}' has no column '{field}'"
                    if hint:
                        message += f". Did you mean: {', '.join(hint)}?"
                    findings.append({
                        "severity": "error",
                        "code": "unknown_column",
                        "queryName": query.get("name"),
                        "field": field,
                        "message": message,
                    })

    analysis["findings"] = findings
    analysis["errors"] = len([f for f in findings if f.get("severity") == "error"])
    analysis["warnings"] = len([f for f in findings if f.get("severity") == "warning"])
    return analysis


async def _tool_test_report_template(args: dict) -> Any:
    inst = await resolve_instance(args.get("instance"))
    summary = await resolve_template(inst, args.get("template"), args.get("schema"), args.get("template_id"))
    params = args.get("params") or {}
    query_params = {k: str(v) for k, v in params.items()}

    path = f"api/{inst}/{summary.get('schemaName')}/{summary.get('name')}/generate/preview.txt"
    response = await _report_request("GET", path, params=query_params)
    if response.status_code >= 400:
        message = response.text[:1000]
        try:
            body = response.json()
            if isinstance(body, dict):
                message = body.get("Message") or body.get("message") or message
        except Exception:
            pass
        raise ReportError({
            "error": message,
            "type": "generation_failed",
            "status": response.status_code,
            "template": summary,
        })

    return {"ok": True, "template": summary, "params": params, "text": response.text}
```

- [ ] **Step 4: Зарегистрировать инструменты**

В `tool_defs()` добавить:

```python
        types.Tool(
            name="analyze_report_template",
            description=(
                "Analyze a report template: its queries, {{ }} expressions and loops, plus findings "
                "such as unknown queries, type mismatches, unused queries and columns that do not exist."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    **_TEMPLATE_ARGS,
                    "validate_queries": {
                        "type": "boolean",
                        "description": "Validate FunQL and column names against OzmaDB (default true)",
                    },
                    **_INSTANCE_ARG,
                },
            },
        ),
        types.Tool(
            name="test_report_template",
            description="Render a report template to plain text with the given parameters, to check a change end to end.",
            inputSchema={
                "type": "object",
                "properties": {
                    **_TEMPLATE_ARGS,
                    "params": {"type": "object", "description": "Report parameters, e.g. {\"id\": 42}"},
                    **_INSTANCE_ARG,
                },
            },
        ),
```

В `TOOL_NAMES` добавить `"analyze_report_template"` и `"test_report_template"`, в `dispatch`:

```python
    if name == "analyze_report_template":
        return await _tool_analyze_report_template(args)
    if name == "test_report_template":
        return await _tool_test_report_template(args)
```

- [ ] **Step 5: Запустить тесты**

Run: `.venv/bin/python -m pytest tests/test_report_analysis.py -q && .venv/bin/python -m pytest -q`
Expected: PASS, все тесты.

- [ ] **Step 6: Коммит**

```bash
git add ozma_mcp/reports.py tests/test_report_analysis.py
git commit -m "Add report template analysis and test generation tools"
```

---

### Task 7: Документация

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: список инструментов из задач 3–6.

- [ ] **Step 1: Описать инструменты в README**

Добавить в `README.md` раздел «Report generator tools» с таблицей всех одиннадцати инструментов
(`list_report_schemas`, `list_report_templates`, `get_report_template`, `search_in_report_templates`,
`create_report_schema`, `download_report_template`, `upload_report_template`, `delete_report_template`,
`safe_update_report_query`, `analyze_report_template`, `test_report_template`) и подразделом о настройке:

- `X-Ozma-Report-URL` / `ozma_report_url` / `OZMA_REPORT_URL` – адрес отчётного генератора; по умолчанию выводится
  из `X-Ozma-URL` заменой `/api/` на `/report-generator/`;
- `X-Ozma-Instance` / `OZMA_INSTANCE` – имя инстанса; по умолчанию берётся из `GET /api/instances`, если инстанс один;
- `OZMA_REPORT_DISABLED=1` – полностью скрыть инструменты отчётов;
- `OZMA_REPORT_BACKUP_DIR` – каталог бэкапов, по умолчанию `backups/report`.

Явно указать, что существующие конфигурации клиентов менять не требуется.

- [ ] **Step 2: Добавить раздел в AGENTS.md**

Дописать короткий раздел о работе с шаблонами: сначала `analyze_report_template`, правки запросов – через
`safe_update_report_query` с `dry_run=true`, полная замена файла – через `download_report_template` →
локальная правка → `upload_report_template`, проверка результата – `test_report_template`.

- [ ] **Step 3: Коммит**

```bash
git add README.md AGENTS.md
git commit -m "Document report generator tools"
```

---

### Task 8: Проверка на боевом стенде

**Files:**
- Ничего не меняется; задача проверочная.

**Interfaces:**
- Consumes: развёрнутый API отчётного генератора на `ozma.gogol.school`.

- [ ] **Step 1: Убедиться, что API развёрнут**

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://ozma.gogol.school/report-generator/api/instances
```
Expected: 401 без токена. Если 404 – новая версия отчётного генератора ещё не выкачена, остальные шаги отложить.

- [ ] **Step 2: Прогнать инструменты через локальный stdio-сервер**

```bash
cd /Users/vientooscuro/PythonProjects/OzmaMCPExternal
OZMA_API_BASE=https://ozma.gogol.school/api/ \
OZMA_AUTH_URL=https://ozma.gogol.school/auth/realms/ozma/protocol/openid-connect/token \
OZMA_CLIENT_ID=ozmadb \
OZMA_CLIENT_SECRET="$(security find-generic-password -s ozma-client-secret -w 2>/dev/null || echo cKIu9citwiEBBJjZkMaKVoinzxGOb37h)" \
OZMA_USERNAME=vientooscuro@vientooscuro.ru \
OZMA_PASSWORD=Molnia01 \
.venv/bin/python - <<'PY'
import asyncio, os
from ozma_mcp import reports, server
from ozma_mcp.session import OzmaCredentials, OzmaSession

creds = OzmaCredentials(
    api_base=os.environ["OZMA_API_BASE"], auth_url=os.environ["OZMA_AUTH_URL"],
    client_id=os.environ["OZMA_CLIENT_ID"], client_secret=os.environ["OZMA_CLIENT_SECRET"],
    username=os.environ["OZMA_USERNAME"], password=os.environ["OZMA_PASSWORD"],
)
server.SESSION_CTX.set(OzmaSession(creds))

async def main():
    print("instances:", await reports._report_json("GET", "api/instances"))
    print("schemas:", await reports.dispatch("list_report_schemas", {}))
    templates = await reports.dispatch("list_report_templates", {})
    print("templates:", [t["name"] for t in templates])
    if templates:
        first = templates[0]
        print("analysis:", await reports.dispatch("analyze_report_template", {"template_id": first["id"]}))

asyncio.run(main())
PY
```
Expected: непустые списки, разбор первого шаблона без исключений.

- [ ] **Step 3: Проверить безопасную правку запроса**

Взять любой шаблон, выполнить `safe_update_report_query` с `dry_run=True` и заменой, которая заведомо
находится в тексте запроса (например пробел на пробел). Убедиться, что `occurrences` больше нуля,
`backup_path` равен `None`, и на стенде ничего не изменилось.

- [ ] **Step 4: Зафиксировать результаты**

Если по ходу проверки нашлись расхождения с планом – завести правки отдельными коммитами с тестами,
воспроизводящими проблему.
