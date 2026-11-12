# EasyResources 0.3.0

Переиспользуемая экономика для Unity 6.3: кошельки и склады, точные количества, атомарные операции, резервы и сохранения. Дополнительные модули: производство, доставка, торговля, предметный инвентарь, прогрессия, сетевой авторитет, большие числа и UI.

Простой сценарий требует **Resource asset + Resource Container**. Определение описывает ресурс; количество хранится отдельно в каждом контейнере. Лимиты, журнал, сетевой слой и остальные модули подключаются по необходимости. Базовая сборка не зависит от игрока, ввода, ECM2 и других Easy-пакетов.

## Установка и первый запуск

1. Unity **6000.3**. Package Manager → **+ → Install package from disk…** → `package.json` этого пакета. Встроенный пакет в `Packages/com.oreethehat.easy-resources` уже установлен.
2. Выберите **EasyResources → Samples → Basics → Import**.
3. Откройте `Assets/Samples/EasyResources/0.3.0/Basics/ResourceBasics.unity`, нажмите **Play**.
4. Ожидается **13 PASS**. Итог: A = **6 Wood / 3 Stone**, B = **3 Wood / 1 Stone**, резервов нет.
5. Для расширений импортируйте **Workflows**, откройте `ResourceWorkflows.unity`. Ожидается **18 PASS** и панель ресурсов. Для этой сцены нужен стандартный модуль UI Toolkit.

В Games Factory локальные сцены установлены в `Assets/PackageTests/EasyResources/{Basics,Workflows,Benchmarks}`. Не импортируйте туда вторую копию тех же sample-скриптов: для дополнительной сцены используйте меню **Tools → EasyResources → Create Basics Scene / Create Workflows Scene**. Примеры выполняются автоматически; игрового ввода нет.

Для своего объекта: **Create → EasyResources → Resource**, **GameObject → Create Empty**, **Add Component → Resource Container**, добавьте ресурс и количество в **Initial Contents**. В Play инспектор покажет **Available / Reserved / Total**. По умолчанию хранилище без ограничений. Выключение компонента сохраняет состояние; уничтожение освобождает его. Повторный Play начинает с исходных настроек.

## Основные вызовы

```csharp
using EasyResources;

container.Add(wood, 10);
long available = container.GetAvailable(wood);
var cost = new ResourceCost(new ResourceAmount(wood, 4), new ResourceAmount(stone, 2));

if (container.TrySpend(cost, out var failure))
    FinishAction();
else
    ShowReason(failure.ToString());
```

Стоимость можно задать прямо в инспекторе или вынести в **Resource Cost** asset. Переиспользуйте `ResourceCost`: его проверка и нормализация кэшируются. `Prepare()` прогревает кэш заранее. Повторяющиеся строки суммируются, пустая стоимость означает бесплатную операцию. `null`, отрицательное количество и конфликт ID означают ошибку настройки.

Количество — неотрицательный `long` в **наименьших единицах**. Для дробных ресурсов укажите `Decimal Places`: например, 3 для миллилитров. Инспектор количества принимает обычную дробь без преобразования через `float`.

```csharp
container.Add(fuel, fuel.ToUnits(1.25m)); // 1250 единиц при Decimal Places = 3
string label = fuel.Format(container.GetAvailable(fuel)); // 1.25 L, с учётом локали
var discounted = cost.Scale(9, 10, ResourceRounding.Up);
var batchCost = cost.Multiply(5);
```

`ToUnits` и округление `Exact` отклоняют потерю точности. Для количества за пределами `long` предусмотрен отдельный `EasyResources.LargeNumbers` на `BigInteger`.

## Транзакции, лимиты, резервы

```csharp
var warehouse = new ResourceStore(limits: new ResourceLimits(
    limits: new[] { new ResourceLimit(wood, 100) }, maximumWeight: 500));

bool moved = player.Store.TryTransferTo(warehouse, cost, out var failure);
long accepted = warehouse.AddUpTo(wood, 150); // Остаток = 150 - accepted

var plan = new ResourceTransaction();
plan.Spend(player.Store, price)
    .Add(warehouse, goods)
    .Add(treasury, tax);
bool completed = plan.TryExecute(out failure);
```

