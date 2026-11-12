# Интеграция оплаты и сохранений

Дополнительное API остаётся в `EasyResources.Runtime`; обязательной зависимости от EasyCombat, EasyUI или EasyLocalization нет. Адаптер боевых стоимостей принадлежит EasyCombat, использует существующий ResourceContainer и не создаёт второй баланс.

## Короткая синхронная оплата

```csharp
var notifications = wallet.Store.DeferNotifications(
    new ResourceOperationContext("combat-cost", operationId));
ResourceReservation payment = default;
bool deliveryStarted = false;
try
{
    if (!wallet.TryReserve(combinedCost, out payment, out var failure))
        return false; // Ни списания, ни промежуточного события.

    // До необратимого выполнения: проверка цели, владельца, доступности действия.
    if (!CanExecuteNow())
        return false;

    // Граница невозврата принадлежит вызывающей игровой системе.
    // Она решает момент commit и порядок применения своего результата.
    deliveryStarted = true;
    ExecuteAndCompleteGameState();
    return true;
}
finally
{
    try
    {
        if (deliveryStarted)
        {
            // Сохраняет расход и при исключении после начала эффекта.
            if (!payment.TryCommit(out var completionFailure))
                ReportInvalidatedOperation(completionFailure); // Не повторять оплату в новом состоянии.
        }
        else payment.Cancel();
    }
    finally { notifications.Dispose(); }
}
```

Этот фрагмент показывает механизм границы уведомлений. Политика исключений и момент подтверждения определяются контрактом вызывающей системы. Для EasyCombat используйте его `CombatCostProvider` / `CombatCostTransaction.Complete(bool committed)`: `Complete(true)` вызывает `payment.TryCommit(out failure)`, `Complete(false)` — `payment.Cancel()`, и оба закрывают `notifications` в `finally`. По согласованному контракту Combat исключение после начала необратимого Execute/trace сохраняет расход. Сами по себе `DeferNotifications` и `Dispose` не выбирают исход и не откатывают игровой мир.

Одна `ResourceCost` содержит все ресурсы оплаты. Повторяющиеся строки суммируются до проверки. `CanAfford` — подсказка для UI; `TryReserve` — фактическая атомарная проверка и захват доступного количества. Возвращаемое `true` не освобождает вызывающую систему от проверки собственного состояния владельца/действия.

`ResourceNotificationScope` — небольшой readonly struct. Его копии закрывают одну область только один раз; `IsActive` показывает, действует ли она ещё. Вложенные области можно закрывать в любом порядке: события придут после закрытия последней. Вложенный beam pulse видит резерв admission-стоимости и не может потратить его повторно. Расход уже подтверждённого дочернего действия остаётся, если родительский резерв позднее отменён.

Не храните область через кадр или `await`. Для windup/строительства/исследования используйте обычный именованный резерв; область уведомлений нужна для короткого синхронного участка принятия/завершения операции. После возврата боевой команды scope должен быть закрыт. Забытый scope удерживает уведомления и намеренно запрещает снимок состояния.

## Что наблюдают UI и локализация

Внутри области остатки и резервы меняются сразу, но `Changed`, `DetailedChanged`, ревизия и журнал отражают результат только на выходе. Финал даёт одну ревизию, один `Changed` и максимум одну строку `DetailedChanged` на ресурс: исходное состояние → итоговое. `Kind` — `Transaction`, `Context` — контекст внешней области. Полная отмена нового резерва без итогового изменения не создаёт события или записи истории.

`GetTotal`, `GetReserved`, `GetAvailable` и `CanAfford` доступны для чтения, в том числе вложенному действию. События должны обновлять представление. Мутации того же участвующего хранилища из события по-прежнему запрещены; исключение C#-наблюдателя логируется и не отменяет подтверждённое состояние. Готовая Resource Panel объединяет изменения в одно обновление за кадр. Собственная локализованная строка может по Changed перечитать число и передать его форматтеру EasyLocalization; Core не навязывает локализационный backend.

Если область закрыта из callback другого участвующего хранилища, итоговая публикация ждёт завершения исходного набора callbacks. Все участники исходной ResourceTransaction остаются защищены от reentry во время этой финализации. Область относится к одному хранилищу: для операций над несколькими хранилищами продолжайте использовать ResourceTransaction; она сначала проверяет и обновляет всех участников.

## Отмена, компенсация и уничтожение

- `payment.Cancel()` освобождает оставшийся резерв, не уменьшая total. Повторный cancel/commit, `default` и старое поколение возвращают `false`.
- `payment.TryCommit(out failure)` подтверждает весь оставшийся резерв с типизированным отказом; частичный overload `TryCommit(part, out failure)` сохранён.
- `plan.Clear().Commit(first).Commit(second).Add(destination, reward).TryExecute(out failure)` атомарно обрабатывает несколько резервов и выдачу. Отказ по лимиту/переполнению ничего не списывает и оставляет оплату активной.
- После успешного commit резерв завершён: Cancel не является возвратом потраченного. Компенсация — новая явная транзакция с первоначальной стоимостью; она тоже может целиком отклониться по вместимости/переполнению. Её повторяемость контролирует сохраняемая фаза игровой операции.
- Destroy/disable потребителя до исполнения: отмените его активный резерв и закройте его scope. Если потребитель уничтожен из финального Changed, платёж уже завершён и не возвращается автоматически. Второй completion не оплачивает и не компенсирует повторно.
- Уничтожение ResourceContainer инвалидирует его старые резервы/scopes. Если оно произошло из уведомления, освобождение откладывается до конца наблюдателей; они ещё видят согласованное финальное состояние.

