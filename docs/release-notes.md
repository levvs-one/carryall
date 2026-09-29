## Carryall 0.2.0

Первый релиз под полноценным именем **Carryall**.

### Главное

- GUI теперь публикуется как `Carryall.exe`.
- Добавлен отдельный `carryall.exe` CLI для автоматизации.
- CLI умеет собирать ZIP/папку, задавать разрешённые расширения, обязательные пути и лимиты размеров, а также проверять существующие комплекты.
- Release pipeline публикует отдельные self-contained Windows x64 архивы GUI и CLI плюс `SHA256SUMS.txt`.
- CI проверяет build, тесты, CLI smoke test и обе publish-конфигурации.
- Добавлены security/threat-model и contributor docs.

### Совместимость

Формат 0.1 не менялся: `handinpack.json`, `contents.txt` и schema version 1 сохранены. Комплекты Handinpack 0.1 можно проверять Carryall 0.2.

### Ограничение

Windows-бинарники пока не подписаны коммерческим code-signing сертификатом. Проверяйте источник релиза и SHA-256 архива.
