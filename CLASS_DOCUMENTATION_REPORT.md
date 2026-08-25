# گزارش مستندسازی کلاس‌های پروژه Ergonomy

**تاریخ تهیه:** ۱۴۰۵/۰۶/۰۳ (2026-08-25)  
**محدوده:** تمام فایل‌های `#C` موجود در راه‌حل `Ergonomy.sln` شامل پروژه میراثی تک‌فرایندی و سه پروژه معماری دوفرایندی.  
**قاعده بررسی:** منطق تجاری، امضاها، نام‌ها و موجودیت‌های پایگاه داده تغییر نکرده‌اند. فقط مستندسازی اضافه شده است.

---

## ۱. نمای معماری سیستم

عامل ارگونومی یک نرم‌افزار ویندوزی است که فعالیت صفحه‌کلید و ماوس کاربر را اندازه‌گیری می‌کند، در آستانه تعریف‌شده هشدار نرمش نشان می‌دهد، متریک سخت‌افزاری ماشین را جمع می‌کند و همه داده‌ها را از مسیر **SQLite Outbox → Kafka → ClickHouse** به مرکز ارسال می‌نماید. تنظیمات اجرایی از متغیرهای محیطی ماشین بارگذاری می‌شوند و سپس به‌صورت دوره‌ای از API تنظیمات (پشتیبانی‌شده با PostgreSQL) تازه‌سازی می‌گردند؛ با این قید که نقاط پایانی زیرساخت و سوئیچ‌های امنیتی هرگز از API بازنویسی نمی‌شوند.

راه‌حل در حال گذار از معماری تک‌فرایندی به معماری دوفرایندی است:

| پروژه | نقش |
| --- | --- |
| `Ergonomy` | برنامه میراثی WinForms که هنوز کل مسیر را در یک فرایند تعاملی اجرا می‌کند. |
| `Ergonomy.Core` | کتابخانه مشترک قراردادها، تنظیمات، لاگ ساختاریافته و انتقال Named Pipe. |
| `Ergonomy.Service` | فرایند پس‌زمینه نشست صفر (Windows Service) که سرور پایپ را میزبانی می‌کند. |
| `Ergonomy.Task` | فرایند تعاملی هر نشست کاربری که کلاینت پایپ و حلقه پیام WinForms را دارد. |

جریان اصلی داده در برنامه میراثی:

```
GlobalInputHook → ActivityMonitor → ErgonomyManager
        ↓ آستانه
   AlarmManager / فرم‌های UI
        ↓ payload
LocalDatabaseManager (SQLite outbox)
        ↓
   SyncEngine → KafkaConnect → Kafka
```

کارگران پس‌زمینه (`SettingsRefreshWorker`، `HealthMonitorWorker`، `PermissionMonitorWorker`، `AdvancedMetricsWorker`) مستقل از نخ UI اجرا می‌شوند. پوسته `MainApplicationContext` فقط چرخه حیات، آیکون سینی و مدیریت خواب اضطراری را نگه می‌دارد.

---

## ۲. پروژه میراثی `Ergonomy`

### ۲.۱ `Program.cs`

| فیلد | مقدار |
| --- | --- |
| فایل | `Program.cs` |
| فضای نام | `Ergonomy` |
| کلاس | `Program` (static) |
| پایه / واسط | ندارد |
| مسئولیت | نقطه ورود برنامه تعاملی میراثی. |
| وابستگی‌ها | `ServiceRegistrar`، `ISettingsService`، `MainApplicationContext` |
| جایگاه معماری | ریشه ترکیب (Composition Root) روی نخ STA. حلقه جنریک‌هاست عمداً استفاده نمی‌شود تا `Application.Run` پمپ فرایند بماند. |

**اعضای مهم**

- کنترل پنهان `uiAnchor` برای انتقال کار از کارگران به نخ UI.
- بازنشانی `SynchronizationContext` قبل از بارگذاری تنظیمات تا بن‌بست استارتاپ رخ ندهد.

**متدها**

| متد | مسئولیت |
| --- | --- |
| `Main` | پیکربندی WinForms، ساخت کنترل پنهان، ساخت DI، بارگذاری بوت‌استرپ و اجرای پوسته برنامه. |

---

### ۲.۲ `MainApplicationContext.cs`

| فیلد | مقدار |
| --- | --- |
| فایل | `MainApplicationContext.cs` |
| فضای نام | `Ergonomy` |
| کلاس | `MainApplicationContext` |
| پایه / واسط | `ApplicationContext` |
| مسئولیت | پوسته سبک چرخه حیات UI: آیکون سینی، مدیریت استثنای نخ UI، اتصال callbackهای فرمان، شروع/توقف کارگران و خواب اضطراری. |
| وابستگی‌ها | `ISettingsService`، `SyncEngine`، `ErgonomyManager`، `CommandManager`، `MessageLogService`، `PermissionsEvaluator`، کارگران چهارگانه، `MetricsEndpoint`، `MachineIdentity`، `WakeUpScheduler`، `HealthCheckService`، `KafkaConnect` |
| جایگاه معماری | لایه پوسته. منطق تنظیمات/همگام‌سازی/متریک از این کلاس خارج شده است. |

**اعضای مهم**

- `_notifyIcon`: نمایش حضور عامل در سینی سیستم.
- `_uiAnchor`: کنترل پنهان marshalling.
- `OnSqliteCriticalFailure`: اتصال خرابی SQLite به چرخه خواب.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | تزریق وابستگی‌ها، ثبت مدیریت خطا، اتصال فرمان‌ها، ارزیابی اولیه مجوز و شروع کارگران. |
| `StartMetricsEndpoint` | راه‌اندازی شنونده پرومتئوس. |
| `OnSettingsChanged` | اعمال تنظیمات تازه روی همگام‌سازی، فرمان، ارگونومی و مجوزها. |
| `HandleCriticalFailure` | ثبت خطای مرگبار و ورود به خواب. |
| `GoToSleepAndRetry` | توقف کارگران و زمان‌بندی بیداری. |
| `WakeUpAsync` | شروع مجدد پایش و ارزیابی پس از خواب. |
| `Dispose` | توقف منظم همه اجزا و آزادسازی منابع. |

---

### ۲.۳ `ErgonomyManager.cs`

| فیلد | مقدار |
| --- | --- |
| فایل | `ErgonomyManager.cs` |
| فضای نام | `Ergonomy.Core` |
| کلاس | `ErgonomyManager` |
| پایه / واسط | `IDisposable` |
| مسئولیت | هماهنگ‌کننده چرخه جمع‌آوری ارگونومی: شروع/توقف هوک، ارزیابی آستانه، نمایش هشدار و صف‌بندی payload نشست. |
| وابستگی‌ها | `AppSettings`، `LocalDatabaseManager`، `MachineIdentity`، `ActivityMonitor`، `AlarmManager`، `DataLogger`، `Control` |
| جایگاه معماری | قلب مسیر ارگونومی در فرایند میراثی. |

**اعضای مهم**