## Точные единицы

Количество в Core — `long` в наименьших единицах. Параметр точности называется **DecimalPlaces**, а не Precision. При `DecimalPlaces = 2` одна целая единица = 100 raw units, поэтому стоимость 1,25 = 125.

```csharp
long raw = energy.ToUnits(1.25m); // Точно; исключение при лишних знаках или переполнении.
decimal quantity = energy.FromUnits(raw);
string text = energy.Format(raw);
```

Составную стоимость готовьте один раз и переиспользуйте. Не приводите float к long с отбрасыванием дроби. Если входной контракт задаётся float, преобразование и политика точности должны быть явными у владельца адаптера; EasyResources принимает уже проверенные raw units. Изменение DecimalPlaces после выпуска требует миграции сохранённых количеств и замороженных стоимостей активных операций через ResourceStateMigration.

## Снимок без повторной оплаты

Снимок игры делайте на границе законченной команды, после закрытия всех коротких областей. `CaptureState` и `ResourceReplicator.Capture` явно отклоняют незакрытую область. Согласованное состояние одного кошелька не является автоматическим снимком чужого боевого контроллера.

Для сохраняемого длительного действия:

```csharp
var options = new ResourceReservationOptions("player-1/action-42", nowMilliseconds: now);
wallet.TryReserve(frozenCost, out payment, out failure, options);

// В одну запись игры: кошелёк + фаза операции + OwnerId + reservation ID + frozen cost.
var walletState = wallet.CaptureState(includeReservations: true, nowMilliseconds: now);
long savedReservationId = payment.Id;
```

Порядок загрузки:

1. Остановить выдачу новых игровых команд и завершить/отменить текущую синхронную обработку. Подготовить определения и проверить игровой DTO до изменения мира.
2. Восстановить кошельки. Для нескольких — `ResourceSnapshots.RestoreAll`. Явно включить `ResumeReservations` и `ReplaceActiveReservations`, задать новый монотонный `NowMilliseconds`.
3. Восстановить фазу контроллера. Pending-операцию связать с `store.TryGetReservation(savedId, savedOwnerId, out payment)`. Проверить замороженную оставшуюся стоимость через `CopyRemainingTo` и сохранённые строки. **Не вызывать TryReserve повторно**.
4. Если сохранена фаза committed/completed, не повторять списание или выдачу результата. Отдельные игровые эффекты/снаряды восстанавливает их владелец.
5. Проверить, что все восстановленные named holds принадлежат реально возобновлённым операциям. Бесхозные/отменённые операции явно отменить; затем разрешить новые команды.

```csharp
store.RestoreState(walletState, definitions, new ResourceRestoreOptions(
    resumeReservations: true, replaceActiveReservations: true, nowMilliseconds: newNow));
if (!store.TryGetReservation(savedReservationId, "player-1/action-42", out payment))
    RejectInconsistentOperationSnapshot();
```

Restore с явным ReplaceActiveReservations инвалидирует также старые notification scopes, отбрасывает их ещё не опубликованные изменения и публикует восстановленное состояние. Поздний Dispose/Commit старого дескриптора не закрывает новую область и не изменяет восстановленный кошелёк. Без явного разрешения заменять активную область/резерв нельзя. OwnerId связывает данные операции, но не является сетевым доказательством полномочий.

## Настройка для общей сцены

1. Создать Resource assets Ammo (DecimalPlaces 0) и Energy (DecimalPlaces 2). На игроке/его общем кошельке добавить Resource Container, задать 10 Ammo и 5 Energy в Initial Contents.
2. Подключить владельческий sample EasyCombat/EasyResources: его EasyResourcesCostProvider направить в этот Container; CombatResourceMap связывает боевые определения с теми же assets. Второй счётчик этих ресурсов не создавать. Точные поля и способ установки — в README адаптера EasyCombat.
3. Событие Changed использовать для UI/локализованной подписи. Для подтверждённого результата действия использовать событие самой боевой системы, а не любое изменение кошелька.
4. Проверить достаточную/недостаточную составную цену, отмену до release, вложенный импульс и смерть/восстановление. Ожидание: отказ и отмена не списывают; успешное завершение списывает один раз; UI получает итог.
5. Сохранять кошелёк и контроллеры единым игровым снимком в описанном порядке. Общий мастер и сцену EasySuite ведёт координатор; этот пакет не создаёт второго Combat bridge.

Регрессии: `DeferredNotificationTests`, `DeferredNotificationLifecycleTests` и прежние тесты пакета. Локальный профилировочный компонент/сцена — `Assets/PackageTests/EasyResources/IntegrationWave1`. Итоговые измерения и воспроизведение хранятся в `Documentation~/Reports/IntegrationWave1` и `Audits/EasyResources/IntegrationWave1`.
