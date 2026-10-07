# Защита выпуска: проверенное состояние и действия владельца

Эта инструкция не даёт разрешения на передачу ключа или публикацию релиза. До отдельного согласия владельца секрет подписи не загружается, release workflow не запускается. Обычный Windows CI использует одноразовые тестовые ключи.

## Повторная проверка при подготовке 1.11.1, 4 октября 2026

После принятия PR #12 и #13 повторно прочитаны серверные правила. В ruleset `24250308`
для `main` включены минимум одно одобрение, CODEOWNERS review и last-push approval;
старые approvals сбрасываются, обсуждения должны быть закрыты. Обязательны `windows`
и `Same-host baseline/current performance` от GitHub Actions с актуальной базой.
Удаление и force push запрещены. `Khrumium` не может обойти правила; полный список
`bypass_actors` этому аккаунту API не вернул, поэтому отсутствие обходов у всех
администраторов здесь не утверждается.

Окружение `release` (`23118891621`) требует только BigGluk23, `prevent_self_review=true`,
`can_admins_bypass=false`; разрешена ровно одна deployment policy: branch `main`.
Списки имён secrets окружения и репозитория пусты. Значения ключей/паролей не читались.
Независимое восстановление ключа и личная 2FA этой проверкой не подтверждены.
План подготовки кандидата и остающиеся согласования: [v1.11.1-release.md](v1.11.1-release.md).

## Явно включённый solo-режим, 8 октября 2026

По решению владельца `main` временно работает без обязательного второго ревьюера:
required approvals = 0, CODEOWNERS review и last-push approval выключены. Обязательные
PR, закрытие обсуждений, `windows`, `Same-host baseline/current performance`, запреты
удаления и force-push сохранены. В окружении `release` Required reviewers выключен;
разрешена только точная branch `main`, admin bypass выключен, ключ остаётся только
environment secret. В этом режиме release workflow разрешает dispatch только при
`actor == triggering_actor == BigGluk23`. Возврат Required reviewer BigGluk23 и
Prevent self-review автоматически возвращает защищённый режим с независимым запуском.

Ниже сохранён исторический снимок **до** исправления правил и приёмки PR #12.
Он не описывает текущее состояние обязательного ревью.

## Исторический снимок до PR #12, 4 октября 2026

