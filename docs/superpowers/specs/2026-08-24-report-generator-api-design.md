# REST API отчётного генератора и инструменты MCP для работы с шаблонами

Дата: 2026-08-24
Статус: одобрено к реализации

## Контекст

Сейчас управление шаблонами отчётов доступно только через админский веб-интерфейс:
`AdminController` отдаёт HTML-партиалы под cookie-аутентификацией OIDC, а единственный
машинный эндпоинт – генерация отчёта
(`GET /api/{instance}/{schema}/{template}/generate/{file}.{format}` в `GenerateController`).

Задача: дать программный доступ к шаблонам, чтобы агент через MCP мог их выгружать,
править и анализировать. Работа затрагивает два репозитория:

- `ozma-report-generator` (этот) – новый REST API;
- `OzmaMCPExternal` (`~/PythonProjects/OzmaMCPExternal`) – новые MCP-инструменты поверх этого API.

### Принятые решения

**API живёт в отчётном генераторе, а не в ozmadb.** Три причины:

1. Топология обратная. RG мультитенантный: одна инсталляция обслуживает много инстансов
   (таблица `Instances`, `{instanceName}` во всех роутах, `OzmaDBSettings:DatabaseServerUrl`
   с плейсхолдером `{instanceName}`). Инстанс ozmadb – наоборот, один на базу. API внутри
   ozmadb всё равно ходил бы в RG за данными, то есть тот же API пришлось бы писать дважды.
2. Авторизация уже общая. Собственной модели прав у RG нет: он берёт Bearer-токен и
   спрашивает у ozmadb `/check_access` и `/permissions`, `IsRoot` означает админа.
   Новый API использует тот же токен и ту же модель прав – новой аутентификации не нужно.
3. Данные и рендеринг живут в RG: ODT-байты и распарсенные запросы в отдельной базе,
   Sandwych.Reporting, unoconv. Перенос в F#-сервер – это миграция данных плюс порт ODT-движка.

Побочный эффект, который был аргументом против: у клиентов без RG эндпоинтов просто нет,
и MCP отдаёт внятную ошибку вместо мёртвых роутов в ozmadb.

**Глубина правок – файл целиком плюс отдельные запросы.** Правка текста самого документа
на сервере (замена выражений внутри ODT-XML) в объём не входит: ODT рвёт текст на span'ы,
надёжная замена нетривиальна.

**Истории версий в БД нет.** Схема БД RG не меняется; страховку обеспечивает MCP,
складывая текущее состояние шаблона в `backups/` перед каждой записью.

## Часть 1. REST API в отчётном генераторе

### Роуты

Всё под уже существующим префиксом `/api/{instanceName}/`. Коллизии с роутом генерации нет:
тот требует шесть сегментов с литералом `generate` и точкой в имени файла.

```
GET    /api/instances                             → инстансы, доступные вызывающему
GET    /api/{inst}/schemas                        → [{id, name}]
POST   /api/{inst}/schemas                        {name} → {id, name}
DELETE /api/{inst}/schemas/{id}

GET    /api/{inst}/templates?schema=<name>        → [{id, schemaId, schemaName, name, queryCount}]
GET    /api/{inst}/templates/{id}                 → метаданные + запросы
GET    /api/{inst}/templates/{id}/file            → ODT с восстановленными <query>
POST   /api/{inst}/templates                      multipart: schemaName, name, file
PUT    /api/{inst}/templates/{id}/file            multipart: file
DELETE /api/{inst}/templates/{id}
PUT    /api/{inst}/templates/{id}/queries/{qid}   {queryText} → правка текста запроса
POST   /api/{inst}/templates/{id}/analyze         → структурный разбор шаблона
```

Генерация не дублируется: dry-run идёт через существующий
`GET /api/{inst}/{schema}/{template}/generate/{name}.txt`.

### Почему правка запроса без файла корректна

В БД хранится ODT **без** запросов (`OdtWithoutQueries`), а тексты запросов – отдельной
таблицей `ReportTemplateQueries`. При скачивании `RestoreQueriesInOdt` вклеивает их обратно
в документ. Поэтому изменение `QueryText` в базе автоматически попадает в скачанный файл
и в генерацию, перезаливка ODT не требуется.

Меняется только текст запроса. Имя запроса менять нельзя: на него ссылаются выражения
`{{name.field}}` в теле документа, которое API не правит. Тип запроса тоже не меняется –
он должен соответствовать способу использования в документе.

### Контракты

`GET /api/{inst}/templates/{id}`:

