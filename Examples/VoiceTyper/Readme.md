# Whisper Voice Typer

Глобальна голосова диктовка для Windows: натисни й утримуй гарячу клавішу, говори, відпусти —
розпізнаний текст вставляється в **активне вікно** (будь-яке поле вводу).

Працює на GPU через Direct3D 11 рушій Const-me/Whisper (`Whisper.dll`), без хмари й без інтернету.

## Як це працює

1. Утримуєш клавішу (за замовчуванням **Right Ctrl**) → застосунок пише аудіо з мікрофона.
2. Відпускаєш → весь записаний фрагмент розпізнається одним проходом (`runFull`) і вставляється
   у вікно, що має фокус, через емуляцію набору (`SendInput`) або буфер обміну (`Ctrl+V`).

Push-to-talk + розпізнавання цілого фрагмента дає найвищу точність (немає VAD-нарізки).

## Налаштування у вікні

| Поле | Опис |
|---|---|
| **Модель (GGML)** | шлях до `.bin` моделі. `↻` — перезавантажити, `...` — вибрати файл. |
| **Мікрофон** | пристрій захоплення (або «за замовчуванням»). |
| **Відеокарта (GPU)** | адаптер для інференсу. «Авто» обирає дискретну карту (NVIDIA/AMD), а не вбудовану Intel. Зміна перезавантажує модель. |
| **Мова** | Українська / English / Русский. |
| **Вставка тексту** | `SendInput` (всюди, не чіпає буфер) або `Буфер обміну` (швидко, кирилиця надійно). |
| **Клавіша (утримання)** | Right Ctrl / Right Alt / Right Shift / F8 / F9 / Pause / Scroll Lock / Caps Lock. |
| **Додавати пробіл** | дописувати пробіл після кожного фрагмента. |
| **Перехоплювати клавішу** | не передавати гарячу клавішу у вікно (рекомендовано). |

Налаштування зберігаються в `%AppData%\WhisperVoiceTyper\settings.json`.

## Вибір моделі

Нативний рушій тут **пропатчено під словник large-v3** (`Vocabulary` приймає `n_vocab` 51865 **і** 51866
та коректно зсуває спецтокени), тож multilingual працює і з **large-v3 / large-v3-turbo**.

* Працюють **f16** multilingual-моделі: `small`, `medium`, `large-v1`, `large-v2`, `large-v3`, `large-v3-turbo`.
* **Квантовані** (`q5`/`q8`) моделі рушій **не вантажить** — лише `f16`.

### Рекомендації для RTX 3060 (uk + en)

| Модель | Розмір | Якість укр. | Швидкість | Посилання (HuggingFace) |
|---|---|---|---|---|
| `ggml-large-v3-turbo.bin` | ~1.6 GB | відмінна | **дуже швидко** | `.../resolve/main/ggml-large-v3-turbo.bin` |
| `ggml-large-v2.bin` | ~3.1 GB | найкраща | повільніше | `.../resolve/main/ggml-large-v2.bin` |
| `ggml-medium.bin` | ~1.5 GB | дуже добра | швидко | `.../resolve/main/ggml-medium.bin` |
| `ggml-small.bin` | ~466 MB | прийнятна | дуже швидко | `.../resolve/main/ggml-small.bin` |

(База: `https://huggingface.co/ggerganov/whisper.cpp`)

### Збірка нативного рушія (за потреби)

Готовий `Whisper.dll` уже в репозиторії. Щоб перезібрати з патчем v3 (потрібні **VS Build Tools 2022**
з C++ і Windows SDK):

```powershell
# 1) шейдери: ComputeShaders → CompressShaders генерує shaderData-Release.inl + мовні файли
msbuild ComputeShaders\ComputeShaders.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet run --project Tools\CompressShaders\CompressShaders.csproj -c Release
# 2) сам рушій
msbuild WhisperCpp.sln /t:Whisper /p:Configuration=Release /p:Platform=x64
# -> x64\Release\Whisper.dll
```

> Примітка: збірку краще робити **поза OneDrive** (синхронізація блокує запис згенерованих файлів).
> Бекенд `Whisper/WhisperCpp` (сучасна whisper.cpp через підмодуль) поки виключено зі збірки —
> працює пропатчений DirectCompute-рушій.

## Збірка

Застосунок на **WPF (.NET 8)**. Нативний `Whisper.dll` лежить поруч із проектом і копіюється у
вихідну теку автоматично. Потрібен .NET 8 SDK (Windows) і GPU з підтримкою Direct3D 11.

```powershell
# звичайна збірка
dotnet build Examples/VoiceTyper/VoiceTyper.csproj -c Debug

# реліз (self-contained, працює без встановленого .NET)
dotnet publish Examples/VoiceTyper/VoiceTyper.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false -p:GeneratePackageOnBuild=false
```

Готовий реліз: `dist/WhisperVoiceTyper-v1.0-win-x64.zip` — розпакувати й запустити `VoiceTyper.exe`
(`Whisper.dll` має лежати поруч, він уже в архіві). Модель GGML завантажується окремо (див. нижче).

Іконку `app.ico` генерує `Tools/IconGen` (векторний мікрофон, кілька розмірів).

## Запуск

1. Запусти `VoiceTyper.exe`.
2. Вибери модель (multilingual, до v3 — див. вище) і дочекайся «Готово».
3. Клацни в потрібне поле вводу (браузер, месенджер, редактор…).
4. Утримуй гарячу клавішу, говори, відпусти — текст з'явиться.

## Обмеження

* **Автовизначення мови не підтримується** цим рушієм — мова обирається явно.
  З мовою `uk` модель усе одно непогано вставляє окремі англійські слова в український потік.
* Вставка в вікна, запущені **від адміністратора**, вимагатиме запуску VoiceTyper також від адміністратора
  (обмеження ізоляції UIPI у Windows).
