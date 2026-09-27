# PhotoShelf

PhotoShelf — быстрый локальный менеджер фототеки для Windows в духе классической Picasa: дерево библиотеки и папок, виртуализированная сетка миниатюр, временная шкала, мгновенный просмотр с клавиатуры и безопасный разбор дубликатов. Файлы остаются на своих местах; приложение хранит только локальный каталог, метаданные и кэш производных изображений.

Текущее состояние — архитектурный фундамент: solution и границы слоёв, доменная metadata-модель, storage-neutral контракты, первая SQLite-миграция и лёгкий WPF shell. Индексатор, рабочая сетка и viewer будут следующими реализационными срезами.

Проект не копирует название, логотип, иконки или графические материалы Google/Picasa. Референс относится к проверенным UX-паттернам: плотной компоновке, быстрым действиям и ощущению лёгкого настольного приложения. Визуальная тема и набор иконок PhotoShelf должны быть собственными.

## Обязательные принципы

- Скорость проектируется с первого дня: виртуализация, фоновые очереди, отменяемые операции, bounded cache и предзагрузка соседних кадров.
- SQLite — локальный каталог, а не место хранения оригиналов. UI и бизнес-логика работают через `IPhotoCatalog`; заменить адаптер на PostgreSQL или другой store можно без их переписывания.
- Время файловой системы и даты из EXIF/XMP/IPTC хранятся раздельно. Сортировка по дате съёмки никогда не подменяется `LastWriteTime` без явного fallback.
- Нормализованные метаданные оптимизированы для поиска. Все исходные и неизвестные пары сохраняются отдельно вместе с namespace, типом, источником и порядком.
- Одно логическое фото (`MediaAsset`) может состоять из нескольких файлов: HEIC/JPEG + MOV для Live Photo, XMP sidecar, Apple AAE и будущих companion-файлов.
- Операции с дублями сначала перемещают файлы в восстанавливаемый карантин с manifest; окончательное удаление — отдельное подтверждённое действие.

## Метаданные первой версии

Нормализуются и индексируются, где доступны:

- `DateTimeOriginal`, `CreateDate`, `ModifyDate` с известным offset и точностью;
- GPS latitude/longitude/altitude;
- camera make/model, lens make/model, focal length, aperture, shutter/exposure time, ISO;
- orientation, width/height, color profile;
- rating, title, caption, description, keywords/tags;
- author/creator, copyright, software;
- document/image/instance/content identifiers.

Reader API одинаков для JPEG/PNG/TIFF и будущих HEIC/HEIF/RAW-декодеров. Встроенные метаданные специальных форматов не должны теряться при добавлении нативных декодеров.

## Структура

```text
src/
  PhotoShelf.Domain/                 сущности и metadata value objects
  PhotoShelf.Application/            use cases и независимые порты
  PhotoShelf.Infrastructure.Sqlite/  SQLite-схема, миграции и адаптер каталога
  PhotoShelf.Desktop/                WPF shell и presentation layer
tests/
  PhotoShelf.Domain.Tests/
docs/
  architecture.md
  database.md
```

Направление зависимостей:

```text
Desktop ───────► Application ───────► Domain
                         ▲
                         │ implements ports
Infrastructure.Sqlite ───┘
```

## Фаза 1

1. Подключение папок и инкрементальное сканирование.
2. Чтение базовых файлов и metadata pipeline с сохранением normalized + raw properties.
3. Виртуализированная сетка, папки/даты, полноэкранный просмотр и клавиатурная навигация.
4. Дисковый кэш миниатюр и предзагрузка соседних изображений.
5. Поиск точных дублей и безопасный карантин.
6. Модели связей XMP/AAE/Live Photo готовы; полная интерпретация этих форматов может прийти следующей фазой.

Подробности: [архитектура](docs/architecture.md) и [схема каталога](docs/database.md).

## Сборка

Требуется Windows и .NET 9 SDK:

```powershell
dotnet restore PhotoShelf.sln
dotnet build PhotoShelf.sln -c Release
dotnet test PhotoShelf.sln -c Release
```

На машине, где создан каркас, установлен .NET Desktop Runtime 9, но отсутствует SDK; поэтому сборку необходимо выполнить после установки SDK.