```json
{
  "id": 12,
  "name": "invoice",
  "schemaId": 3,
  "schemaName": "fin",
  "queries": [
    {"id": 45, "name": "header", "type": "SingleRow", "queryText": "{ $id int }: select ..."}
  ]
}
```

`POST /api/{inst}/templates/{id}/analyze`:

```json
{
  "templateId": 12,
  "schemaName": "fin",
  "name": "invoice",
  "queries": [
    {
      "name": "rows",
      "type": "ManyRows",
      "kind": "funql",
      "namedView": null,
      "parameters": ["id"],
      "queryText": "..."
    },
    {
      "name": "header",
      "type": "SingleRow",
      "kind": "namedView",
      "namedView": {"schema": "fin", "name": "invoice_header"},
      "parameters": ["id"],
      "queryText": "/views/fin/invoice_header"
    }
  ],
  "expressions": [
    {"queryName": "rows", "impliedType": "ManyRows", "subQueryName": "row", "fields": ["sum", "name"]},
    {"queryName": "header", "impliedType": "SingleRow", "subQueryName": null, "fields": ["number"]}
  ],
  "findings": [
    {"severity": "error", "code": "unknown_query", "queryName": "totals", "field": null,
     "message": "Expression references query 'totals' which is not defined in the template"}
  ]
}
```

Коды findings, которые формирует RG:

| Код | Уровень | Условие |
|---|---|---|
| `unknown_query` | error | выражение ссылается на запрос, которого нет в шаблоне |
| `query_type_mismatch` | error | тип запроса не совпадает с подразумеваемым использованием (`ManyRows` без цикла и наоборот) |
| `unused_query` | warning | запрос определён, но ни одно выражение на него не ссылается |
| `no_expressions` | error | в шаблоне нет ни одного выражения `{{ }}` |
| `duplicate_query_name` | error | два запроса с одинаковым именем |

Проверки `unknown_query` и `query_type_mismatch` сейчас живут внутри `GenerateReport`
и падают эксепшеном в рантайме. Они выносятся в переиспользуемый анализатор: в режиме
анализа он собирает список, в режиме генерации по-прежнему бросает на первой ошибке.

Проверка полей (`{{query.field}}` против реальных колонок) на стороне RG невозможна без
выполнения запросов, поэтому её делает MCP через ozmadb (часть 2).

### Ошибки

JSON в стиле `GenerateController`: `{"error": "<code>", "message": "<text>"}`.

| Статус | Код | Когда |
|---|---|---|
| 401 | `unauthorized` | нет токена, токен невалиден, ozmadb ответил 401 |
| 403 | `forbidden` | токен валиден, но `IsRoot` не выставлен |
| 404 | `not_found` | инстанс, схема, шаблон или запрос не найдены |
| 400 | `bad_request` | невалидный ODT, отсутствующие поля формы, конфликт имён |
| 500 | `internal` | всё остальное |

### Авторизация

Атрибут-фильтр `[OzmaAdmin]` (реализует `IAsyncAuthorizationFilter`) вместо `[Authorize]` –
чтобы API-клиент с Bearer-токеном не получал OIDC-редирект.

Порядок работы: достать токен через существующий `TokenProcessor` (сначала заголовок
`Authorization`, иначе клейм `access_token` из cookie-identity – так API работает и из
браузера), взять `instanceName` из route values, спросить права у ozmadb, требовать `IsRoot`.

Для тестируемости проверка прав уезжает за интерфейс:

```csharp
public interface IOzmaPermissionsChecker
{
    Task<PermissionsResponse?> GetPermissions(HttpContext context, string instanceName);
}
```

Боевая реализация делает то же, что сейчас `AdminController.HasPermissionsForInstance`:
создаёт `TokenProcessor` и `OzmaDBApiConnector`. Регистрируется в `Startup.ConfigureServices`
как singleton. В тестах подменяется фейком.

`AdminController` переводится на тот же интерфейс, чтобы проверка прав была в одном месте.
Разметку и роуты админки не трогаем.

### GET /api/instances

Единственный эндпоинт без `{instanceName}` в пути, поэтому `[OzmaAdmin]` к нему неприменим.
Логика:

- если задан `OzmaDBSettings:ForceInstance` – возвращаем только его, проверив права по нему;
- иначе читаем имена из таблицы `Instances` и для каждого спрашиваем `/permissions`,
  оставляя те, где `IsRoot`. Инстансов в реальных инсталляциях единицы; результат
  проверки кэшируется в памяти на 60 секунд по ключу «инстанс + хэш токена»;
