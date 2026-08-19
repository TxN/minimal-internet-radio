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
- Автоматическое переподключение при обрыве.
- Выдача декодированного PCM потребителю через событие.

AAC / Ogg Vorbis детектируются по `Content-Type`/сигнатуре, но пока не декодируются —
для расширения предусмотрен интерфейс `IAudioDecoder`.

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

## Архитектура

| Класс | Роль |
|---|---|
| `InternetRadio` | публичный фасад: `SetUrl` / `Start` / `Stop` + события |
| `StreamClient` | сырой TCP (+TLS), ручной HTTP/ICY-запрос и разбор заголовков |
| `RingBuffer` | потокобезопасный кольцевой буфер |
| `IcySplitter` | отделяет in-band метаданные от чистого аудиопотока |
| `CodecDetector` | определение кодека по `Content-Type` + магическим байтам |
| `IAudioDecoder` | интерфейс подключаемого декодера |
| `Mp3Decoder` | собственный декодер MP3 (скалярный порт public-domain алгоритма minimp3, CC0) |
| `Mp3Tables` | константные таблицы декодера (сгенерированы из minimp3) |

Два фоновых потока: чтение сокета → кольцевой буфер → ICY-сплиттер → пребуфер →
декодер → события PCM. События вызываются на фоновых потоках; при необходимости
маршалируйте их в основной/UI-поток на своей стороне.

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

## Сборка и тест

```powershell
dotnet build InternetRadio.sln -c Release

# офлайн-проверка декодера (файл -> PCM):
dotnet run --project tests\InternetRadio.Tests -c Release -- decode in.mp3 out.pcm

# регресс-тест потокового пути декодера (порциями N байт; результат обязан совпадать с decode):
dotnet run --project tests\InternetRadio.Tests -c Release -- chunk in.mp3 out.pcm 8192

# бенчмарк декодера на реальном MP3 (пропускная способность, realtime-фактор, аллокации):
dotnet run --project tests\InternetRadio.Tests -c Release -- bench in.mp3 5 8192

# живой поток (url, секунды, вывод WAV):
dotnet run --project tests\InternetRadio.Tests -c Release -- live http://host:8000/stream 10 out.wav

# послушать вживую на Windows (минимальный плеер на waveOut/winmm):
dotnet run --project tests\InternetRadio.Tests -c Release -- play http://host:8000/stream 30

# проиграть локальный WAV тем же плеером (изоляция уровня воспроизведения):
dotnet run --project tests\InternetRadio.Tests -c Release -- wavplay file.wav 30

# проиграть локальный MP3 через буферизацию + декодер (стриминг с диска, без сети):
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

`fileplay <file.mp3> [seconds]` — то же проигрывание, но источником служит локальный
файл: читается с диска порциями и прогоняется через кольцевой буфер, пребуфер и
декодер (без сети и ICY-метаданных). С `seconds = 0` (или без аргумента) файл
проигрывается целиком, после чего очередь звука дренируется.

Плеер намеренно вынесен в тестовый проект (Windows-only): сама библиотека
`InternetRadio` остаётся кроссплатформенной и не содержит Windows-зависимостей.
