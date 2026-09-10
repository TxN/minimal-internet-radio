# Vendored: StbVorbisSharp (C# port of stb_vorbis)

Эти файлы — сторонний код, скопированный в репозиторий целиком, без изменений логики.
Правки при вендоринге ровно две:

1. вложенные пространства имён (`StbVorbisSharp` → `InternetRadio.OggVorbis.Vendored`,
   `Hebron.Runtime` → `InternetRadio.OggVorbis.Vendored.Hebron`), чтобы типы не
   конфликтовали с настоящим пакетом `StbVorbisSharp`, если он окажется в том же проекте;
2. `public static unsafe partial class StbVorbis` → `internal ...`, чтобы порт не попадал
   в публичную поверхность плагина (наружу торчит только `OggVorbisDecoderFactory`);
3. `#pragma warning disable 0649` в `StbVorbis.cs` — после сужения видимости компилятор
   стал предупреждать о полях структуры, которые push-путь не присваивает.

| | |
|---|---|
| Происхождение | https://github.com/StbSharp/StbVorbisSharp |
| Версия | 1.22.4 (порт `stb_vorbis.c` v1.22) |
| Лицензия | Public Domain (по README проекта; исходный `stb_vorbis` — public domain / MIT, автор Sean Barrett) |
| Файлы | `StbVorbis.cs`, `StbVorbis.Generated.cs`, `Hebron.Runtime/*` |
| Изменено | нет, кроме пространств имён |

Зачем вендорить, а не ссылаться на пакет: сборка `InternetRadio.OggVorbis` остаётся
самодостаточной (один DLL без внешних зависимостей), в репозитории сохраняется принцип
«ядро и плагин без внешних пакетов», а для Unity не нужно тащить рядом ещё один
managed-DLL. Цена: ~110 КБ сгенерированного кода в репозитории и ручное обновление
версии.

Обновление версии: скопировать файлы из соответствующего тега
`StbSharp/StbVorbisSharp`, заново применить переименование пространств имён и прогнать
гейты из `docs/ogg-vorbis-plan.md` (побитовое совпадение с `tools/ogg_ref.c`).

Замечания по интеграции:

- порт использует `unsafe` (указатели) — в `InternetRadio.OggVorbis.csproj` включён
  `AllowUnsafeBlocks`. Нативные зависимости при этом не появляются: память под структуры
  setup выделяется через `Marshal.AllocHGlobal` (аллокатор рантайма), сам код — managed;
- порт отдаёт planar `float`; в interleaved 16-bit PCM его переводит `OggVorbisDecoder`
  с усечением (`(int)(x * 32768f)` + clamp), как эталон `tools/ogg_ref.c`;
- проверено: побитовое совпадение с эталонным C на 11 активах (моно/стерео,
  44.1/48/192 кГц), realtime-фактор ≈ x1900, ~57 КБ аллокаций на открытие потока и
  ноль на кадр.