- ответ `{"instances": ["gogol"]}`; при полном отсутствии прав – пустой список и 200,
  чтобы не раскрывать, существуют ли инстансы вообще.

Для чтения таблицы нужен доступ к базе без привязки к инстансу: текущий `Repository`
требует `instanceName` в конструкторе и создаёт схему из `db/db.sql`, если таблиц нет.
Добавляется `InstanceRepository` с тем же поведением по созданию схемы, но без требования
конкретного инстанса.

### Общий код

Из `AddTemplate` и `UpdateTemplateFile` в `Services/TemplateService.cs` выносится
дублирующаяся цепочка: загрузить ODT из потока → `GetQueriesFromOdt` →
`RemoveQueriesFromOdt` → проверить конструктором `OdtTemplate` → сохранить байты и
список запросов. `AdminController` начинает звать этот сервис – это единственная правка
в коде админки, кроме перехода на `IOzmaPermissionsChecker`.

Анализатор – `Services/TemplateAnalyzer.cs`: принимает `OdfDocument` без запросов и список
`ReportTemplateQuery`, возвращает структуру для `/analyze`. `ReportTemplateFunctions.GenerateReport`
переводится на него для проверок `unknown_query` и `query_type_mismatch`, сохраняя
поведение «бросить исключение на первой ошибке».

### Что не меняется

- Схема БД (`db/db.sql`).
- Админский UI и его роуты.
- Роут генерации.
- Конфигурация в `appsettings` кроме отсутствия новых ключей: `ForceInstance`,
  `DatabaseServerUrl`, `AuthSettings` используются как есть.

## Часть 2. Инструменты MCP

Новый модуль `ozma_mcp/reports.py` в `OzmaMCPExternal`; `server.py` (6100 строк) не растим.
Определения инструментов и диспетчеризация подключаются из `list_tools` и `_dispatch`.

### Конфигурация подключения

**URL отчётного генератора:** заголовок `X-Ozma-Report-URL`, query-параметр
`ozma_report_url`, переменная окружения `OZMA_REPORT_URL` (для stdio) – в этом приоритете.
Если ничего не задано, выводим из `X-Ozma-URL` заменой хвоста `/api/` на `/report-generator/`.

Конвенция проверена на боевых стендах: `https://ozma.gogol.school/report-generator/`
и `https://crm.gelfand.dev/report-generator/` отвечают (Kestrel за Caddy,
`admin/{instance}/` даёт 302 на OIDC). Существующие конфигурации в `~/.claude.json`
и у codex продолжают работать без правок.

**Имя инстанса:** заголовок `X-Ozma-Instance`, env `OZMA_INSTANCE`, иначе `GET /api/instances`
один раз за сессию. Ровно один инстанс – берём молча; несколько – инструмент возвращает
ошибку с перечислением вариантов и требует явный параметр `instance`.

**Видимость инструментов.** `list_tools` статичен, а HTTP-транспорт stateless, поэтому
проверять доступность RG на каждом листинге дорого. Инструменты видны всегда; при обращении
к недоступному RG возвращается ошибка вида «report generator недоступен по адресу X».
Полностью скрыть – `OZMA_REPORT_DISABLED=1`.

### Набор инструментов

| Инструмент | Назначение |
|---|---|
| `list_report_schemas` | схемы шаблонов инстанса |
| `list_report_templates` | шаблоны, опционально фильтр по схеме |
| `get_report_template` | метаданные и все запросы шаблона |
| `download_report_template` | выгрузка ODT с восстановленными `<query>` |
| `upload_report_template` | создать шаблон или заменить файл существующего |
| `safe_update_report_query` | правка текста одного запроса |
| `analyze_report_template` | разбор шаблона плюс семантические проверки |
| `test_report_template` | пробная генерация с параметрами |
| `search_in_report_templates` | поиск по текстам запросов всех шаблонов инстанса |
| `create_report_schema` | создать схему |
| `delete_report_template` | удалить шаблон |

Все изменяющие инструменты проходят через существующий `_require_write()`.

`search_in_report_templates` серверного эндпоинта не требует: он берёт список шаблонов
и запрашивает каждый через `GET /templates/{id}`, затем ищет подстроку в текстах запросов.
Шаблонов в инстансе десятки, отдельный поисковый роут в API не нужен.

`test_report_template` дёргает существующий роут генерации с `format=txt` (конвертация
через unoconv, который уже используется для pdf и html) и возвращает текст результата,
обрезанный по общим лимитам вывода, либо сообщение об ошибке рендера.

### Передача файлов в удалённом режиме