Последний пример задаёт **все** изменения операции. Для сохранения общего количества явно добавляйте соответствующие списания; `Add` является разрешённой игровой выдачей. Готовые `Transfer`, `Exchange`, магазин и производство строят свои списания сами. Все хранилища проверяются до изменения. Выдача результата не может финансировать оплату этой же операции. Лимиты учитывают и зарезервированные запасы. Вес и объём задаются на одну наименьшую единицу ресурса.

```csharp
var options = new ResourceReservationOptions("building-42", expiresAtMilliseconds: 10000);
if (container.TryReserve(cost, out var hold, out var failure, options))
{
    hold.TryCommit(firstStageCost, out failure); // Частичное списание
    hold.TryResize(remainingCost, out failure);  // Заменить оставшийся резерв
    // Позже: hold.Commit() или hold.Cancel().
}
container.Store.ExpireReservations(nowMilliseconds);
```

Резерв уменьшает доступное, сохраняя общее количество. `Commit` списывает, `Cancel` освобождает. Повторное завершение и устаревшие дескрипторы безопасно возвращают `false`. Время задаёт игра; истечение обрабатывается явным `ExpireReservations`, фонового таймера нет. Владелец операции отвечает за отмену при своём завершении. `ResourceReservationScope` отменяет группу по `OwnerId` при `Dispose`.

Для общей оплаты и выдачи: `new ResourceTransaction().Commit(hold).Add(output, reward).TryExecute()`. При нехватке места резерв остаётся действительным, результат не выдаётся.

## Модули

| Сборка / пространство имён | Готовый функционал |
|---|---|
| `EasyResources.Runtime` / `EasyResources` | Точные количества, составные стоимости, транзакции, лимиты, резервы, миграции, журнал, очередь операций |
| `EasyResources.Production` | Рецепты, ограниченная FIFO-очередь, пауза, офлайн-догон, производство и расход по скорости, доставка |
| `EasyResources.Trading` | Запас магазина, касса, котировки, динамические цены, налоги, комиссии, явный кредит и проценты |
| `EasyResources.Inventory` | Слоты, совместимые стаки, уникальные экземпляры, качество, прочность, срок годности, создание предмета за ресурсы |
| `EasyResources.Progression` | XP, уровни, ранги, условия и ветки, модификаторы характеристик, исследование, престиж |
| `EasyResources.Networking` | Проверка владельца, команды с последовательностью и защитой от повтора, серверные правила, снимки и дельты, клиентская копия только для чтения |
| `EasyResources.LargeNumbers` | Точные произвольные целые количества, резервирование, обмен, перевод и мост к обычному кошельку |
| `EasyResources.UI` | Resource Panel на UI Toolkit: доступное, резерв, предпросмотр стоимости и вместимость; обновление по событиям |

Ссылку на сборку добавляйте в свой `.asmdef` только при использовании соответствующего модуля. Модули зависят от ядра, а не друг от друга. UI собирается при наличии стандартного `com.unity.modules.uielements`; он не нужен ядру. Инспектор позволяет создавать Recipe, Item, Trade Catalog, Progression Definition и Node assets. Компоненты Producer, Production Station, Resource Trader, Inventory Container и Progression дают стартовый сценарий без собственного управляющего класса.

Примеры и правила модулей: [Modules.md](Documentation~/Modules.md). Перенос версии 0.1: [Migration.md](Documentation~/Migration.md).

## События и жизненный цикл

`Changed` вызывается после всей успешной операции. Наблюдатели видят новые остатки **всех** её участников, в том числе предметы, долг и ранг. `DetailedChanged` содержит ресурс, до/после, ревизию, причину и владельца. Неуспешная операция ядра не публикует изменение. История отключена по умолчанию; `historyCapacity` включает ограниченное кольцо записей.

Читать участников в событии можно. Изменять их до окончания уведомления нельзя: отложите действие. Исключения C#-наблюдателей логируются и не отменяют уже выполненную операцию. Подписки ядра фиксируются на начало публикации. Уничтожение Resource Container из уведомления безопасно откладывает освобождение.

API выполняется на создающем главном потоке Unity. Оно не предназначено для Jobs/Burst. Сетевой транспорт должен передавать команды на этот поток. `ResourceBatch` ограничивает число **целых** транзакций за вызов `Process`; он не растягивает одну транзакцию между кадрами. Поставленный в очередь план нельзя изменять до обработки.