- `_notificationTimer`: تایمر `System.Timers.Timer` که مستقل از پمپ UI تیک می‌زند.
- `_lifecycleLock` و `_thresholdEvalGate`: جلوگیری از شروع/توقف هم‌زمان و ارزیابی هم‌پوشان آستانه.
- `SettingsSourceIsApi` / `IsRunning`: وضعیت منبع تنظیمات و اجرا.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | اتصال وابستگی‌های پایش، هشدار، هویت و outbox. |
| `UpdateSettings` | به‌روزرسانی مرجع تنظیمات، هشدار، لاگر اکسل و فاصله تایمر. |
| `Start` | بارگذاری ناهمگام تصاویر، شروع پایش و ثبت رکورد `Start`. |
| `Stop` | توقف تایمر/هوک، بستن هشدار و ثبت رکورد `End`. |
| `LogEffectiveSettings` | چاپ مقادیر مؤثر مجوز و آستانه. |
| `OnNotificationTimerElapsed` | مقایسه مجموع فعالیت با آستانه و ارسال کار به نخ UI. |
| `HandleThresholdReached` | نمایش هشدار اولیه، ثبت `Update` و صفر کردن مجموع‌ها. |
| `LogSessionState` | ساخت `UserActivityPayload` و نوشتن غیرمسدود در SQLite. |
| `ToShamsiDateTimeString` | تبدیل زمان محلی به رشته شمسی. |
| `Dispose` | توقف مدیر و آزادسازی تایمر و پایشگر. |

---

### ۲.۴ `AlarmManager.cs`

#### کلاس `ImageApiResponse`

| فیلد | مقدار |
| --- | --- |
| فضای نام | `Ergonomy` |
| پایه | ندارد |
| مسئولیت | DTO پاسخ API تصاویر هشدار (`name` و داده Base64). |
| متد | ندارد. |

#### کلاس `AlarmManager`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | مدیریت شمارنده‌های هشدار، بارگذاری تصاویر از API و نمایش فرم‌های اولیه/ثانویه روی نخ UI. |
| وابستگی‌ها | `AppSettings`، `PrimaryAlarmForm`، `SecondaryAlarmForm`، `HttpClient` |
| جایگاه معماری | لایه ارائه هشدار ارگونومی. |

**اعضای مهم**

- `_loadedImages` و `_currentImageIndex`: چرخش تصاویر.
- `_isAlarmActive`، `_sessionCloseCounter`، شمارنده‌های اولیه/ثانویه.
- قفل `_lock` برای ایمنی چندنخی.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | آماده‌سازی فهرست خالی تصاویر. |
| `UpdateSettings` | جایگزینی امن تنظیمات. |
| `LoadImagesFromApiAsync` | دریافت ناهمگام تصاویر از API و تبدیل Base64. |
| `ShowPrimaryAlarm` | نمایش هشدار اولیه یا جلوگیری به‌خاطر حد بستن نشست. |
| `OnPrimaryAlarmClosed` | افزایش شمارنده بستن کاربر و تصمیم برای هشدار ثانویه. |
| `ShowSecondaryAlarmOnUiThread` | نمایش هشدار ثانویه با تصویر تصادفی. |
| `StopAlarms` | پاک‌کردن پرچم فعال بودن هشدار. |

---

### ۲.۵ `CommandManager.cs`

#### کلاس `RemoteCommand`

DTO فرمان راه دور شامل `Id` و `Command`. متد ندارد.

#### کلاس `CommandManager`

| فیلد | مقدار |
| --- | --- |
| پایه / واسط | `IDisposable` |
| مسئولیت | پایش دوره‌ای API فرمان و زمان‌بندی محلی خاموشی/راه‌اندازی؛ اجرای فقط فرمان‌های مجاز. |
| وابستگی‌ها | `ISettingsService`، `LocalDatabaseManager`، `HttpClient`، `MessageAlarmForm` |
| جایگاه معماری | کانال کنترل راه دور. سیاست امنیتی ماشین‌محور در هر مرز اجرا دوباره بررسی می‌شود. |

**اعضای مهم**

- `_scheduleTimer`، `_pollGate`، `_lastExecutedSchedule`.
- callbackهای `OnLogRequired`، `OnStopCollection`، `OnStartCollection`، `OnForceSync`.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | ساخت تایمر و کلاینت HTTP. |
| `GetIntervalMilliseconds` | تبدیل فاصله به میلی‌ثانیه با پیش‌فرض ۳۰ ثانیه. |
| `Start` / `Stop` | کنترل تایمر. |
| `OnTimerElapsed` | آغاز یک دور پایش. |
| `PollOnceAsync` | بررسی زمان‌بندی و دریافت فرمان‌ها بدون همپوشانی. |
| `CheckScheduledTasks` | اجرای shutdown زمان‌بندی‌شده در صورت مجاز بودن. |
| `CheckAndExecuteCommandsFromApi` | دریافت فهرست pending از API. |
| `ExecuteDelayedAsync` | تأخیر ۲۰ ثانیه، اجرای مجدد بررسی مجوز و اعلام اجرا. |
| `ProcessCommand` | اجرای `msg:`، `start` و `stop`. |
| `IsRemoteEnabled` / `IsSystemPowerEnabled` | خواندن سوئیچ‌های امنیتی. |
| `DenyRemote` / `DenySystemPower` | ثبت رد شدن فرمان. |
| `ExecuteSystemPower` | فراخوانی فرایند `shutdown`. |
| `UpdateSettings` | به‌روزرسانی فاصله تایمر. |
| `Dispose` | انتظار برای پایش جاری و آزادسازی منابع. |

---

### ۲.۶ `DatabaseManager.cs`

#### کلاس `DatabaseManager`

| فیلد | مقدار |
| --- | --- |
| فضای نام | `Ergonomy.Database` |
| مسئولیت | دسترسی مستقیم به PostgreSQL برای تنظیمات، تصاویر هشدار، فرمان‌های کلاینت و پروب سلامت. |
| وابستگی‌ها | `Npgsql`، `LocalDatabaseManager` |
| جایگاه معماری | لایه دسترسی داده مرکزی. در مسیر اصلی فعلی تنظیمات بیشتر از API خوانده می‌شوند؛ این کلاس مسیر قدیمی/مکمل PostgreSQL را نگه می‌دارد. |

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | ذخیره مشخصات اتصال و صف محلی. |
| `GetConnectionString` | ساخت رشته Npgsql. |
| `GetSettingsFromDatabase` | خواندن `settings_json` از `app_configuration`. |
| `CheckAndLogPostgresConnectionAsync` | پروب اتصال و ثبت نتیجه در outbox `app_logs`. |
| `GetAllImageNames` | فهرست نام تصاویر از `alarm_images`. |
| `GetImageFromDatabase` | خواندن بایت تصویر و ساخت Bitmap مستقل. |
| `GetPendingCommands` | خواندن فرمان‌های `pending` از `client_commands`. |
| `MarkCommandAsOutdated` / `MarkCommandAsExecuted` | به‌روزرسانی وضعیت فرمان. |

#### کلاس `ClientCommand`

DTO فرمان پایگاه با `Id` و `Command`.

---

### ۲.۷ `SessionManager.cs`

| فیلد | مقدار |
| --- | --- |
| فضای نام | `Ergonomy.Database` |
| مسئولیت | مدل قدیمی صف‌بندی فعالیت نشست با فیلدهای سازگار با جدول `user_activity` پستگرس. |
| وابستگی‌ها | `LocalDatabaseManager` |
| جایگاه معماری | مسیر مکمل/قدیمی. مسیر زنده ارگونومی از `ErgonomyManager.LogSessionState` و `UserActivityPayload` استفاده می‌کند. |

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | ثبت هویت و زمان شروع. |
| `StartSession` | رکورد Start صفر. |
| `UpdateActivityData` | رکورد میانی Update. |
| `EndSession` | رکورد End. |
| `QueueActivity` | ساخت شیء ناشناس و ذخیره در SQLite. |

