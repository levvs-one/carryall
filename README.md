# Carryall

[![CI](https://github.com/levvs-one/carryall/actions/workflows/ci.yml/badge.svg)](https://github.com/levvs-one/carryall/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/levvs-one/carryall)](https://github.com/levvs-one/carryall/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Соберите материалы для передачи, проверьте состав и получите результат, который можно перепроверить побайтно.**

Carryall — локальное Windows-приложение и CLI для подготовки наборов файлов. Оно копирует выбранные материалы в новую папку или ZIP, создаёт читаемую опись, записывает SHA-256 и затем повторно читает готовый результат перед публикацией. Исходные файлы не переименовываются, не перезаписываются и не удаляются.

## Что изменилось в 0.2

- Бинарник GUI теперь называется **Carryall.exe**.
- Добавлен headless **Carryall CLI** для автоматизации сборки и проверки.
- CI проверяет GUI, ядро, тесты и CLI, а также публикуемость self-contained Windows-сборок.
- Релизы собираются из `VERSION`, получают два ZIP-артефакта и `SHA256SUMS.txt`.
- Добавлены `CHANGELOG.md`, `SECURITY.md`, инструкция по CLI и Dependabot.
- Формат комплекта остаётся совместимым: `handinpack.json`, `contents.txt` и schema version 1 не менялись.

## Скачать

Откройте [последний релиз](https://github.com/levvs-one/carryall/releases/latest).

Для Windows x64 доступны:

- `Carryall-<version>-windows-x64.zip` — графическое приложение.
- `carryall-cli-<version>-windows-x64.zip` — консольная версия.
- `SHA256SUMS.txt` — контрольные суммы релизных архивов.

Сборки self-contained: отдельная установка .NET Runtime не нужна. Бинарники пока не подписаны коммерческим code-signing сертификатом, поэтому Windows может показать предупреждение о неизвестном издателе. Проверяйте источник и SHA-256 релизного архива.

## Как работает GUI

1. Добавьте файлы или папки, либо перетащите их в окно.
2. Проверьте будущие пути копий, исключения и требования к комплекту.
3. Выберите новую папку результата и формат: ZIP или папка.
4. Нажмите **«Собрать и проверить»**.
5. Готовое имя появляется только после повторного чтения и проверки записанного комплекта.

Существующие результаты не заменяются. При ошибке или отмене незавершённая запись остаётся рядом с результатом как `.handinpack-<id>.incomplete` и никогда не выдаётся за готовый комплект.

## CLI

Быстрый пример:

```powershell
carryall build .\handover.zip .\drawings .\spec.pdf --format zip
carryall verify .\handover.zip
```

Ограничения и обязательные пути тоже доступны из командной строки:

```powershell
carryall build .\handover.zip .\project `
  --allow-ext .pdf,.dwg,.dxf `
  --require drawings/final.dwg `
  --max-file-mib 200 `
  --max-total-mib 1500
```

Полная справка: [docs/cli.md](docs/cli.md).

## Что означает «проверен»

Carryall проверяет, что:

- фактический набор файлов совпадает с manifest;
- отсутствуют лишние и недостающие записи;
- длина каждого файла совпадает;
- SHA-256 каждого файла совпадает;
- `contents.txt` соответствует хешу и длине из manifest;
- пути не содержат опасных или неоднозначных Windows-конструкций;
- ZIP не содержит поддерживаемых как файлы ссылок/reparse entries.

Это **проверка целостности и согласованности**, а не цифровая подпись автора. Тот, кто может заменить одновременно файлы и manifest, способен создать другой внутренне согласованный комплект. Модель угроз описана в [SECURITY.md](SECURITY.md).

## Защитные ограничения

Carryall намеренно отклоняет или ограничивает:

- абсолютные и выходящие наружу пути (`..`);
- зарезервированные Windows-имена вроде `CON`, `NUL`, `COM1`;
- коллизии имён с учётом регистра и Unicode normalization;
- reparse points, junction/symlink и облачные placeholders;
- изменение исходника между сканированием и копированием;
- перезапись готового результата;
- результат внутри исходной папки;
- manifest больше 16 MiB;
- более 100 000 файлов;
- payload больше 1 TiB.

Программа не делает VSS snapshot. Перед сборкой остановите программы, которые продолжают изменять исходные файлы.

## Сборка из исходников

Нужны Windows, PowerShell и .NET SDK **10.0.400** либо более новый patch той же ветки `10.0.4xx`.

```powershell
dotnet restore Handinpack.sln --locked-mode
dotnet build Handinpack.sln -c Release --no-restore
dotnet test --solution Handinpack.sln -c Release --no-build
dotnet run --project src\Handinpack.App\Handinpack.App.csproj -c Release --no-build
dotnet run --project src\Carryall.Cli\Carryall.Cli.csproj -c Release --no-build -- --help
```

## Структура

- `src/Handinpack.Core` — планирование, запись, проверка и стабильный формат manifest.
- `src/Handinpack.App` — WPF-интерфейс Carryall.
- `src/Carryall.Cli` — CLI без внешних runtime-зависимостей.
- `tests/Handinpack.Tests` — файловые, hostile-package и roundtrip-тесты.
- `tests/Handinpack.App.Tests` — ввод, рецепты и восстановление состояния окна.
- `docs/format.md` — формат, лимиты и границы проверки.
- `docs/how-to-use.md` — подробная работа с GUI.
- `docs/cli.md` — использование CLI.

## Совместимость с Handinpack 0.1

Carryall — новое имя проекта Handinpack. Версия 0.2 меняет пользовательское имя приложения и добавляет CLI, но **не ломает формат 0.1**:

- manifest по-прежнему называется `handinpack.json`;
- читаемая опись — `contents.txt`;
- schema version остаётся `1`;
- рецепты продолжают использовать расширение `.handinpack.json`.

Старые комплекты можно проверять новой версией.

## Разработка и безопасность

CI работает на Windows и использует locked NuGet restore, analyzers, warnings-as-errors и реальные файловые тесты, включая сценарий с 10 000 файлов.

Как участвовать: [CONTRIBUTING.md](CONTRIBUTING.md)  
История изменений: [CHANGELOG.md](CHANGELOG.md)  
Безопасность и threat model: [SECURITY.md](SECURITY.md)

Лицензия: [MIT](LICENSE).
