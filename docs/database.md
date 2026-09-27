# SQLite-каталог

Источник истины схемы: `src/PhotoShelf.Infrastructure.Sqlite/Migrations/001_initial.sql`.

## Почему SQLite

Каталог принадлежит одному локальному пользователю и содержит индекс, а не оригиналы. SQLite даёт транзакции, WAL, backup одним набором файлов и не требует службы. Это не проникает выше infrastructure layer: замена хранилища означает новый адаптер `IPhotoCatalog`, а не переписывание экранов.

## Основные таблицы

| Таблица | Назначение |
|---|---|
| `library_roots` | Подключённые корни и состояние сканирования |
| `media_assets` | Логические фото/Live Photo, показываемые пользователю |
| `media_files` | Физические image/video/sidecar-файлы и FS timestamps |
| `asset_files` | Роли файлов внутри asset |
| `normalized_metadata` | Поля для отображения, сортировки и фильтров |
| `metadata_properties` | Lossless raw/unknown metadata с provenance |
| `metadata_unique_ids` | Повторяемые идентификаторы EXIF/XMP/Apple |
| `metadata_field_sources` | Provenance выбранного значения каждого normalized field |
| `tags`, `asset_tags` | Нормализованные keywords и их источник |
| `quarantine_batches/items` | План, состояние и возможность восстановления |

## Даты

SQLite TEXT хранит ISO-8601. Для EXIF-подобной даты отдельно сохраняются local wall-clock и offset minutes. Если offset неизвестен, значение не притворяется UTC. FS timestamps всегда UTC и лежат только в `media_files`.

## Raw metadata

`metadata_properties` не ограничивает набор ключей. Сохраняются namespace/group/key, тип, raw text или blob, optional normalized text, language, ordinal, source kind и source file. Поэтому новый parser может переосмыслить уже сохранённые значения без изменения базовой схемы или повторного чтения отключённого диска.

Большие binary blocks по умолчанию не копируются: сохраняются digest, length и locator. Малые неизвестные значения можно хранить inline. Это предотвращает разрастание каталога из-за previews и maker notes.

## Индексы

Первая миграция покрывает дату съёмки, папку, camera/lens, rating, GPS, content hash, companion role и tags. Полнотекстовый индекс title/caption/description/author можно добавить отдельной SQLite-миграцией после измерений; это деталь адаптера.

## Миграции

Каждая миграция выполняется целиком в транзакции, записывается в `schema_migrations` и никогда не изменяется после выпуска. Новые изменения добавляются следующим номером. Перед несовместимой миграцией приложение делает SQLite online backup.