---

### ۲.۸ `KafkaConnect.cs`

| فیلد | مقدار |
| --- | --- |
| فضای نام | `Ergonomy.Database` |
| پایه / واسط | `IDisposable` |
| مسئولیت | تولیدکننده کافکا با تأیید همه replicaها، ارسال idempotent و فشرده‌سازی Gzip. |
| وابستگی‌ها | `Confluent.Kafka`، `UserActivityPayload` |
| جایگاه معماری | مرز خروجی شبکه به سمت Kafka/ClickHouse. |

**اعضای مهم**

- تاپیک‌های `_userActivityTopic`، `_systemMetricsTopic`، `_appLogsTopic`.
- `_producer` با `Acks.All` و `EnableIdempotence`.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | اعتبارسنجی bootstrap/تاپیک و ساخت producer. |
| `SendUserActivityAsync` | ارسال payload فعالیت با کلید `messageId`. |
| `SendSystemMetricsAsync` | ارسال متریک سیستم. |
| `SendAppLogAsync` | ارسال لاگ برنامه. |
| `SendMessageAsync` | تحویل واقعی پیام و بازپرتاب خطای تحویل. |
| `RequireTopicName` | اجبار وجود نام تاپیک. |
| `ThrowIfDisposed` / `Dispose` | محافظت و flush نهایی. |

---

### ۲.۹ `LocalDatabaseManager.cs`

#### `QueueTargets`

ثابت‌های نام هدف صف: `advanced_system_metrics`، `user_activity`، `app_logs`.

#### شمارشی‌ها

| نوع | مسئولیت |
| --- | --- |
| `TargetPriority` | اولویت حذف در بحران: Critical / Medium / Low. |
| `CapacityStatus` | وضعیت ظرفیت: Normal / Warning / Critical. |
| `OutboxSaveResult` | نتیجه درج: Saved / DroppedLowPriority / Failed. |

#### ساختار `RetentionResult`

نتیجه یک دور نگهداری با تعداد حذف سنی و ظرفیتی. سازنده مقادیر را ذخیره می‌کند.

#### کلاس `LocalDatabaseManager`

| فیلد | مقدار |
| --- | --- |
| پایه / واسط | `IDisposable` |
| مسئولیت | outbox بادوام SQLite: درج اولویت‌دار، خواندن دسته، حذف پس از تحویل، نگهداری سنی/ظرفیتی. |
| وابستگی‌ها | `OutboxSettings`، `SqliteOutboxConnectionProvider`، `Microsoft.Data.Sqlite` |
| جایگاه معماری | بافر محلی قطعی بین جمع‌آوری و Kafka. بدون این لایه قطع شبکه باعث از دست رفتن داده می‌شود. |

**اعضای مهم**

- شمارنده‌های اتمی `_pendingCount`، `_droppedLowPriorityCount`، حذف سنی/ظرفیتی.
- `_cachedStatus` با بازه تازه‌سازی ۵ ثانیه.
- تایمر نگهداری بر اساس `RetentionCheckIntervalSeconds`.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده‌ها | مقداردهی مسیر، ایجاد جدول، همگام‌سازی شمارنده و شروع تایمر. |
| `OnRetentionTimerElapsed` | اجرای دوره‌ای نگهداری. |
| `CreateOpenConnection` | باز کردن اتصال WAL با busy_timeout. |
| `InitializeDatabase` | ایجاد جدول `sync_queue` و ایندکس‌ها. |
| `EnsureMessageIdColumn` | مهاجرت ستون `message_id`. |
| `GetColumnNames` | خواندن PRAGMA table_info. |
| `GetTargetPriority` | نگاشت هدف به اولویت حذف. |
| `GetDatabaseSizeBytes` | اندازه فایل DB + WAL. |
| `GetCapacityStatus` | محاسبه نسبت ظرفیت. |
| `SaveUserActivity` | درج JSON با gating اولویت. |
| `GetPendingRecords` | خواندن قدیمی‌ترین دسته. |
| `DeleteRecord` | حذف پس از تحویل یا مسموم بودن. |
| `RunRetention` | حذف سنی و در صورت نیاز ظرفیتی. |
| `DeleteExpiredRecords` | حذف بر اساس سن. |
| `DeleteLowPriorityToRelieve` | حذف app_logs سپس user_activity. |
| `DeleteOldestByTarget` | حذف محدود یک هدف. |
| `ReconcileCount` | اصلاح drift شمارنده. |
| `SerializePayload` | تبدیل شیء به JSON. |
| `Dispose` | توقف تایمر نگهداری. |

---

### ۲.۱۰ `SqliteOutboxConnectionProvider.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | تنها منبع معتبر مسیر و رشته اتصال outbox در `%ProgramData%\Ergonomy\ergonomy_local.db`. |
| جایگاه معماری | جداسازی محل فایل از منطق صف. |

**متدها:** سازنده پوشه را می‌سازد و `SqliteConnectionStringBuilder` را با cache اشتراکی پیکربندی می‌کند.

---

### ۲.۱۱ `SyncEngine.cs`

| فیلد | مقدار |
| --- | --- |
| پایه / واسط | `IDisposable` |
| مسئولیت | تخلیه قابل پیش‌بینی outbox به Kafka با دسته ۵۰تایی، حذف پس از تحویل، backoff نمایی و حذف رکورد سمی. |
| وابستگی‌ها | `LocalDatabaseManager`، `KafkaConnect`، `AgentMetrics` |
| جایگاه معماری | پل پایداری محلی و تحویل مرکزی. |

**اعضای مهم**

- `_syncGate` برای جلوگیری از همپوشانی دسته.
- `_consecutiveKafkaFailures` و `_backoffUntilUtc`.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | تنظیم فاصله پایه و وابستگی‌ها. |
| `Start` / `Stop` | کنترل حلقه پس‌زمینه. |
| `ForceSyncAsync` | همگام‌سازی فوری بدون backoff. |
| `UpdateSyncInterval` | تغییر فاصله حلقه. |
| `RunLoopAsync` | دور فوری و سپس PeriodicTimer. |
| `ProcessQueueAsync` | خواندن، ارسال، حذف، متریک. |
| `ApplyBackoff` | محاسبه تأخیر نمایی تا سقف ۳۰ دقیقه. |
| `SendRecordToKafkaAsync` | مسیریابی بر اساس `TargetTable`. |
| `HandlePoisonRecord` | حذف JSON نامعتبر. |
| `ThrowIfDisposed` / `Dispose` | ایمنی آزادسازی. |

---

### ۲.۱۲ `AdvancedMetricsCollector.cs`

#### کلاس `AdvancedMetricsCollector`

| فیلد | مقدار |
| --- | --- |
| فضای نام | سراسری (بدون namespace) |
| مسئولیت | جمع‌آوری متریک‌های سخت‌افزاری، امنیتی، فرایندی و شبکه‌ای ماشین بر اساس فهرست فعال تنظیمات. |
| وابستگی‌ها | WMI، LibreHardwareMonitor، Event Log، Performance Counter، SQLite مرورگرها |
| جایگاه معماری | منبع داده `advanced_system_metrics`. توسط `AdvancedMetricsWorker` فراخوانی می‌شود. |

**اعضای مهم**

