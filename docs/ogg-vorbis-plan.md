# Ogg Vorbis: решение и приёмка (фаза 2)

Документ фиксирует, **как** в проекте появился Ogg Vorbis, **почему** выбран готовый
порт вместо собственной реализации, и **чем** это проверено. Инфраструктура валидации
(эталонный C, активы, золотые хеши, оракул) остаётся пригодной для будущих обновлений
версии порта.

## Решение

Взят **готовый managed-порт `stb_vorbis`** — StbVorbisSharp 1.22.4 (public domain) —
и **вендорен исходником** в `src/InternetRadio.OggVorbis/StbVorbis`. Свой порт с нуля
остановлен: он не создаёт ценности, пока существует проверенный эквивалент.

| Критерий | Как выполнено |
|---|---|
| Без нативных зависимостей | clean managed C#; память под setup-структуры — `Marshal.AllocHGlobal` (аллокатор рантайма) |
| netstandard2.0 | пакет и исходники таргетят netstandard2.0 |
| Ноль внешних пакетов в репозитории | исходник вендорен (public domain), `PackageReference` нет |
| Одна самодостаточная сборка | `InternetRadio.OggVorbis.dll` без соседних DLL — удобно для Unity |
| Совпадение с эталоном | **побитовое** на всех тестовых активах (см. «Приёмка») |
| `unsafe` | только внутри плагина (`AllowUnsafeBlocks` в его csproj); ядро чистое |

Цена решения: ~110 КБ сгенерированного кода в репозитории и ручное обновление версии
(процедура ниже). Единственная правка при вендоринге — вложенные пространства имён
(`InternetRadio.OggVorbis.Vendored[.Hebron]`), чтобы типы не конфликтовали с настоящим
пакетом `StbVorbisSharp`.

### Отклонённые альтернативы

| Вариант | Почему нет |
|---|---|
| Свой скалярный порт `stb_vorbis` (5584 строки C) | ровно то, что уже сделано в StbVorbisSharp; отдельная большая задача с реальным риском ошибок |
| NVorbis (NuGet) | тянет `System.Memory` + `System.ValueTuple` (для Unity это ещё несколько DLL) и принципиально **pull**-ориентирован: чтобы встроить в пушевый `Feed`, нужен свой поток + очередь внутри плагина |
| `PackageReference` на StbVorbisSharp | рабочий вариант, но появляется внешний пакет и второй DLL рядом с плагином; оставлен как опция (см. TODO в `STATUS.md`) |
| Нативные `libvorbis` / `stb_vorbis` через P/Invoke | запрещено требованием «без нативных зависимостей» |
| Concentus (+`Concentus.Oggfile`) | это Opus, а не Vorbis: потоки fallout.fm не декодирует |

## Что реализовано

| Файл | Роль |
|---|---|
| `OggVorbisDecoderFactory.cs` | детект по `Content-Type` (`*vorbis*`) и по сигнатуре (Ogg-страница + id-заголовок `0x01 "vorbis"`), `UsesIcyMetadata = false` |
| `OggVorbisDecoder.cs` | push→`Feed` адаптер: окно неполных пакетов, ресинк, chained-потоки, конверсия в interleaved s16, кэш буферов кадров, `IAudioTagSource` (Vorbis comment) |
| `StbVorbis/*` | вендоренный порт + `README.md` с происхождением, лицензией и процедурой обновления |

Поток данных: `Feed` копит байты в своём окне → `stb_vorbis_open_pushdata` (один раз на
логический поток) → `stb_vorbis_decode_frame_pushdata` в цикле → planar float →
interleaved s16 → `PcmDecoded`. `Reset()` закрывает декодер (освобождает unmanaged-память)
и очищает окно; ядро вызывает его при каждом переподключении.

**Chained-потоки.** Icecast-источники чейнят новый логический поток на каждый трек (за
EOS-страницей идёт BOS-страница с новыми заголовками и тегами). Такой поток порт сам не
продолжает: страница начала нового потока ограничивает окно, поданное текущему декодеру,
а по исчерпании текущего потока декодер переоткрывается на ней — читаются новые
заголовки/теги и звук не прерывается. Без этого поток обрезался на первом стыке (это
измерено на склейке двух файлов: 21 600 сэмплов вместо 300 952).

## Конвенция PCM (критично)

stb содержит две реализации float→short, и они **не эквивалентны**:

- по умолчанию («быстрая», union-трюк) — округление к ближайшему;
- с `STB_VORBIS_NO_FAST_SCALED_FLOAT` — усечение `(int)(x * 32768)`.

Измерено на реальном файле: расхождение ровно **1 LSB на 47.6% сэмплов**. Принято:
эталон собирается с `STB_VORBIS_NO_FAST_SCALED_FLOAT`, а порт обязан усекать:

```csharp
int v = (int)(x * 32768f);
if ((uint)(v + 32768) > 65535) v = v < 0 ? -32768 : 32767;
```

## Инфраструктура валидации