- Репозиторий `BigGluk23/PhotoShelf` публичный; GitHub user ID владельца `205813777`.
- У текущего аккаунта `Khrumium` есть `push`, но нет `admin` или `maintain`.
- Проверен `main` на `e49966f76fbc664c8824e672b7b52e732a7431cc`. Активный ruleset `PhotoShelf reviewed main` (`24250308`) действует ровно на `refs/heads/main`. Удаление и non-fast-forward запрещены; PR и закрытие обсуждений обязательны.
- **Независимое ревью сейчас не обязательно:** `required_approving_review_count=0`, `require_code_owner_review=false`, `require_last_push_approval=false`. Сброс старых approvals включён. Подготовленная ранее ветка из задачи [#7](https://github.com/BigGluk23/PhotoShelf/issues/7) (`afa3326`) описывала более строгий снимок; его нельзя выдавать за текущее состояние.
- Проверки `windows` и `Same-host baseline/current performance` от GitHub Actions (`integration_id=15368`) обязательны, `strict_required_status_checks_policy=true`.
- Для `Khrumium` API сообщает `current_user_can_bypass=never`. Поле `bypass_actors` в ответе этому аккаунту отсутствует; это не доказывает отсутствие обходов у других участников. Владелец должен проверить полный список под административным аккаунтом.
- Окружение `release` (`23118891621`) требует единственного reviewer `BigGluk23`, `prevent_self_review=true`, `can_admins_bypass=false`. В deployment policies ровно одна запись: branch `main`; тегов и wildcard нет.
- Содержимое ключей, secrets и личные настройки аккаунтов при этом аудите не читались. Утверждения прежнего отчёта владельца о 2FA и отсутствии signing secret не являются их новой независимой проверкой. Состояние ключа и восстановления надо подтвердить отдельно перед выпуском.

Источники снимка: `GET /repos/BigGluk23/PhotoShelf`, `/rulesets/24250308`, `/rules/branches/main`, `/environments/release`, `/environments/release/deployment-branch-policies`. `Khrumium` может подготовить PR, но не восстановить административные настройки ruleset. Изменение CODEOWNERS в Git само по себе эти настройки не включает.

Не выдавать наличие YAML/CODEOWNERS за действующую защиту GitHub. После настройки требуется повторное чтение серверных правил.

## 1. Восстановить обязательное ревью основной ветки

Владелец открывает существующий [ruleset 24250308](https://github.com/BigGluk23/PhotoShelf/settings/rules/24250308). В нём нужно включить три отсутствующих требования: минимум одно одобрение, обязательное одобрение CODEOWNERS и одобрение последнего push другим участником. Более строгие существующие требования сохраняются. Итоговая конфигурация должна обеспечивать:

- No bypass actors, включая администратора.
- Запрет удаления и force push.
- Только pull request; минимум одно одобрение; обязательное одобрение CODEOWNERS.
- Старые одобрения сбрасываются после новых коммитов; последний push одобряет другой человек; обсуждения должны быть закрыты.
- Обязательные проверки от GitHub Actions (`integration_id=15368`): `windows` и `Same-host baseline/current performance`, на актуальной базе main.

Минимальный образец: `tools/release-main-ruleset.json`. Он не является полным снимком действующего ruleset. **Не заменять им существующий ruleset и не создавать дубликат**: это может потерять более строгие параметры. При изменении через API сначала прочитать текущую конфигурацию и изменить только необходимые поля. До и после изменения перечитать:

```bash
gh api repos/BigGluk23/PhotoShelf/rulesets
gh api repos/BigGluk23/PhotoShelf/rulesets/24250308
gh api repos/BigGluk23/PhotoShelf/rules/branches/main
```

`.github/CODEOWNERS` предлагает двух доверенных ревьюеров: `BigGluk23` и `Khrumium`. Изменение вступает в силу после принятия соответствующего PR в `main`; до этого в базе PR остаётся прежний список. GitHub достаточно одобрения одного подходящего code owner, а не обоих сразу. Для PR Khrumium ревью выполняет BigGluk23; для PR BigGluk23 — Khrumium. Ревьюер не должен быть автором PR или участником, выполнившим последний push. Новые коммиты требуют повторной проверки. Добавление CODEOWNER не выдаёт административные права и не меняет reviewer окружения `release`.

Порядок завершения [#7](https://github.com/BigGluk23/PhotoShelf/issues/7): владелец восстанавливает и перечитывает ruleset; проверяет PR с аудитом и вторым CODEOWNER на актуальном SHA; после успешных обязательных checks одобряет и принимает его без bypass. Затем проверяются состояние `main`, действующие правила и Windows CI коммита слияния. Только после этого задача закрывается. До выполнения серверного шага выпуск остаётся организационно заблокирован, даже если CI кода зелёный.

## 2. Сохранить защиту окружения release

Окружение **release** уже создано. Перед выпуском выбрать один режим и проверить его в [Settings → Environments](https://github.com/BigGluk23/PhotoShelf/settings/environments):

- Protected: Required reviewer — **только BigGluk23**, **Prevent self-review** включён; запуск выполняет другой доверенный участник.
- Solo: Required reviewers выключен; запуск выполняет только BigGluk23.
- Выключить **Allow administrators to bypass configured protection rules**.
- Deployment branches and tags: **Selected branches and tags**, ровно одно правило типа **Branch** с именем **main**. Не добавлять tag/wildcard.

Минимальный образец protected-режима хранится в `tools/release-environment.json`. Текущее окружение и policy `main` пересоздавать не нужно. Команды чтения:

```bash
gh api repos/BigGluk23/PhotoShelf/environments/release
gh api repos/BigGluk23/PhotoShelf/environments/release/deployment-branch-policies
```

В ответе аудита от 4 октября присутствует `can_admins_bypass=false`. Если при последующей проверке это поле недоступно, запрет надо подтвердить в интерфейсе владельца: отсутствие поля не является доказательством запрета.

В protected-режиме запуск выполняет другой доверенный участник, а владелец одобряет точный SHA и пакет. В solo-режиме workflow принимает только запуск владельца и не требует approval. Окружение не должно автоматически создаваться при первом запуске YAML: preflight обязан заранее прочитать и проверить его конфигурацию.

После отдельного согласования ключ помещается **только в Environment secrets окружения release**. Repository/organization secret с тем же именем недопустим: он доступен другим workflows. Не загружать ключ при выполнении этой инструкции. Публикация также требует отдельного решения.

## 3. Проверить аккаунты

Каждый участник с правом записи проверяет собственную [настройку 2FA](https://github.com/settings/security): предпочтительно passkey или аппаратный ключ, с резервным способом восстановления. Recovery codes и пароли не передавать агенту, в чат, Git или логи. Владелец проверяет актуальный список [Collaborators](https://github.com/BigGluk23/PhotoShelf/settings/access) и оставляет только необходимых участников.

Публичный API не позволяет подтвердить личную 2FA всех участников. До подтверждения владельцем это остаётся явным непроверенным пунктом, а не успешной автоматической проверкой.

## 4. Ключ и восстановление

Порядок инцидента и смены доверия описан в [update-signing-recovery.md](update-signing-recovery.md). Локальная зашифрованная копия создаётся `tools/signing_key_backup.py`; пароль хранится отдельно в выбранной по умолчанию «Связке ключей» macOS. Копирование только зашифрованного файла на другой компьютер **не** даёт восстановления без пароля. Эта защита не изолирует секрет от полного захвата текущего macOS-аккаунта.

Владелец должен сохранить пароль из записи **PhotoShelf release signing backup** в отдельном надёжном менеджере паролей, а зашифрованный файл — на независимом носителе. До фактической проверки такого восстановления `offDeviceRecoveryVerified` остаётся `false`. Не отправлять пароль агенту или в переписку.

Источники: [GitHub environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments), [ruleset API](https://docs.github.com/en/rest/repos/rules), [environment API](https://docs.github.com/en/rest/deployments/environments), [secret isolation](https://docs.github.com/en/actions/reference/security/secure-use).
