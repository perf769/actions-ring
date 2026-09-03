# Actions Ring — сборка и установка

Готовая портативная сборка доступна на странице
[последнего релиза](https://github.com/perf769/actions-ring/releases/latest).
Исходный код находится в [репозитории Actions Ring](https://github.com/perf769/actions-ring).

## Сборка релиза

Из корня репозитория запустите:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\Build.ps1
```

Скрипт использует .NET SDK 10, выполняет restore, build и test, публикует
самодостаточный single-file `win-x64` релиз в `artifacts\publish`, затем создаёт
`dist\ActionsRing-portable.zip`.

Для CI или тихого запуска добавьте `-Quiet`. Параметр `-SkipTests` предназначен
только для явно сокращённой локальной сборки.

## Портативный запуск

Распакуйте `ActionsRing-portable.zip` в обычную папку и запустите
`ActionsRing.exe`. Установка .NET на целевом ПК не требуется.

## Установка для текущего пользователя

После распаковки ZIP запустите:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

Программа устанавливается без прав администратора в
`%LOCALAPPDATA%\Programs\ActionsRing`. В меню «Пуск» создаётся ярлык
`Actions Ring`, а программа регистрируется для текущего пользователя в разделе
Windows «Установленные приложения».

Установщик не включает автозапуск. Им управляет сама программа через
свою настройку, поэтому состояние отображается в разделе автозагрузки Windows.
Если автозапуск ранее включался у портативной копии, выключите его в ней перед
установкой, а затем включите снова уже в установленной программе.

Тихая установка:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -Quiet
```

## Удаление

Установленную программу удобнее всего удалить через Windows «Установленные
приложения». Для портативной или распакованной копии используйте:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall.ps1
```

По умолчанию настройки в `%LOCALAPPDATA%\ActionsRing` сохраняются, чтобы их
можно было использовать после переустановки. Для полного удаления настроек:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall.ps1 -RemoveUserConfig
```

Для тихого удаления добавьте `-Quiet`.