## Сохранения

`CaptureState()` возвращает независимое DTO версии 2 с ID, количеством, точностью и ревизией. Версия 1 читается. Формат файла, атомарную запись и облачное хранилище выбирает игра.

```csharp
var state = store.CaptureState(includeReservations: true, nowMilliseconds: now);
store.RestoreState(state, definitions, new ResourceRestoreOptions(
    resumeReservations: true, replaceActiveReservations: true, nowMilliseconds: newNow));
store.TryGetReservation(savedOperation.ReservationId, savedOperation.OwnerId, out var restoredPayment);
```

По умолчанию активные резервы запрещают сохранение и замену состояния. Явное сохранение требует непустого `OwnerId`. При восстановлении старые дескрипторы становятся недействительными; операции связываются с новыми по сохранённым ID. Очереди, доставки и исследования имеют собственные DTO и `FromState`: сначала восстановите кошельки, затем операции. Не создавайте второй управляющий объект для того же сохранённого резерва.

Неизвестные ID, неверная точность, отрицательные значения, дубли и превышение ограничений отклоняют восстановление целиком. `UnknownResourcePolicy.Ignore` применяется только к обычным остаткам: часть активной оплаты молча теряться не может. `ResourceSnapshots.RestoreAll` восстанавливает несколько кошельков атомарно. `ResourceStateMigration` явно переименовывает ID и преобразует единицы.

Стабильные ID и `.meta` храните в Git. При копировании definition asset инспектор предлагает **Give This Copy a New ID**. Не меняйте ID оригинала со старыми сохранениями. Проверка **Tools → EasyResources → Validate Resource IDs** и проверка перед сборкой ищут конфликты всех типов определений.

## Оплата игровых действий

Для короткой синхронной операции `store.DeferNotifications()` объединяет промежуточные изменения в одно итоговое уведомление. Сначала резервируйте составную стоимость, затем явно подтвердите её через `payment.TryCommit(out failure)` или отмените и закройте область. Вложенные действия видят один общий остаток. Область не переносится через кадр или `await` и сама не откатывает расход.

[Рецепт интеграции](Documentation~/IntegrationWave1.md) описывает порядок оплаты, уничтожение владельца, события UI, точные единицы и восстановление именованного резерва без повторной оплаты через `TryGetReservation(id, ownerId, out payment)`. Адаптер EasyCombat принадлежит EasyCombat. [Результаты интеграционных проверок](Documentation~/Reports/IntegrationWave1/Validation.md).

## Проверки и границы пакета

Тесты: `Tests/EditMode`, `Tests/PlayMode`. Для внешней установки включите пакет в `testables` своего `Packages/manifest.json` и установите Unity Test Framework. Sample **Benchmarks** выполняет калиброванные CPU / GC / memory-замеры в Development Player. Условия и фактические результаты: [Validation.md](Documentation~/Validation.md).

Готовые операции закрывают перечисленные классы задач и допускают композицию. Правила конкретной игры задаются её кодом. Здоровье и урон принадлежат EasyCombat, подбор — EasyInteraction, строительство — EasyBuilding, отклик — EasyFeedbacks. Серверная аутентификация, сетевой транспорт, защита файлов сохранения и игровая экономика не подменяются библиотекой ресурсов.

Версия подготовлена локально. Git-публикация не выполняется автоматически.

## Совместимость Samples

Samples этого выпуска требуют EasyResources **0.3.0+**. Core устанавливается без других Easy-пакетов. Установите зависимости только выбранного sample, затем импортируйте его один раз через Package Manager. В таблице указаны поддерживаемые минимумы этой версии адаптеров: у старых выпусков может не быть нужного API. Зависимости Unity из package.json устанавливаются автоматически.

| Sample | Дополнительные требования | Импортировать заранее |
|---|---|---|
| Basics | Нет | — |
| Workflows | UI Toolkit module 1.0.0 | — |
| Benchmarks | Нет | — |

Оставляйте одну копию каждой sample-сборки. Существующие импорты могут оставаться в прежней папке Assets: перед повторным импортом сверяйте изменения, сохраняя свои правки и GUID assets/actions.