- `_enabledMetrics`: مجموعه نرمال‌شده نام متریک‌ها.
- `_topProcessesCount` و `_targetIp`.

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | نرمال‌سازی نام متریک‌ها. |
| `Collect` | ساخت دیکشنری خروجی با زمان میلادی/شمسی و کلیدهای فعال. |
| `GetInteractiveWindowsUsername` / `GetExplorerUser` | شناسایی کاربر نشست دسکتاپ. |
| `GetDiskHealthStatus` | وضعیت دیسک از WMI. |
| `GetCriticalSystemEvents` | شمارش EventID 41 و 1001 در ۲۴ ساعت. |
| `CollectHardwareData` | CPU/RAM/دیسک/شبکه از LibreHardwareMonitor. |
| `GetCpuWmiDetails` | جزئیات پردازنده. |
| `GetDiskPerformanceMetrics` | شمارنده‌های PhysicalDisk. |
| `GetAdvancedSmartData` | ویژگی‌های SMART حیاتی. |
| `GetTopProcesses` | پرمصرف‌ترین فرایندها. |
| `GetDiskModels` / `GetDiskWmiInfo` / `GetGpuDetails` | موجودی سخت‌افزار. |
| `GetMotherboardSerial` / `IsValidSerial` | سریال معتبر برد. |
| `PerformNetworkTrace` | traceroute محدود. |
| `GetSecurityStatus` | آنتی‌ویروس/فایروال. |
| `GetFailedLoginsCount` | EventID 4625 در دو ساعت. |
| `GetUsbDevicesCount` | شمارش USB. |
| `GetBootTime` | زمان بوت از TickCount64. |
| `GetTotalThreads` / `GetTotalHandles` | شمارش سراسری. |
| `GetAllBrowsersHistoryLast24Hours` و توابع کمکی مرورگر | مسیر توسعه‌ای خواندن تاریخچه؛ در `Collect` فعلی فراخوانی نمی‌شود. |
| `CopyIfExists` | کپی امن فایل SQLite مرورگر. |

#### کلاس `UpdateVisitor`

پیاده‌سازی `IVisitor` برای به‌روزرسانی درخت حسگر LibreHardwareMonitor.

| متد | مسئولیت |
| --- | --- |
| `VisitComputer` | شروع پیمایش. |
| `VisitHardware` | Update قطعه و زیرقطعات. |
| `VisitSensor` / `VisitParameter` | بدون پردازش اضافه. |

---

## ۳. پیکربندی

### ۳.۱ `Configuration/AppDefaults.cs`

#### `AppDefaults`

نرمال‌سازی و اعتبارسنجی متمرکز `AppSettings` تا همه بارگذارها قواعد یکسانی داشته باشند.

| متد | مسئولیت |
| --- | --- |
| `Normalize` (int/double) | جایگزینی مقدار نامعتبر با پیش‌فرض. |
| `Apply` | نرمال کردن فاصله‌های همگام‌سازی، متریک، تنظیمات و خواب. |
| `ValidateRequired` | اجبار وجود API و Kafka bootstrap. |

#### `SettingsValidationException`

استثنای تنظیم ناقص با سازنده پیامی.

---

### ۳.۲ `Configuration/EnvironmentSettingsProvider.cs`

| فیلد | مقدار |
| --- | --- |
| نوع | static class |
| مسئولیت | خواندن تنظیمات **فقط** از Environment Variables سطح ماشین؛ بدون فایل JSON. |
| جایگاه معماری | منبع بوت‌استرپ تغییرناپذیر زیرساخت. |

**متدها:** `GetEnv`، `GetBool`، `GetDouble`، `GetInt`، `GetString`، `Load`، `ParseEnabledMetrics`.

---

### ۳.۳ `Configuration/SettingsService.cs`

#### واسط `ISettingsService`

قرارداد منبع واحد تنظیمات مؤثر، بوت‌استرپ، پرچم منبع API، رویداد تغییر، بارگذاری بوت‌استرپ و تازه‌سازی ناهمگام از API.

#### کلاس `SettingsService`

| فیلد | مقدار |
| --- | --- |
| پایه / واسط | `ISettingsService`، `IDisposable` |
| مسئولیت | نگه‌داری تنظیمات مؤثر، تازه‌سازی از API، حفظ زیرساخت محیطی و سوئیچ‌های امنیتی. |
| وابستگی‌ها | `HttpClient`، `EnvironmentSettingsProvider`، `AppDefaults` |

**متدها**

| متد | مسئولیت |
| --- | --- |
| سازنده | تزریق HTTP و لاگر. |
| `LoadBootstrap` | بارگذاری و اعمال پیش‌فرض‌ها. |
| `TryValidate` | اعتبارسنجی غیرپرتابی. |
| `RefreshFromApiAsync` | دریافت، حفظ زیرساخت، مقایسه JSON و اعلام تغییر. |
| `PreserveEnvironmentInfrastructureSettings` | بازنویسی API/Kafka/سوئیچ‌ها از بوت‌استرپ. |
| `NormalizeSettings` | سریال‌سازی برای مقایسه. |
| `Dispose` | آزادسازی قفل تازه‌سازی. |

---

### ۳.۴ مدل‌های تنظیمات در `Ergonomy.Core/Configuration`

| کلاس | مسئولیت |
| --- | --- |
| `AppSettings` | مدل کامل تنظیمات اجرایی، مجوزها، آلارم، متریک، زمان‌بندی و زیرساخت. |
| `ApiSettings` | آدرس‌های Settings / LoadImages / Commands. |
| `KafkaSettings` | Bootstrap و نام تاپیک‌ها. |
| `DatabaseSettings` | مشخصات PostgreSQL. |
| `ImageSettings` | مسیرهای تصویر هشدار. |
| `OutboxSettings` | سقف رکورد، حجم، سن و آستانه‌های outbox. |

این کلاس‌ها DTO هستند و متد اجرایی ندارند.

---

## ۴. هوک ورودی و ثبت محلی

### ۴.۱ `Hooks/GlobalInputHook.cs`

#### `GlobalInputHook`

| فیلد | مقدار |
| --- | --- |
| پایه / واسط | `IDisposable` |
| مسئولیت | نصب هوک‌های `WH_KEYBOARD_LL` و `WH_MOUSE_LL` روی نخ اختصاصی با حلقه پیام بومی. callbackها فقط افزایش اتمی انجام می‌دهند. |
| جایگاه معماری | پایین‌ترین لایه جمع‌آوری فعالیت. باید روی دسکتاپ تعاملی اجرا شود؛ نشست صفر آن را نمی‌بیند. |

**اعضای مهم**

- شمارنده‌های اتمی رویداد و پیکسل حرکت.
- `_startCompletedEvent` برای همگام‌سازی نصب با فراخواننده.
- ساختارهای P/Invoke: `POINT`، `MSG`، `MSLLHOOKSTRUCT`.

**متدها:** سازنده، `Start`، `Stop`، `HookThreadEntryPoint`، `DescribeWin32Error`، `CleanupHooksOnHookThread`، `ConsumeSnapshot`، `ResetCounters`، `SetHook`، `KeyboardHookCallback`، `MouseHookCallback`، `Dispose`، `PackPoint`، `UnpackX`، `UnpackY` و ده متد `DllImport` زنجیره هوک/صف پیام.

#### `InputActivitySnapshot`

ساختار فقط‌خواندنی یک پنجره نمونه‌برداری با پرچم‌های `HasKeyboardActivity` و `HasMouseActivity`.

