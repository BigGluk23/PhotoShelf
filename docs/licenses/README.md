# Лицензии декодеров и чтения метаданных

Эта папка поставляется рядом с `PhotoShelf.exe` как `licenses/`, в том числе при single-file publish. Сохраняйте её при распространении приложения. Тексты лицензий приведены без изменений; библиотеки PhotoShelf не модифицирует.

| Компонент | Версия | Файлы лицензий и уведомлений |
| --- | --- | --- |
| SkiaSharp | 4.152.1 | [MIT](SkiaSharp-4.152.1-LICENSE.txt) |
| SkiaSharp.NativeAssets.Win32 | 4.152.1 | [MIT](SkiaSharp.NativeAssets.Win32-4.152.1-LICENSE.txt), [все third-party notices из NuGet](SkiaSharp.NativeAssets.Win32-4.152.1-THIRD-PARTY-NOTICES.txt) |
| MetadataExtractor | 2.9.3 | [Apache-2.0 и attribution](MetadataExtractor-2.9.3-LICENSE.txt) |
| XmpCore, транзитивная зависимость MetadataExtractor | 6.1.10.1 | [Adobe BSD-3-Clause](XmpCore-6.1.10.1-Adobe-BSD-LICENSE.txt), [attribution XmpCore](XmpCore-6.1.10.1-ATTRIBUTION.txt) |

SkiaSharp-тексты скопированы из официальных NuGet-пакетов [SkiaSharp](https://www.nuget.org/packages/SkiaSharp/4.152.1) и [SkiaSharp.NativeAssets.Win32](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Win32/4.152.1). Native notices содержат лицензии включённых в нативную сборку сторонних компонентов, в том числе Skia и кодеков.

Текст MetadataExtractor взят из [LICENSE ревизии, указанной в пакете 2.9.3](https://github.com/drewnoakes/metadata-extractor-dotnet/blob/15be36f1f918e6cdba0bfa8feb7af7f8ac13b69f/LICENSE).

[README точной ревизии XmpCore 6.1.10.1](https://github.com/drewnoakes/xmp-core-dotnet/blob/66b975558d9199cd3eb49093261a61deb30f8c06/README.md#license) указывает BSD-лицензию Adobe XMP SDK. Старая ссылка Adobe EULA возвращает 404; поэтому включён неизменённый [BSD-текст из официального Adobe XMP Toolkit SDK](https://github.com/adobe/XMP-Toolkit-SDK/blob/42eb5267d1ffc1d22066438599a9e2dec020d9ca/LICENSE), а исходные copyright/attribution XmpCore сохранены отдельно.

Версии зависимостей закреплены в project/lock-файлах. При их обновлении обновляйте соответствующие тексты и проверяйте состав опубликованной папки `licenses/`. Этот список относится к новым зависимостям декодирования и метаданных v0.10.3.