MCP работает и по HTTP (сервер на другой машине), поэтому запись файла на диск помогает
только в stdio-режиме. Обе стороны симметричны:

- `download_report_template(..., out_path=None, as_base64=False)`: по умолчанию пишет файл
  и возвращает `{path, size_bytes, sha256}`; при `as_base64=True` возвращает содержимое
  в base64. Файлы больше 1 МБ в base64 не отдаются – возвращается ошибка с подсказкой
  использовать `out_path`.
- `upload_report_template(..., file_path=None, content_base64=None)`: принимает любой
  из двух источников, ровно один обязателен.

### safe_update_report_query

Полное зеркало `safe_update_view_query`: параметры `template`, `query_name`, режим
`from_text` + `to_text` либо `new_query`, плюс `replace_count`, `dry_run`,
`validate_before_commit`. Валидация – существующий `_tool_validate_funql`, то есть
проверяется реальный FunQL против ozmadb. Именованные вьюхи (`/views/{schema}/{name}`)
валидируются проверкой существования вьюхи, а не парсингом.

Ответ включает `occurrences`, `applied_replacements` (или `planned_replacements`
в `dry_run`), результат валидации и путь к бэкапу.

### Бэкапы

Перед `upload_report_template(mode="replace")` и перед любым не-`dry_run` вызовом
`safe_update_report_query` MCP скачивает текущее состояние в
`backups/report/{instance}/{schema}.{template}_{YYYYmmdd_HHMMSS}.odt` и рядом кладёт
`.queries.json` с текстами запросов. Путь возвращается в ответе инструмента.
В удалённом режиме бэкапы лежат на диске MCP-сервера – там же, где текущая папка `backups/`.

### Семантический анализ поверх структурного

`analyze_report_template` берёт результат `/analyze` у RG и дополняет его:

- каждый анонимный запрос гоняется через `_tool_validate_funql` – синтаксис и типы колонок;
- для именованных вьюх проверяется существование через `list_user_views` и берутся колонки
  через `list_view_columns`;
- поля из `{{query.field}}` сверяются с реальными колонками; несовпадение даёт finding
  `unknown_column` с перечислением похожих имён.

Итоговый JSON – объединение findings от RG и от MCP в одном списке.

## Часть 3. Проверка

### Отчётный генератор

Тестов в репозитории нет, поэтому добавляется проект `OzmaReportGenerator.Tests` (xUnit),
подключённый в `OzmaReportGenerator.sln`. Покрываем:

- round-trip запросов на фикстурном ODT: извлечь → удалить → восстановить → извлечь снова,
  тексты и типы совпадают;
- `TemplateService`: невалидный ODT даёт понятную ошибку, валидный – корректный набор запросов;
- `TemplateAnalyzer` на фикстурах: битая ссылка на запрос, несовпадение типа,
  неиспользуемый запрос, шаблон без выражений;
- `[OzmaAdmin]`: 401 без токена, 403 без `IsRoot`, проход при `IsRoot` – с фейковым
  `IOzmaPermissionsChecker`.

Фикстурные ODT генерируются кодом в тестах (минимальный OpenDocument-zip), чтобы не
тащить бинарники в репозиторий.

### MCP

pytest в стиле существующих `tests/`, без новых зависимостей: `httpx.MockTransport`
и `monkeypatch`. Покрываем:

- деривацию URL RG из `X-Ozma-URL` и приоритет явных заголовков;
- выбор инстанса: один – молча, несколько – ошибка со списком;
- создание бэкапа перед записью и его отсутствие при `dry_run`;
- `safe_update_report_query`: подсчёт вхождений, отказ при непрошедшей валидации;
- сборку findings при сверке полей с колонками.

### Ручная проверка

На `ozma.gogol.school`: список схем и шаблонов, выгрузка ODT, `analyze`, правка запроса
с `dry_run=true`, затем настоящая правка, генерация в txt и сверка результата.

## Порядок работ

1. RG: `TemplateService`, `TemplateAnalyzer`, `IOzmaPermissionsChecker`, `[OzmaAdmin]`,
   перевод `AdminController` и `GenerateReport` на них, тесты.
2. RG: `TemplateApiController` и `InstanceRepository`, тесты.
3. MCP: транспорт к RG (конфигурация, выбор инстанса, ошибки), тесты.
4. MCP: инструменты чтения и выгрузки, тесты.
5. MCP: запись, бэкапы, `safe_update_report_query`, тесты.
6. MCP: анализ и пробная генерация, тесты.
7. Документация: README обоих репозиториев, ручная проверка на боевом стенде.
