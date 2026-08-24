# Report template REST API

Программный доступ к схемам и шаблонам отчётов. Дополняет админский веб-интерфейс: те же данные,
но в JSON и под токеном OzmaDB, без OIDC-редиректов.

## Аутентификация

Все запросы требуют заголовок с access-токеном OzmaDB:

```
Authorization: Bearer <ozmadb access token>
```

Собственной модели прав у отчётного генератора нет: он спрашивает `/permissions` у OzmaDB нужного
инстанса и требует признак `is_root`. Запросы из браузера с cookie-сессией админки тоже работают –
токен в этом случае берётся из клейма `access_token`.

Базовый адрес зависит от развёртывания. В типовой инсталляции отчётный генератор стоит рядом с OzmaDB:

```
https://<host>/report-generator/
```

## Ошибки

Любая ошибка возвращается как JSON:

```json
{"error": "forbidden", "message": "User has no admin rights for instance gogol"}
```

| Статус | `error` | Когда |
|---|---|---|
| 401 | `unauthorized` | токена нет, он невалиден, или OzmaDB ответил 401 |
| 403 | `forbidden` | токен валиден, но у пользователя нет прав администратора |
| 404 | `not_found` | инстанс, схема, шаблон или запрос не найдены |
| 400 | `bad_request` | невалидный ODT, не хватает полей формы, нет имени инстанса в роуте |
| 500 | `internal` | всё остальное |

## Инстансы

### GET /api/instances

Возвращает инстансы, для которых у вызывающего есть права администратора. Если в конфигурации задан
`OzmaDBSettings:ForceInstance`, возвращается только он. Отсутствие прав даёт пустой список, а не 403 –
чтобы не раскрывать состав инсталляции.

```bash
curl -s https://ozma.gogol.school/report-generator/api/instances \
  -H "Authorization: Bearer $TOKEN"
```

```json
{"instances": ["gogol"]}
```

## Схемы шаблонов

### GET /api/{instance}/schemas

```json
[{"id": 7, "name": "fin"}, {"id": 8, "name": "hr"}]
```

### POST /api/{instance}/schemas

Тело: `{"name": "ops"}`. Из имени удаляются пробелы, слэши и двойные подчёркивания.

```bash
curl -s -X POST https://ozma.gogol.school/report-generator/api/gogol/schemas \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name": "ops"}'
```

```json
{"id": 9, "name": "ops"}
```

Если инстанс ещё не зарегистрирован в базе отчётного генератора, он создаётся здесь же – так же,
как это делает админка при первом открытии. Повторное имя схемы даёт 400 `bad_request`.

### DELETE /api/{instance}/schemas/{id}

Удаляет схему вместе со всеми её шаблонами (каскад в БД). Ответ – пустой 200.

## Шаблоны

### GET /api/{instance}/templates?schema=fin

Параметр `schema` необязателен.

```json
[
  {"id": 12, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 2}
]
```

### GET /api/{instance}/templates/{id}

Метаданные шаблона вместе с текстами запросов.

```json
{
  "id": 12,
  "name": "invoice",
  "schemaId": 7,
  "schemaName": "fin",
  "queries": [
    {"id": 45, "name": "hdr", "type": "SingleRow", "queryText": "{ $id int }: select num as number from public.inv"},
    {"id": 46, "name": "rows", "type": "ManyRows", "queryText": "select s as sum from public.lines"}
  ]
}
```

Типы запросов: `SingleValue`, `SingleRow`, `ManyRows`.

### GET /api/{instance}/templates/{id}/file

Отдаёт ODT с восстановленными блоками `<query>` – ровно тот файл, который был загружен.
В базе документ хранится без запросов, они вклеиваются обратно при выгрузке.

```bash
curl -s https://ozma.gogol.school/report-generator/api/gogol/templates/12/file \
  -H "Authorization: Bearer $TOKEN" -o invoice.odt
```

### POST /api/{instance}/templates

`multipart/form-data`: `schemaName`, `name`, `file`.

```bash
curl -s -X POST https://ozma.gogol.school/report-generator/api/gogol/templates \
  -H "Authorization: Bearer $TOKEN" \
  -F schemaName=fin -F name=invoice -F file=@invoice.odt
```

```json
{"id": 12, "schemaId": 7, "schemaName": "fin", "name": "invoice", "queryCount": 2}
```

Имя шаблона уникально внутри схемы: повтор даёт 400 `bad_request`. Невалидный ODT – тоже 400,
с текстом ошибки разбора.

### PUT /api/{instance}/templates/{id}/file

`multipart/form-data`: `file`. Заменяет документ и полностью перечитывает из него запросы.
Предыдущая версия не сохраняется.

### DELETE /api/{instance}/templates/{id}

Удаляет шаблон вместе с его запросами. Ответ – пустой 200.

## Запросы шаблона

### PUT /api/{instance}/templates/{id}/queries/{queryId}

Меняет текст одного запроса, не трогая ODT.

```bash
curl -s -X PUT https://ozma.gogol.school/report-generator/api/gogol/templates/12/queries/45 \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"queryText": "{ $id int }: select num as number from public.invoices"}'
```

```json
{"id": 45, "name": "hdr", "type": "SingleRow", "queryText": "{ $id int }: select num as number from public.invoices"}
```

Меняется только текст. Имя запроса неизменяемо: на него ссылаются выражения `{{name.field}}` в теле
документа, которое API не правит. Тип запроса тоже неизменяем – он должен соответствовать способу
использования в документе.

## Анализ

### POST /api/{instance}/templates/{id}/analyze

Разбирает шаблон, не выполняя запросов: что он тянет, что подставляет и что в нём не сходится.

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
      "queryText": "{ $id int }: select s as sum from public.lines"
    },
    {
      "name": "header",
      "type": "SingleRow",
      "kind": "namedView",
      "namedView": {"schema": "fin", "name": "invoice_header"},
      "parameters": [],
      "queryText": "/views/fin/invoice_header"
    }
  ],
  "expressions": [
    {"queryName": "rows", "impliedType": "ManyRows", "subQueryName": "row", "fields": ["sum"]},
    {"queryName": "header", "impliedType": "SingleRow", "subQueryName": null, "fields": ["number"]}
  ],
  "findings": [
    {
      "severity": "error",
      "code": "unknown_query",
      "queryName": "totals",
      "field": null,
      "message": "Expression references query 'totals' which is not defined in the template"
    }
  ]
}
```

Коды находок:

| Код | Уровень | Условие |
|---|---|---|
| `unknown_query` | error | выражение ссылается на запрос, которого нет в шаблоне |
| `query_type_mismatch` | error | тип запроса не совпадает с использованием (`ManyRows` без цикла и наоборот) |
| `unused_query` | warning | запрос определён, но ни одно выражение на него не ссылается |
| `no_expressions` | error | в шаблоне нет ни одного выражения `{{ }}` |
| `duplicate_query_name` | error | два запроса с одинаковым именем |

Проверку имён колонок отчётный генератор не делает – для этого нужно выполнить запрос. Её выполняет
MCP-сервер поверх этого ответа, обращаясь к OzmaDB.

## Генерация

Существующий роут генерации не менялся:

```
GET /api/{instance}/{schema}/{template}/generate/{fileName}.{format}
```

Форматы: `odt`, `pdf`, `html`, `txt`. Параметры отчёта передаются в query string. Для проверки шаблона
после правки удобен `txt`:

```bash
curl -s "https://ozma.gogol.school/report-generator/api/gogol/fin/invoice/generate/preview.txt?id=42" \
  -H "Authorization: Bearer $TOKEN"
```
