# InternetRadio — компактный проигрыватель интернет-радио на C#

Компактный, кроссплатформенный модуль для проигрывания интернет-радио
(Shoutcast / Icecast) без внешних зависимостей и без привязки к Unity.

Целевая платформа — **netstandard2.0** (совместимо с Unity 2018.3+ и .NET Framework/6/7/8).
Единственный внешний пакет отсутствует: HTTP/ICY-клиент, буферизация и декодирование
реализованы целиком в проекте.

## Что умеет

- Подключение к станции по `http://` (и `https://` через SslStream).
- Разбор ответа `ICY 200 OK` / `HTTP/1.0 200 OK` и заголовков (`icy-metaint`, `icy-name`, `icy-br`, …).
- Приём бесконечного потока и отделение in-band ICY-метаданных (`StreamTitle`) от аудио.
- Буферизация: потокобезопасный кольцевой буфер + пребуфер перед стартом декодирования.
- Декодирование **MP3 (MPEG-1/2/2.5 Layer III)** в PCM 16 бит (моно/стерео/joint stereo,
  короткие блоки, битовый резервуар).
- Декодирование **Ogg Vorbis** — опциональным плагином `InternetRadio.OggVorbis`.
- Автоматическое переподключение при обрыве (с полным сбросом состояния кодека).
- Выдача декодированного PCM потребителю через событие.
- Декодирование **файлов и произвольных потоков** теми же декодерами: `AudioFileDecoder`
  читает источник порциями (память не зависит от длины) и сообщает о завершении.
- Метаданные станции: заголовки `icy-*` / `ice-*`, `ice-audio-info`
  (`samplerate` / `channels` / `bitrate`) и теги контейнера (Vorbis comment) — событие
  `TagsChanged`.
- Chained Ogg-потоки (Icecast чейнит новый логический поток на каждый трек): декодер
  переоткрывается на новом потоке, поэтому звук и per-track теги не теряются.
- Подключаемые декодеры: встроен MP3, остальные кодеки добавляются регистрацией
  фабрики (`IAudioDecoderFactory`) и не требуют правок ядра.

Ядро декодирует **MP3**. Поток кодека, для которого не зарегистрирован декодер,
отклоняется с диагностикой: `No registered decoder accepts this stream. Registered
codecs: mp3. Content-Type: application/ogg, leading bytes: 4f 67 67 53 00 02 00 00`.
Декодер подключается одной строкой:

```csharp
var radio = new InternetRadio();
radio.Decoders.Register(new OggVorbisDecoderFactory()); // из InternetRadio.OggVorbis.dll
```

`InternetRadio.OggVorbis` — отдельная сборка (ссылается на ядро, наоборот нельзя),
поэтому кодеки можно не тащить в проект, если они не нужны. Реализация — вендоренный
public-domain порт `stb_vorbis` v1.22 (см. `src/InternetRadio.OggVorbis/StbVorbis/README.md`);
он использует указатели, поэтому `AllowUnsafeBlocks` включён **только** в этой сборке,
ядро остаётся без `unsafe` и без внешних пакетов.

## Использование

```csharp
var radio = new InternetRadio();

radio.PcmDecoded += frame =>
{
    // frame.Samples  — interleaved short[] (L,R,L,R,...)
    // frame.SampleRate, frame.Channels
    // В Unity: скопировать в AudioClip.SetData(...)
};
radio.StreamTitleChanged += title => Console.WriteLine("Сейчас играет: " + title);
radio.StateChanged  += s => Console.WriteLine(s);
radio.Error         += e => Console.WriteLine(e.Message);

radio.SetUrl("http://195.91.237.50:8000/ices128");
radio.Start();
// ...
radio.Stop();
```

Три обязательные команды: `SetUrl` (задать адрес станции), `Start` (запустить),
`Stop` (остановить).

Полезные настройки (свойства): `PrebufferBytes`, `RingCapacityBytes`,
`SocketReceiveBufferBytes`, `ConnectTimeoutMs`, `ReadTimeoutMs`, `ReconnectDelayMs`,
`AutoReconnect`.

## Декодирование файлов и потоков

Те же декодеры доступны отдельно от радио — для локального файла или любого
forward-only потока (сеть, `MemoryStream`). Чтение идёт порциями, память не зависит от
длины источника, о конце сообщает событие `Completed`:

```csharp
var decoder = new AudioFileDecoder();      // ядро + зарегистрированные плагины
decoder.SetSource("music.ogg");            // или SetSource(stream, "audio/mpeg")
decoder.PcmDecoded += frame => { /* frame.Samples — interleaved short[] */ };
decoder.TagsChanged += tags => { /* tags.Title / Artist / Album / Genre */ };
decoder.Completed  += () => Console.WriteLine("декодирование завершено");
decoder.Error      += e  => Console.WriteLine(e.Message);
decoder.Start();                           // фоновый поток
// ...
decoder.Stop();                            // отмена: Completed не поднимается
```

