# Защита выпуска: состояние и аудит GitHub

Эта инструкция не даёт разрешения на передачу ключа или публикацию релиза. До отдельного согласия владельца секрет подписи не загружается, release workflow не запускается. Обычный Windows CI использует одноразовые тестовые ключи.

## Проверенное состояние 3 октября 2026

- Репозиторий `BigGluk23/PhotoShelf` публичный; GitHub user ID владельца `205813777`.
- У текущего аккаунта `Khrumium` есть `push`, но нет `admin` или `maintain`.
- Активный ruleset `PhotoShelf reviewed main` (`24250308`) применяется ровно к `refs/heads/main`. `bypass_actors=[]`, `current_user_can_bypass=never`; запрещены удаление и non-fast-forward. Обязательны PR, одно одобрение CODEOWNERS, сброс старых approvals, одобрение последнего push другим участником, закрытие review threads и актуальные проверки `windows` и `Same-host baseline/current performance` от GitHub Actions (`integration_id=15368`).
- Окружение `release` (`23118891621`) требует только reviewer `BigGluk23`, включает **Prevent self-review**, не разрешает administrator bypass и допускает ровно одну deployment policy: branch `main`; тегов и wildcard нет.
- Environment secrets и variables пусты. `PHOTOSHELF_UPDATE_SIGNING_KEY` не загружен; release workflow, тег, GitHub Release и deployment не запускались.
- В GitHub UI владельца подтверждено: 2FA включена, preferred method — authenticator app, состояние `Configured`. Recovery codes, пароль и OTP агент не читал и не сохранял. Состояние 2FA остальных участников публичный API не подтверждает.

Не выдавать наличие YAML/CODEOWNERS за действующую защиту GitHub: источником истины остаются серверные ruleset и environment. Перед каждым выпуском перечитывать их и сохранять появившиеся более строгие ограничения.

## 1. Аудит основной ветки

В [Settings → Rules](https://github.com/BigGluk23/PhotoShelf/settings/rules) уже действует ruleset `24250308`. Он должен сохранять:

- No bypass actors, включая администратора.
- Запрет удаления и force push.
- Только pull request; минимум одно одобрение; обязательное одобрение CODEOWNERS.
- Старые одобрения сбрасываются после новых коммитов; последний push одобряет другой человек; обсуждения должны быть закрыты.
- Обязательные проверки от GitHub Actions (`integration_id=15368`): `windows` и `Same-host baseline/current performance`, на актуальной базе main.

Точная минимальная REST-конфигурация: `tools/release-main-ruleset.json`. Сначала перечитать текущие правила и сохранить более строгие ограничения, если они появились. **Не заменять действующий ruleset** этим файлом и не создавать второй дублирующий набор правил:

```bash
gh api repos/BigGluk23/PhotoShelf/rulesets
gh api repos/BigGluk23/PhotoShelf/rulesets/24250308
gh api repos/BigGluk23/PhotoShelf/rules/branches/main
```

`.github/CODEOWNERS` назначает владельца ревьюером всех изменений. Рабочая схема: отдельный участник (например Khrumium) создаёт PR, BigGluk23 проверяет и одобряет. Владелец не может одобрять собственный PR; для его собственных изменений потребуется заранее назначенный второй доверенный code owner. Не обходить правило снятием защиты.

## 2. Аудит окружения release

В [Settings → Environments](https://github.com/BigGluk23/PhotoShelf/settings/environments) уже создано **release** (`23118891621`). Оно должно сохранять:

- Required reviewer: **только BigGluk23**.
- Включить **Prevent self-review**.
- Выключить **Allow administrators to bypass configured protection rules**.
- Deployment branches and tags: **Selected branches and tags**, ровно одно правило типа **Branch** с именем **main**. Не добавлять tag/wildcard.

Первые две настройки и режим ветки представлены в `tools/release-environment.json`. Перед изменением сначала прочитать окружение и сохранить существующие более строгие правила. Команды ниже предназначены для аудита; повторно создавать policy `main` нельзя:

```bash
gh api repos/BigGluk23/PhotoShelf/environments/release
gh api repos/BigGluk23/PhotoShelf/environments/release/deployment-branch-policies
```

Запрет обхода администратором надо проверить в интерфейсе: документированный REST-ответ не гарантирует наличие `can_admins_bypass`. Отсутствие поля не является доказательством запрета. Не подменять эту проверку выдуманным API-полем.

Запуск релиза выполняет другой доверенный участник; владелец одобряет точный SHA и пакет в GitHub. Сам владелец не сможет одобрить запуск, который инициировал: это ожидаемое действие запрета self-review. Окружение не должно автоматически создаваться незащищённым при первом запуске YAML: preflight обязан заранее прочитать и проверить его конфигурацию.

После отдельного согласования ключ помещается **только в Environment secrets окружения release**. Repository/organization secret с тем же именем недопустим: он доступен другим workflows. Не загружать ключ при выполнении этой инструкции. Публикация также требует отдельного решения.

## 3. Проверить аккаунты

Каждый участник с правом записи проверяет собственную [настройку 2FA](https://github.com/settings/security): предпочтительно passkey или аппаратный ключ, с резервным способом восстановления. Recovery codes и пароли не передавать агенту, в чат, Git или логи. Владелец проверяет актуальный список [Collaborators](https://github.com/BigGluk23/PhotoShelf/settings/access) и оставляет только необходимых участников.

2FA владельца подтверждена в его GitHub UI 30 сентября 2026. Публичный API не позволяет подтвердить личную 2FA всех остальных участников; каждый участник с write обязан проверить её самостоятельно. Это ограничение не подменяется состоянием 2FA владельца.

## 4. Ключ и восстановление

Порядок инцидента и смены доверия описан в [update-signing-recovery.md](update-signing-recovery.md). Локальная зашифрованная копия создаётся `tools/signing_key_backup.py`; пароль хранится отдельно в выбранной по умолчанию «Связке ключей» macOS. Копирование только зашифрованного файла на другой компьютер **не** даёт восстановления без пароля. Эта защита не изолирует секрет от полного захвата текущего macOS-аккаунта.

Владелец должен сохранить пароль из записи **PhotoShelf release signing backup** в отдельном надёжном менеджере паролей, а зашифрованный файл — на независимом носителе. До фактической проверки такого восстановления `offDeviceRecoveryVerified` остаётся `false`. Не отправлять пароль агенту или в переписку.

Источники: [GitHub environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments), [ruleset API](https://docs.github.com/en/rest/repos/rules), [environment API](https://docs.github.com/en/rest/deployments/environments), [secret isolation](https://docs.github.com/en/actions/reference/security/secure-use).