| Что | Где |
|---|---|
| Эталонный C-харнесс (push-API stb, скользящее окно) | `tools/ogg_ref.c` |
| Сборка эталона (MSVC из VS Build Tools) | `cl /O2 /DSTB_VORBIS_NO_FAST_SCALED_FLOAT /I C:\TestWorkspace\reference /Fe:C:\TestWorkspace\build\ogg_ref.exe tools\ogg_ref.c` (через `vcvars64.bat`) |
| Исходник stb | `C:\TestWorkspace\reference\stb_vorbis.c` (v1.22) |
| Тестовые активы | `C:\TestWorkspace\audio\ogg\` — 12 файлов, моно/стерео, 44.1/48/192 кГц, 7.5 КБ…216 с, включая `tagged_chained_44100.ogg` (реальный 2 МБ захват fallout.fm: два chained-трека с полным Vorbis comment) |
| Золотой PCM + SHA256 | `C:\TestWorkspace\audio\ref\*.pcm`, манифест `hashes.txt` |
| Независимый оракул (NVorbis 0.10.5) | `C:\TestWorkspace\oracle` — сравнение с золотом: ≤1 LSB на 0.000–0.005% сэмплов |

Побочные факты, добытые при подготовке (полезны при будущих правках):

- NVorbis на `ocean_ambience.ogg` отдаёт **на 448 сэмплов больше** в хвосте — это
  разница границ последнего кадра, а не ошибка; эталоном остаётся stb.
- Обе конфигурации конверсии дают одинаковое **число** сэмплов, различаются только
  значения (см. конвенцию выше).

## Гейты приёмки (фактические результаты)

| # | Проверка | Команда | Результат |
|---|---|---|---|
| G1 | Сборка + реестр | `dotnet build InternetRadio.sln -c Release`, `-- registry` | 0 ошибок, все проверки ok |
| G2 | Побитовое совпадение | `-- decode <asset> out.pcm` ×11 | 11/11 SHA256 == `hashes.txt` |
| G3 | Инвариантность к порциям | `-- chunk <asset> out.pcm 512|65536` ×11 | 22/22 == G2 |
| G4 | Полный конвейер | `-- pipe <asset> out.pcm 777|16384` ×11 | 22/22 == G2 |
| G5 | Аллокации/скорость | `-- bench <asset> 5 8192` | realtime x269…x402; 62…70 КБ на открытие потока, 0 на кадр |
| G6 | Живой поток | `-- live https://fallout.fm:8444/falloutfm6.ogg 12 out.wav` | 1 184 128 сэмплов, 44100 Гц, 2 ch, per-track `TAGS`, без ошибок |
| G7 | Оракул | `dotnet run --project C:\TestWorkspace\oracle -c Release -- <asset> <golden>` | ≤1 LSB (ожидаемо) |
| G8 | Ядро не тронуто | `git diff --stat -- src/InternetRadio` | только намеренные изменения (см. ниже) |
| G9 | Регресс MP3 | `-- decode` / `-- chunk` / `-- file` на MP3 | `1920E5C9…E03F` |
| G10 | Ресинк | `-- resync <asset> out.pcm 4096` | PCM совпадает с чистым файлом (17 и 4096 байт мусора) |
| G11 | Chained-потоки | склейка `mono_48000_short.ogg` + `crow_cry.ogg` → `-- file` | 300 952 сэмпла, второй поток бит-в-бит |
| G12 | ID3 больше окна | MP3 с 5 КБ ID3v2 и без хинта → `-- file` | бит-в-бит с эталоном |
| G13 | Файловый API | `-- file <asset> out.pcm 4096` и `-- file ... 0` (синхронный) ×12 | 24/24 == G2, `completed=True`, счётчик сэмплов совпадает |
| G14 | Per-track теги + chained | `-- file tagged_chained_44100.ogg` | 2 события `TAGS` (`A Wonderful Guy` → `The End Of The World`), PCM == эталонный C |

Итого: **84/84** файловых проверки совпали с эталоном, плюс chained-потоки (синтетический
и реальный захват), ID3 без хинта, per-track теги, ресинк, реестр и регресс MP3.


## Обновление вендоренной версии

1. Скопировать `src/StbVorbis.cs`, `src/StbVorbis.Generated.cs`,
   `src/Hebron.Runtime/*` из нужного тега `StbSharp/StbVorbisSharp`.
2. Заново применить переименование пространств имён:
   `StbVorbisSharp` → `InternetRadio.OggVorbis.Vendored`,
   `Hebron.Runtime` → `InternetRadio.OggVorbis.Vendored.Hebron`,
   `using Hebron.Runtime;` → `using InternetRadio.OggVorbis.Vendored.Hebron;`.
3. Пересобрать эталон и золотые PCM (`ogg_ref.exe <asset> <out.pcm> 4096`) — хеши
   обязаны совпасть с `hashes.txt`; если нет, разбираться до правок в порте.
4. Прогнать G1–G10.

## Осталось опционально

- упаковка: `PackageReference` вместо вендоринга (см. TODO в `STATUS.md`);
- `Station.Name`/`Genre` из Vorbis comment и `ice-*` заголовков;
- AAC-декодер;
- per-song `StreamTitle` для Ogg в общем случае недоступен: Icecast не меняет
  comment header в середине потока (у fallout.fm тайтлы видны только на их
  серверной статус-странице).