Синхронный вариант — тот же поток данных, без фонового потока:

```csharp
long samples = AudioFileDecoder.DecodeAll("music.ogg", registry,
    frame => { ... }, tags => { /* теги контейнера, для chained-потока — на каждый трек */ });
```

Кодек выбирается по расширению (как `Content-Type` у сервера) и по сигнатуре; ведущий
ID3v2-тег пропускается автоматически, даже если он больше окна детекта.

## Архитектура

| Класс | Роль |
|---|---|
| `InternetRadio` | публичный фасад: `SetUrl` / `Start` / `Stop` + события |
| `StreamClient` | сырой TCP (+TLS), ручной HTTP/ICY-запрос и разбор заголовков |
| `RingBuffer` | потокобезопасный кольцевой буфер |
| `IcySplitter` | отделяет in-band метаданные от чистого аудиопотока |
| `AudioDecoderRegistry` | выбор декодера: сначала по `Content-Type`, затем по сигнатуре |
| `IAudioDecoderFactory` | интерфейс подключаемого кодека (детект + создание декодера + ICY-политика) |
| `IAudioDecoder` | интерфейс самого декодера: `Feed` / `Reset` / `PcmDecoded` |
| `Mp3DecoderFactory` | регистрация встроенного MP3 (пример реализации фабрики) |
| `Mp3Decoder` | собственный декодер MP3 (скалярный порт public-domain алгоритма minimp3, CC0) |
| `Mp3Tables` | константные таблицы декодера (сгенерированы из minimp3) |
| `AudioFileDecoder` | офлайн/файловое декодирование тем же реестром: `Start` / `Stop` / `Completed` |
| `AudioTags`, `IAudioTagSource` | теги контейнера, которые может сообщать декодер (Vorbis comment) |
| `AudioContentType` | хинт `Content-Type` для локального файла по его расширению |

В проекте `src/InternetRadio.OggVorbis` (опциональный плагин):

| Класс | Роль |
|---|---|
| `OggVorbisDecoderFactory` | детект Vorbis (по `Content-Type` и по сигнатуре Ogg-страницы), `UsesIcyMetadata = false` |
| `OggVorbisDecoder` | push→`Feed` адаптер: окно неполных пакетов, ресинк по `OggS`, interleaved 16 бит |
| `Vendored.StbVorbis` | вендоренный порт `stb_vorbis` v1.22 (public domain), используется как есть |

Два фоновых потока: чтение сокета → кольцевой буфер → ICY-сплиттер → пребуфер →
декодер → события PCM. События вызываются на фоновых потоках; при необходимости
маршалируйте их в основной/UI-поток на своей стороне.

### Определение кодека

Первые байты каждого соединения (1 КБ) накапливаются и передаются фабрикам: сначала
проверяется `Content-Type`, затем сигнатура. Накопленный префикс **переигрывается**
декодеру, поэтому заголовки контейнера (Ogg) и ID3v2-тег не теряются. Фабрика MP3
пропускает ведущий ID3v2-тег, если он укладывается в окно детекта. Для файлов
(`AudioFileDecoder`) окно автоматически расширяется до конца тега (до 1 МБ), поэтому
MP3 со встроенной обложкой декодируется и без хинта; в живом потоке тег больше 1 КБ
по-прежнему требует корректного `Content-Type`.

### Реконнект

Каждое новое соединение помечается счётчиком эпохи. При смене эпохи декодер
пересоздаётся, ICY-сплиттер сбрасывается по политике кодека
(`UsesIcyMetadata`), пребуфер набирается заново — состояние прошлого потока не
переносится в новый. Для контейнерных кодеков (Ogg) in-band ICY-метаданные не
вырезаются: их удаление разрушило бы поток.

## Замечания