---

### ۴.۲ `Hooks/ActivityMonitor.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | نمونه‌برداری یک‌ثانیه‌ای از اسنپ‌شات هوک و تجمع زمان فعال صفحه‌کلید/ماوس. |
| وابستگی‌ها | `GlobalInputHook` |
| جایگاه معماری | لایه تبدیل رویداد خام به زمان فعال قابل مقایسه با آستانه. |

**متدها:** سازنده، `Start`، `Stop`، `ResetTotals`، `OnSampleTimerElapsed`، `Dispose`.

---

### ۴.۳ `Logging/DataLogger.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | نوشتن ساعتی یک فایل اکسل محلی با زمان تهران/شمسی شامل ثانیه‌های فعالیت و شمارنده بستن. |
| وابستگی‌ها | `ActivityMonitor`، EPPlus، `AppSettings` |
| جایگاه معماری | مسیر تشخیصی محلی جدا از Kafka. شکست نوشتن فایل نادیده گرفته می‌شود. |

**متدها:** سازنده، `GetIntervalMs`، `UpdateSettings`، `Start`، `Stop`، `OnLogTimerElapsed`، `LogData`، `Dispose`.

---

## ۵. سرویس‌ها و کارگران

### ۵.۱ `Services/ServiceRegistrar.cs`

| فیلد | مقدار |
| --- | --- |
| نوع | static |
| مسئولیت | ریشه ترکیب DI برنامه میراثی. |
| جایگاه معماری | تنها جایی که اشیای بلندعمر ساخته و به هم وصل می‌شوند. |

**متدها:** `Build`، `GetWindowsSID`، `GetWindowsUsername`.

---

### ۵.۲ `Services/WorkerBase.cs`

| فیلد | مقدار |
| --- | --- |
| نوع | abstract |
| پایه / واسط | `IDisposable` |
| مسئولیت | الگوی مشترک کارگران دوره‌ای: حلقه پس‌زمینه، PeriodicTimer پویا، گرفتن استثنا، توقف بدون بن‌بست. |

**متدها:** سازنده، `Start`، `RunLoopAsync`، `RunIterationSafelyAsync`، `DoWorkAsync` (abstract)، `GetInterval` (abstract)، `Stop`، `ThrowIfDisposed`، `Dispose`.

---

### ۵.۳ کارگران مشتق‌شده

| کلاس | فایل | فاصله | کار هر دور |
| --- | --- | --- | --- |
| `SettingsRefreshWorker` | `Services/SettingsRefreshWorker.cs` | `SettingsCheckIntervalSeconds` | `RefreshFromApiAsync` |
| `HealthMonitorWorker` | `Services/HealthMonitorWorker.cs` | ۱۵ دقیقه + دور فوری | `HealthCheckService.RunAllAsync` |
| `PermissionMonitorWorker` | `Services/PermissionMonitorWorker.cs` | حداقل فاصله بازبینی SQLite/Kafka | `PermissionsEvaluator.EvaluateAll` |
| `AdvancedMetricsWorker` | `Services/AdvancedMetricsWorker.cs` | `AdvancedMetricsIntervalMinutes` | `AdvancedMetricsCollector.Collect` و ذخیره در outbox |

هر کلاس سازنده، `GetInterval` و `DoWorkAsync` دارد.

---

### ۵.۴ `Services/PermissionsEvaluator.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | ارزیابی سوئیچ‌های `AllowSqliteWrite`، `AllowKafkaWrite` و `AllowErgonomyCollection` و شروع/توقف اجزای متناظر. |
| جایگاه معماری | سیاست زمان اجرا. جایگزین تایمرهای جداگانه مجوز در پوسته قدیمی. |

**متدها:** سازنده، `EvaluateAll`، `EvaluateSqlitePermission`، `EvaluateKafkaPermission`، `EvaluateErgonomyPermission`، `StartLocalDataCollection`، `StopAllDataCollection`، `SetLocalCollectionRunning`، `StopAll`.

---

### ۵.۵ `Services/HealthCheckService.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | پروب سلامت API تنظیمات، SQLite outbox و مصرف حافظه خود عامل؛ ثبت نتیجه در `app_logs`. |
| اعضای مهم | `OnSqliteCriticalFailure` برای بیدار کردن چرخه خواب پوسته. |

**متدها:** سازنده، `RunAllAsync`، `CheckApiHealthAsync`، `CheckSqliteHealthAsync`، `CheckSelfPerformanceAsync`.

---

### ۵.۶ `Services/MessageLogService.cs`

| فیلد | مقدار |
| --- | --- |
| مسئولیت | کانال مشترک تشخیصی کنسول + outbox `app_logs` با برچسب هویت ماشین و تاریخ شمسی. |

**متدها:** سازنده، `Log`، `LogHealth`، `Dispose`.

---

### ۵.۷ `Services/MachineIdentity.cs`

هویت پایدار SID، نام کاربری، نام ماشین و `SessionGuid` یکتا برای برچسب payload. سازنده مقادیر را ثابت می‌کند.

---

### ۵.۸ `Services/WakeUpScheduler.cs`

زمان‌بند یک‌بارمصرف خواب اضطراری. متدها: سازنده، `Schedule`، `Stop`، `Dispose`.

---

## ۶. مشاهده‌پذیری

### ۶.۱ `Observability/AgentMetrics.cs`

رجیستری درون‌فرایندی thread-safe برای exposition متنی پرومتئوس. از Kafka/SQLite برای مشاهده‌پذیری استفاده نمی‌کند.

کلاس‌های داخلی:

| کلاس | مسئولیت |
| --- | --- |
| `AgentMetrics` | شمارنده/گیج و رندر متن. |
| `MetricFamily` | خانواده یک نام متریک. |
| `MetricPoint` | نقطه دارای برچسب با مقدار قابل قفل. |
| `Counter` | دستگیره سبک نام شمارنده. |

**متدهای مهم:** `IncrementCounter` (دو سربار)، `SetGauge` (دو سربار)، `IncrementGauge`، `GetOrAddFamily`، `RenderPrometheusText`، `EscapeLabel`، `AddPoint`، `GetOrderedPoints`، `Add`، `Set`.

---

### ۶.۲ `Observability/MetricsConfig.cs`

پیکربندی درگاه، محیط و شناسه عامل از متغیرهای محیطی ماشین. متدها: سازنده و `FromEnvironment`.

---

### ۶.۳ `Observability/MetricsEndpoint.cs`

شنونده `HttpListener` برای `/metrics` با تخلیه درخواست‌ها هنگام خاموشی. در صورت شکست ACL سراسری به loopback برمی‌گردد.

**متدها:** سازنده، `Start`، `TryStart`، `ServeLoopAsync`، `TrackRequest`، `HandleRequestAsync`، `RenderWithIdentity`، `Escape`، `Stop`، `Dispose`.

#### `MachineIdentityLabels`

برچسب‌های کم‌کاردینالیتی `machine` / `environment` / `agent_id`.

---

## ۷. رابط کاربری

### ۷.۱ `UI/PrimaryAlarmForm.cs` (+ Designer)

فرم هشدار اولیه نرمش. روی دسکتاپ کاربر، همیشه روی سایر پنجره‌ها، با بستن خودکار و بیشینه‌سازی سفارشی نصف صفحه.

**متدها:** سازنده، `AlarmForm_Load`، `WndProc`، `AlarmForm_Resize`، `OnFormClosed`، `InitializeComponent`، `Dispose`.