- Декодер MP3 портирован из общественного достояния
  [minimp3](https://github.com/lieff/minimp3) (CC0) как скалярная реализация
  (Layer III, без SIMD). Проверен посимвольной сверкой с ffmpeg: моно/стерео/
  joint-stereo(M/S), короткие блоки и битовый резервуар декодируются корректно
  (THD синуса ≈ −85 дБ).
- При старте потока первые ~2 кадра (~46 мс) пропускаются для заполнения битового
  резервуара — это штатное поведение и на слух незаметно.
- Полярность PCM соответствует конвенции minimp3 и может отличаться от других
  декодеров (инверсия знака неслышна).
- Для Unity: переводите `short[]` в `float[]` (`s / 32768f`) и используйте
  `AudioClip.Create(..., sampleRate, channels, ...)` + `SetData`.
- Массив `PcmFrame.Samples` переиспользуется между кадрами (нулевые аллокации в
  потоковом пути): копируйте его внутри обработчика `PcmDecoded`, если данные
  нужны дольше, чем до следующего кадра.
- После автопереподключения пребуфер набирается заново, поэтому возможна пауза до
  `PrebufferBytes` (≈8 с при 128 кбит/с); состояние проходит
  `Playing → Buffering → Playing`. Взамен воспроизведение возобновляется без underrun.
- `Station.SampleRate` дополняется первым декодированным кадром, если сервер не
  прислал `icy-sr` (у Ogg-потоков ICY-заголовков метаданных обычно нет).
- Ogg Vorbis: float→PCM переводится **усечением** (`(int)(x * 32768f)` + clamp) — это
  конвенция эталона `tools/ogg_ref.c`; дефолтный «быстрый» путь самого stb округляет
  и расходится с ней на 1 LSB примерно на половине сэмплов. Аллокации — ≈60 КБ на
  открытие потока (структуры setup) и ноль на кадр; выходные буферы кадров
  переиспользуются по размеру блока.
- Метаданные Ogg приходят из двух источников: уровень станции — из заголовков
  (`icy-name` / `icy-genre` / `ice-audio-info`), уровень контейнера — из Vorbis comment
  (`title` / `artist` / `album` / `genre`, событие `TagsChanged`). У одиночного
  Ogg-потока comment header фиксирован на всё соединение; у chained-потоков (Icecast
  чейнит поток на каждый трек) теги читаются заново на каждом стыке — так приходит
  per-track метадата fallout.fm.

## Сборка и тест

```powershell
dotnet build InternetRadio.sln -c Release

# офлайн-проверка декодера (файл -> PCM):
dotnet run --project tests\InternetRadio.Tests -c Release -- decode in.mp3 out.pcm

# регресс-тест потокового пути декодера (порциями N байт; результат обязан совпадать с decode):
dotnet run --project tests\InternetRadio.Tests -c Release -- chunk in.mp3 out.pcm 8192

# весь конвейер (кольцевой буфер -> пребуфер -> декодер) с чтением файла порциями;
# результат обязан побайтово совпадать с decode (проверяет детект кодека и префикс):
dotnet run --project tests\InternetRadio.Tests -c Release -- pipe in.mp3 out.pcm 16384

# офлайн-проверки реестра декодеров (без аудиофайла и сети):
dotnet run --project tests\InternetRadio.Tests -c Release -- registry

# устойчивость декодера: файл с мусором в начале обязан декодироваться как чистый:
dotnet run --project tests\InternetRadio.Tests -c Release -- resync in.ogg out.pcm 4096

# бенчмарк декодера на реальном файле (пропускная способность, realtime-фактор, аллокации):
dotnet run --project tests\InternetRadio.Tests -c Release -- bench in.mp3 5 8192

# живой поток (url, секунды, вывод WAV):
dotnet run --project tests\InternetRadio.Tests -c Release -- live http://host:8000/stream 10 out.wav

# послушать вживую на Windows (минимальный плеер на waveOut/winmm):
dotnet run --project tests\InternetRadio.Tests -c Release -- play http://host:8000/stream 30

# проиграть локальный WAV тем же плеером (изоляция уровня воспроизведения):
dotnet run --project tests\InternetRadio.Tests -c Release -- wavplay file.wav 30

# проиграть локальный файл через буферизацию + декодер (стриминг с диска, без сети):
dotnet run --project tests\InternetRadio.Tests -c Release -- fileplay in.mp3 30
```

## Проверка на слух (Windows)

Тестовый проект содержит минимальный Windows-плеер `WaveOutPlayer` (P/Invoke в
`winmm.dll`, без сторонних библиотек): команда `play` подключается к станции,
декодирует MP3 и воспроизводит PCM через штатный Windows-вывод звука с двойной
буферизацией. Это самый прямой способ убедиться в качестве декодирования на слух.

```powershell
dotnet run --project tests\InternetRadio.Tests -c Release -- play http://195.91.237.50:8000/ices128 30
```

`play` раз в секунду печатает диагностику: число декодированных кадров, скорость
приёма (КБ/с), заполнение кольцевого буфера и глубину звуковой очереди (текущую и
минимальную — её падение к 0 означает underrun). Пополнение очереди идёт по событию
`CALLBACK_EVENT`, а не опросом `Thread.Sleep(1)`; PCM агрегируется в блоки ~100 мс,
чтобы драйвер звука не «спотыкался» на границах мелких блоков.

`fileplay <file> [seconds]` — то же проигрывание, но источником служит локальный
файл: читается с диска порциями и прогоняется через кольцевой буфер, пребуфер и
декодер (без сети и ICY-метаданных). С `seconds = 0` (или без аргумента) файл
проигрывается целиком, после чего очередь звука дренируется.

Плеер намеренно вынесен в тестовый проект (Windows-only): сама библиотека
`InternetRadio` остаётся кроссплатформенной и не содержит Windows-зависимостей.