---

### ۷.۲ `UI/SecondaryAlarmForm.cs` (+ Designer)

هشدار ثانویه پس از رسیدن به حد بستن هشدار اولیه. تا پایان دوره قفل فرمان بستن سیستم نادیده گرفته می‌شود.

**متدها:** مشابه فرم اولیه به‌علاوه تایمر غیرقابل‌بستن.

---

### ۷.۳ `UI/MessageAlarmForm.cs`

فرم پیام مدیریتی برای فرمان راه دور `msg:`. در نخ STA جداگانه توسط `CommandManager` اجرا می‌شود و بخشی از مسیر آستانه ارگونومی نیست.

**متدها:** سازنده، `SetupUI`، `MessageAlarmForm_Load`.

---

## ۸. `Ergonomy.Core` — قرارداد و IPC

### ۸.۱ قرارداد همگام‌سازی `Contracts/SyncModels.cs`

| کلاس | مسئولیت |
| --- | --- |
| `SyncRecord` | ردیف صف outbox: Id، MessageId، TargetTable، Payload، CreatedAt. |
| `UserActivityPayload` | بدنه استاندارد فعالیت کاربر برای Kafka/ClickHouse. |

---

### ۸.۲ ثابت‌ها و پاکت IPC

| کلاس | مسئولیت |
| --- | --- |
| `IpcConstants` | نام پایپ `Ergonomy.Agent.v1`، سقف فریم ۲۵۶KB، backoff و فاصله heartbeat. متد ندارد. |
| `IpcMessageTypes` | کاتالوگ نوع پیام‌های Task↔Service. |
| `IpcMessage` | پاکت JSON با `Create` و `GetPayload`. |
| `IpcSerializer` | گزینه‌های JSON مشترک دو طرف. |

---

### ۸.۳ قراردادهای payload در `IpcContracts.cs`

`TaskHelloPayload`، `HelloAckPayload`، `HeartbeatPayload`، `ActivityReportPayload`، `ShowAlarmPayload`، `AlarmAckPayload`، `SettingsSnapshotPayload`، `ShutdownRequestPayload`، `GoodbyePayload` و شمارشی `AlarmKind`. همه DTO هستند.

---

### ۸.۴ `IpcFraming` و `IpcProtocolException`

فریم‌بندی طول‌پیشوند ۴ بایتی little-endian. متدها: `WriteFrameAsync`، `ReadFrameAsync`، `ReadExactlyAsync`. استثنا دو سازنده دارد.

---

### ۸.۵ `IpcSecurityFactory`

ساخت ACL کمینه‌امتیاز و نمونه سرور با `NamedPipeServerStreamAcl`. متدها: `CreatePipeSecurity`، `CreateServerStream`.

---

### ۸.۶ `IpcConnection`

یک اتصال زنده با سریال‌سازی نوشتن. متدها: سازنده، `SendAsync`، `ReceiveAsync`، `Dispose`.

---

### ۸.۷ `NamedPipeIpcClient`

کلاینت فرایند Task با اتصال مجدد نمایی. ارسال هرگز نخ UI را مسدود نمی‌کند (`TrySendAsync` در قطع بودن false می‌دهد).

**متدها:** سازنده، `Start`، `RunAsync`، `PumpAsync`، `TrySendAsync`، `StopAsync`، `Dispose`.

---

### ۸.۸ `NamedPipeIpcServer`

سرور فرایند Service با حداکثر ۸ نمونه (یک Task به ازای هر نشست ورود).

**متدها:** سازنده، `Start`، `AcceptLoopAsync`، `PumpConnectionAsync`، `BroadcastAsync`، `SendAsync`، `SendSafeAsync`، `StopAsync`، `DelayQuietAsync`، `Dispose`.

---

### ۸.۹ لاگ ساختاریافته

| کلاس | مسئولیت |
| --- | --- |
| `ConsoleStructuredLogProvider` | `ILoggerProvider` سبک برای stdout. |
| `ConsoleStructuredLogger` | قالب‌بندی `[زمان] [سطح] [دسته]`. |
| `LogEvents` | EventIdهای پایدار چرخه حیات، همگام‌سازی و فرمان. متد ندارد. |

**متدهای لاگر:** `CreateLogger`، `Dispose`، سازنده، `BeginScope`، `IsEnabled`، `Log`، `BuildLine`.

---

## ۹. `Ergonomy.Service`

### ۹.۱ `Ergonomy.Service/Program.cs`

نقطه ورود سرویس پس‌زمینه. میزبان جنریک با `UseWindowsService` در دو حالت SCM و کنسول تعاملی.

**متدها:** `Main`، `IsRunningAsWindowsService`، `GetParentProcessId`، `NtQueryInformationProcess`.

---

### ۹.۲ `Hosting/IpcHostedService.cs`

پل `IHostedService` بین عمر Windows Service و سرور پایپ.

**متدها:** سازنده، `StartAsync`، `StopAsync` (پخش shutdown و سپس توقف سرور).

---

### ۹.۳ `Ipc/ServiceIpcHost.cs`

روتر سمت سرویس. درز مهاجرت: `ActivityReceived`، `AlarmAcknowledged` و `SettingsSnapshotProvider` هنوز به کارگران میراثی وصل نشده‌اند.

**متدها:** سازنده، `Start`، `StopAsync`، `ShowAlarmAsync`، `PublishSettingsAsync`، `RequestTaskShutdownAsync`، `OnClientConnected`، `OnClientDisconnected`، `OnMessageAsync`، `HandleHelloAsync`، `Dispose`.

---

## ۱۰. `Ergonomy.Task`

### ۱۰.۱ `Ergonomy.Task/Program.cs`

نقطه ورود فرایند تعاملی. Mutex `Local\Ergonomy.Task.SingleInstance.v1` تک‌نمونه‌ای بودن هر نشست را تضمین می‌کند.

**متدها:** `Main`، `BuildServiceProvider`.

---

### ۱۰.۲ `TaskApplicationContext.cs`

| فیلد | مقدار |
| --- | --- |
| پایه | `ApplicationContext` |
| مسئولیت | نگه‌داشتن کلاینت پایپ، hello/heartbeat، دریافت اسنپ‌شات تنظیمات و marshalling هشدار به نخ UI. |
| وضعیت مهاجرت | فرم‌های واقعی هنوز منتقل نشده‌اند؛ `ShowAlarm` پاسخ `alarm-forms-not-yet-migrated` می‌فرستد. |

**متدها:** سازنده، `ReportActivityAsync`، `OnConnected`، `SendHelloAsync`، `SendHeartbeatAsync`، `OnMessageAsync`، `ShowAlarm`، `MarshalToUi`، `TryGetSid`، `TryGetUsername`، `Dispose`.

---

## ۱۱. نقشه همکاری کلاس‌ها

```
                    ┌──────────────────────────┐
                    │ Environment / Settings API│
                    │  (ماشین + PostgreSQL)     │
                    └────────────┬─────────────┘
                                 │
                    SettingsService / AppDefaults
                                 │
        ┌────────────────────────┼────────────────────────┐
        │                        │                        │
 PermissionsEvaluator     CommandManager          Workers (Settings/
        │                        │                 Health/Permission/
        ├─ ErgonomyManager       ├─ MessageAlarmForm     AdvancedMetrics)
        │    ├ ActivityMonitor   └─ shutdown.exe              │
        │    │    └ GlobalInputHook                     AdvancedMetricsCollector
        │    ├ AlarmManager + Forms                              │
        │    └ DataLogger                                        │
        │                                                        │
        └──────────────┬─────────────────────────────────────────┘
                       ▼
              LocalDatabaseManager (SQLite outbox)
                       │
                   SyncEngine
                       │
                  KafkaConnect
                       │
              Kafka → ClickHouse
```

در معماری هدف، نیمه راست و پایین این نمودار به `Ergonomy.Service` و نیمه هوک/UI به `Ergonomy.Task` منتقل می‌شود و ارتباط فقط از مسیر Named Pipe خواهد بود.

---

## ۱۲. جمع‌بندی کمی بررسی

| شاخص | مقدار |
| --- | --- |
| فایل‌های `#C` بررسی‌شده | ۵۵ |
| انواع مستندشده (کلاس / ساختار / واسط / شمارشی) | ۹۰ نوع یکتا (۹۲ اعلان با احتساب partial طراح) |
| کلاس/ساختار/واسط اصلی | حدود ۷۵ نوع دارای مسئولیت اجرایی یا مدل داده |
| متدهای دارای توضیحات XML فارسی | ۲۹۱ |

---

## ۱۳. نقاط مبهم یا نیازمند بازبینی دستی

1. **`SessionManager` در مسیر زنده استفاده نمی‌شود.** مسیر فعلی `ErgonomyManager.LogSessionState` است. رفتار و شکل payload این دو مسیر یکسان نیست؛ قبل از حذف یا اتصال دوباره باید مالک محصول تصمیم بگیرد.
2. **`DatabaseManager` مسیر مکمل/قدیمی PostgreSQL است.** فرمان‌های زنده از API و `CommandManager` می‌آیند. متدهای `GetPendingCommands` / `MarkCommandAs*` ممکن است مرده باشند.
3. **تاریخچه مرورگر در `AdvancedMetricsCollector`.** توابع `GetAllBrowsersHistoryLast24Hours` و خواندن SQLite کروم/اج/فایرفاکس پیاده‌سازی شده‌اند اما در `Collect` کامنت شده‌اند. فعال‌سازی آن‌ها پیامد حریم خصوصی و حقوقی دارد.
4. **`CommandManager` به `LocalDatabaseManager` وابسته است ولی در بدنه فعلی از آن استفاده نمی‌کند.** وابستگی احتمالاً برای سازگاری مهاجرت باقی مانده است.
5. **مهاجرت دوفرایندی ناقص است.** `Ergonomy.Service` فقط IPC را میزبانی می‌کند و `TaskApplicationContext.ShowAlarm` هنوز فرم واقعی ندارد. اجرای موازی سه باینری رفتار تولید کامل را تکرار نمی‌کند.
6. **`AdvancedMetricsCollector` فضای نام ندارد** و برخی حسگرها به دسترسی مدیر نیاز دارند (SMART، Security Event Log). شکست‌ها silently به مقدار پیش‌فرض تبدیل می‌شوند.
7. **`DataLogger` شکست نوشتن اکسل را می‌بلعد.** مسیر فایل کنار exe ممکن است در نصب Program Files غیرقابل‌نوشتن باشد.
8. **پروب SQLite در `HealthCheckService` رشته اتصال کامل را به‌عنوان «مسیر» باز می‌کند** که درست است، اما نام ویژگی `OutboxDatabasePathForDiagnostics` ممکن است با `ConnectionString` اشتباه گرفته شود.
9. **سوئیچ‌های قدرت سیستم واقعاً `shutdown.exe` را صدا می‌زنند.** مستندسازی رفتار را تغییر نداده؛ بازبینی عملیاتی این مسیر توصیه می‌شود.
10. **در این محیط SDK دات‌نت موجود نبود** و کامپایل انجام نشد. صحت نحوی XML comments باید روی میزبان Windows/.NET 9 با `dotnet build` تأیید شود.

--------------------------------------------------------------------------------------------------------------------------------------------------------
ساده شده


# گزارش ساده‌سازی شده بخش‌های پروژه Ergonomy (ارگونومی)

**تاریخ تهیه:** ۱۴۰۵/۰۶/۰۳ (2026-08-25)  
**مخاطب:** مدیران، طراحان و افراد بدون دانش تخصصی برنامه‌نویسی  
**هدف این سند:** توضیح وظایف بخش‌های مختلف (کلاس‌های) برنامه ارگونومی به زبان ساده و با استفاده از مثال‌های روزمره.

---

## ۱. نمای کلی سیستم (این برنامه چه کار می‌کند؟)

برنامه **ارگونومی** مثل یک «مراقب سلامت جسمی» و «ناظر فنی» برای کامپیوترهای شرکت است. این برنامه دو وظیفه اصلی دارد:
1. **مراقبت از کاربر:** بررسی می‌کند کاربر چقدر بی‌وقفه با موس و کیبورد کار کرده است و اگر از حد مجاز گذشت، روی صفحه به او هشدار می‌دهد که نرمش کند.
2. **مراقبت از کامپیوتر:** سلامت قطعات کامپیوتر (مثل هارد، حافظه، شبکه) را بررسی کرده و این اطلاعات را به سرور مرکزی شرکت می‌فرستد.

**تغییرات جدید برنامه (اسباب‌کشی داخلی):**
قبلاً تمام این کارها توسط یک برنامه یکپارچه انجام می‌شد. اما حالا برنامه‌نویسان در حال تقسیم آن به دو بخش هستند:
*   **بخش موتورخانه (Ergonomy.Service):** یک بخش نامرئی که همیشه در پس‌زمینه روشن است و کارهای سنگین (مثل جمع‌آوری اطلاعات و ارسال به سرور) را انجام می‌دهد.
*   **بخش ظاهری (Ergonomy.Task):** بخشی که کاربر می‌بیند و فقط وظیفه دارد هشدارها و پیام‌ها را روی صفحه نشان دهد.

---

## ۲. بخش‌های اصلی برنامه (پروژه قدیمی و یکپارچه)

در برنامه‌نویسی، وظایف بین فایل‌های مختلفی به نام «کلاس» تقسیم می‌شود. در اینجا این کلاس‌ها را مثل کارمندان یک شرکت معرفی می‌کنیم:

### ۲.۱ `Program.cs` و `MainApplicationContext.cs` (بخش مدیریت کل)
*   **وظیفه:** این دو بخش مثل **مدیرعامل** و **کلید اصلی برق** هستند.
*   **توضیح ساده:** وقتی کامپیوتر روشن می‌شود، این بخش برنامه را بیدار می‌کند، آیکون آن را در گوشه تصویر می‌آورد و به بقیه بخش‌ها (کارمندان) دستور می‌دهد که کارشان را شروع کنند.

### ۲.۲ `ErgonomyManager.cs` (مغز متفکر و هماهنگ‌کننده)
*   **وظیفه:** محاسبه‌گر اصلی زمان کار و استراحت.
*   **توضیح ساده:** این بخش اطلاعات را از سنسورها می‌گیرد و با یک کرنومتر حساب می‌کند که آیا زمان کار پیوسته کاربر از حد مجاز گذشته است یا نه. اگر گذشته باشد، به بخش نمایش دستور می‌دهد که عکس‌های نرمش را روی صفحه بیاورد.

### ۲.۳ `AlarmManager.cs` (بخش هشدارها)
*   **وظیفه:** مسئول نمایش عکس‌های نرمش و هشدارهای سلامتی.
*   **توضیح ساده:** این بخش مجموعه‌ای از عکس‌های ورزشی را از سرور می‌گیرد. وقتی زمان استراحت فرا می‌رسد، این عکس‌ها را به صورت تمام‌صفحه به کاربر نشان می‌دهد تا او را مجبور به استراحت کند.

### ۲.۴ `CommandManager.cs` (گیرنده پیام‌های از راه دور)
*   **وظیفه:** دریافت دستورات از مدیران شبکه.
*   **توضیح ساده:** مثل یک دستگاه فکس یا پیجر است. هر چند ثانیه چک می‌کند که آیا مدیر شبکه دستور خاصی (مثلاً خاموش کردن کامپیوتر یا نمایش یک پیام متنی) فرستاده است یا خیر، و اگر مجاز باشد، آن را اجرا می‌کند.

### ۲.۵ `DatabaseManager` و `LocalDatabaseManager` (دفتردار و بایگانی محلی)
*   **وظیفه:** ذخیره موقت اطلاعات در خود کامپیوتر.
*   **توضیح ساده:** اگر اینترنت قطع شود، برنامه نمی‌تواند اطلاعات سلامت کامپیوتر و ساعات کار را به شرکت بفرستد. این بخش مثل یک دفترچه یادداشت عمل می‌کند و اطلاعات را موقتاً می‌نویسد تا به محض وصل شدن اینترنت، آن‌ها را ارسال کند. وقتی اطلاعات با موفقیت ارسال شد، آن‌ها را از دفترچه پاک می‌کند تا جا باز شود.

### ۲.۶ `KafkaConnect` و `SyncEngine` (پستچی‌ها)
*   **وظیفه:** ارسال اطلاعات به سرور مرکزی.
*   **توضیح ساده:** این دو بخش بسته‌های اطلاعاتی (که دفتردار در دفترچه نوشته بود) را برمی‌دارند و با امنیت کامل به سرور اصلی شرکت پست می‌کنند.

### ۲.۷ `AdvancedMetricsCollector` (دکتر کامپیوتر)
*   **وظیفه:** معاینه قطعات سخت‌افزاری.
*   **توضیح ساده:** این بخش کاری به کاربر ندارد؛ بلکه داخل خود کیس کامپیوتر را چک می‌کند! دمای پردازنده، میزان پر بودن هارد دیسک، وضعیت آنتی‌ویروس و اتصالات اینترنت را بررسی می‌کند تا اگر کامپیوتر در حال خرابی است، قبل از وقوع فاجعه مدیران را باخبر کند.

---

## ۳. بخش تنظیمات و قوانین (Configuration)

این بخش‌ها (`AppSettings`، `SettingsService` و ...) حکم **کتابچه قوانین شرکت** را دارند.
*   **توضیح ساده:** تعیین می‌کنند که مثلاً آستانه خستگی کاربر چند دقیقه است؟ عکس‌های نرمش از چه آدرسی دانلود شوند؟ آیا برنامه اجازه دارد کامپیوتر را خاموش کند؟ این قوانین از سرور مرکزی دانلود می‌شوند و برنامه طبق آن‌ها رفتار می‌کند.

---

## ۴. چشم‌ها و سنسورهای برنامه (Hooks)

### ۴.۱ `GlobalInputHook` و `ActivityMonitor`
*   **وظیفه:** شمارش کلیک‌ها و تایپ‌ها.
*   **توضیح ساده:** این‌ها مثل دوربین‌های مداربسته‌ای هستند که فقط **تعداد** حرکات را می‌شمارند، **نه محتوای آن‌ها را**. یعنی اصلاً کاری ندارند شما چه کلمه‌ای تایپ می‌کنید یا به چه سایتی می‌روید؛ فقط می‌فهمند که "آیا کاربر الان پشت سیستم است و دارد کار می‌کند یا سیستم رها شده است؟"

---

## ۵. کارمندان پشت صحنه (Workers)

برنامه چندین «کارگر» (`Worker`) دارد که کارهای تکراری انجام می‌دهند:
*   **کارگر تنظیمات (`SettingsRefreshWorker`):** هر چند دقیقه چک می‌کند آیا قوانین جدیدی از سمت مدیران آمده است؟
*   **کارگر سلامت (`HealthMonitorWorker`):** هر ۱۵ دقیقه چک می‌کند که خودِ برنامه ارگونومی هنگ نکرده باشد.
*   **کارگر آمار سخت‌افزار (`AdvancedMetricsWorker`):** هر چند ساعت یک‌بار، دکتر کامپیوتر را صدا می‌زند تا سیستم را معاینه کند.

---

## ۶. بخش رابط کاربری و فرم‌ها (UI)

این بخش‌ها همان چیزهایی هستند که کاربر با چشم می‌بیند:
*   `PrimaryAlarmForm`: همان صفحه بزرگی که عکس نرمش را نشان می‌دهد.
*   `SecondaryAlarmForm`: اگر کاربر به هشدار اول بی‌توجهی کند، این صفحه دوم با سخت‌گیری بیشتری روی صفحه ظاهر می‌شود و به راحتی بسته نمی‌شود.
*   `MessageAlarmForm`: یک کادر پیام است که مدیر شبکه می‌تواند از راه دور متنی را برای کاربر بفرستد (مثلاً: "لطفاً سیستم را تا ۱۰ دقیقه دیگر ری‌استارت کنید").

---

## ۷. ارتباطات داخلی (نسخه جدید برنامه)

همان‌طور که در ابتدا گفته شد، برنامه در حال تبدیل شدن به دو تکه است (موتورخانه و ظاهر). برای اینکه این دو تکه با هم حرف بزنند، بخش‌هایی در کد نوشته شده که مثل **تلفن داخلی** عمل می‌کنند:
*   کلاس‌های `NamedPipeIpcClient` و `NamedPipeIpcServer` وظیفه دارند پیام‌ها را بین موتورخانه (که پنهان است) و ظاهر برنامه رد و بدل کنند. مثلاً موتورخانه در بیسیم می‌گوید: "زمان استراحت است!" و بخش ظاهری فوراً عکس را روی صفحه می‌آورد.

---

## ۸. نکات مهم برای مدیران (نقاط نیازمند تصمیم‌گیری)

در حین ساده‌سازی این کدها، چند نکته پیدا شد که مدیران یا صاحبان محصول باید درباره آن‌ها تصمیم بگیرند:
1.  **بخش‌های بی‌استفاده:** بخش‌هایی از برنامه (مثل `SessionManager`) وجود دارند که دیگر استفاده نمی‌شوند و فقط فضا را اشغال کرده‌اند. بهتر است پاک شوند.
2.  **تاریخچه مرورگر:** در بین کدهای دکتر کامپیوتر، بخشی وجود دارد که می‌تواند تاریخچه اینترنت کاربر (سایت‌های بازدید شده) را بخواند. این ویژگی در حال حاضر **خاموش** است. روشن کردن آن نیازمند هماهنگی‌های حقوقی و حفظ حریم خصوصی کارمندان است.
3.  **اسباب‌کشی نیمه‌کاره:** تغییر برنامه از حالت یکپارچه به دو تکه، هنوز کاملاً تمام نشده است و در برخی سیستم‌ها ممکن است هشدارها به درستی نمایش داده نشوند تا زمانی که برنامه‌نویسان این کار را تکمیل کنند.