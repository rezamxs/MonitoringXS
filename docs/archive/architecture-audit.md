# ممیزی معماری، ماژولار بودن و قابلیت نگهداری Monitoring XS

این سند نتیجه ممیزی عمیق معماری، وابستگی‌ها و کیفیت تفکیک ماژول‌ها در مخزن Monitoring XS است.

---

## ۱. ساختار کلان و الگوی معماری

پروژه Monitoring XS بر پایه الگوی **Modular Monolith با مرزهای لایه‌ای سخت‌گیرانه (Inward Dependency)** پیاده‌سازی شده است. مرزهای وابستگی بین پروژه‌ها توسط آزمون‌های ماشینی ArchUnitNET در پروژه `MonitoringXS.ArchitectureTests` محافظت می‌شوند.

```text
MonitoringXS.App (پوسته WinUI 3 و Composition Root)
    │
    ├──> MonitoringXS.DesignSystem (توکن‌های بصری و استایل‌ها)
    │
    ├──> MonitoringXS.Application (هماهنگ‌کننده پایپ‌لاین و هاب اسنپ‌شات)
    │        │
    │        ├──> MonitoringXS.Core (مدل‌های داده تغییرناپذیر و Abstractionها)
    │        │
    │        ├──> MonitoringXS.Collectors (محاسبه دلتا و تجمیع متریک‌ها)
    │        │        └──> MonitoringXS.Core
    │        │
    │        └──> MonitoringXS.Storage (پایداری دیتابیس و تنظیمات)
    │                 └──> MonitoringXS.Core
    │
    ├──> MonitoringXS.Platform.Windows (پیاده‌سازی Win32/ETW/PDH برای قراردادهای Core)
    │        └──> MonitoringXS.Core
    │
    └──> MonitoringXS.PrivilegedBroker (سرویس مجزای ویندوز برای دسترسی سطح کرنل)
             ├──> MonitoringXS.Platform.Windows
             └──> MonitoringXS.Core
```

---

## ۲. مشخصات و مسئولیت هر لایه

### ۲.۱. پروژه `MonitoringXS.Core`
* **مسئولیت:** تعریف هسته دامنه (Domain Core)، مدل‌های داده تغییرناپذیر (`record`)، اینترفیس‌ها و قراردادهای ارتباطی (`Abstractions`).
* **ورودی و خروجی:** ساختارهای داده خالص مانند `ProcessDescriptor`، `ApplicationIdentity`، `MonitoringSnapshot` و اینترفیس‌هایی مثل `IProcessDiscoveryService` و `IMetricHistoryStore`.
* **وابستگی به بیرون:** **صفر.** هیچ وابستگی پروژه‌ای در این لایه وجود ندارد و مستقل از هر فریم‌ورک سیستم‌عامل یا UI کامپایل می‌شود.
* **آزمون‌پذیری:** ۱۰۰ درصد مستقل و سریع، دارای ۱۱ آزمون واحد مستقل در `MonitoringXS.Core.Tests`.

### ۲.۲. پروژه `MonitoringXS.Collectors`
* **مسئولیت:** دریافت داده‌های خام از منابع سیستم‌عامل (از طریق اینترفیس‌ها) و محاسبه نرخ‌ها (Deltas)، تفکیک مقادیر بازه زمانی و تجمیع داده‌ها بر اساس هر برنامه منطقی.
* **ورودی و خروجی:** خواندن نمونه‌های منابع و تبدیل آن‌ها به `ProcessMetricSet`، `PhysicalDiskMetricSet`، `NetworkMetricSet` و `GpuMetricSet`.
* **وابستگی به بیرون:** فقط `MonitoringXS.Core`. هیچ وابستگی مستقیمی به `MonitoringXS.Platform.Windows` ندارد.
* **آزمون‌پذیری:** بسیار بالا؛ با استفاده از پیاده‌سازی‌های ساختگی (Fakes/Mocks) در ۸۶ آزمون واحد مستقل در `MonitoringXS.Collectors.Tests` سنجیده می‌شود.

### ۲.۳. پروژه `MonitoringXS.Platform.Windows`
* **مسئولیت:** برقراری ارتباط مستقیم با کرنل و Win32 API ویندوز (P/Invoke، Toolhelp32، ETW TraceEvent، PDH، WMI و SCM).
* **کلاس‌های کلیدی:**
  * `WindowsProcessDiscoveryService`: کشف پردازه‌ها و هندل‌های پنجره‌های باز.
  * `ApplicationAttributionService`: انتساب پردازه‌ها به نرم‌افزارهای منطقی و تشخیص هویت MSIX/Win32.
  * `EtwPhysicalDiskEventSource`: مدیریت رویدادهای هسته برای I/O دیسک فیزیکی و شبکه.
  * `WindowsGpuPerformanceCounterSource`: دریافت کارکرد کارت گرافیک از طریق شمارنده‌های پرفورمنس PDH.
  * `WindowsProcessActionService`: اجرای امن عملیات توقف پردازه (End Task) و باز کردن مسیر فایل.
* **وابستگی به بیرون:** `MonitoringXS.Core` و پکیج‌های بومی مایکروسافت. به UI یا لایه Application وابسته نیست.

### ۲.۴. پروژه `MonitoringXS.Storage`
* **مسئولیت:** پایداری تاریخچه متریک‌ها در SQLite (`SqliteMetricHistoryStore`) و ذخیره تنظیمات کاربر در فایل JSON (`JsonApplicationSettingsStore`).
* **ورودی و خروجی:** دریافت `MetricHistoryCapture` و خروجی سری‌های زمانی با باکت‌های ۵ دقیقه‌ای و فیلترهای زمانی تا ۲۴ ساعت.
* **وابستگی به بیرون:** فقط `MonitoringXS.Core`. مستقل از رابط کاربری و موتور مانیتورینگ کار می‌کند.
* **آزمون‌پذیری:** دارای ۴۰ آزمون در `MonitoringXS.Storage.Tests` با فایل‌های دیتابیس درون‌حافظه‌ای یا موقت.

### ۲.۵. پروژه `MonitoringXS.Application`
* **مسئولیت:** هماهنگ‌سازی پایپ‌لاین دریافت متریک‌ها (`MonitoringCoordinator`)، پایپ‌لاین مراحل استخراج (`MonitoringCapturePipeline`)، توزیع اسنپ‌شات‌ها به مصرف‌کنندگان (`MonitoringSnapshotHub`) و مدیریت حلقه نمونه‌برداری در پس‌زمینه (`MonitoringRuntime`).
* **وابستگی به بیرون:** `MonitoringXS.Core`. از طریق تزریق وابستگی از پیاده‌سازی‌های کالکتور و ذخیره‌سازی استفاده می‌کند.
* **آزمون‌پذیری:** ۵۰ آزمون همزمانی و اعتبارسنجی پایپ‌لاین در `MonitoringXS.Application.Tests`.

### ۲.۶. پروژه `MonitoringXS.DesignSystem`
* **مسئولیت:** استایل‌ها، براش‌ها و توکن‌های بصری Precision Glass برای پوسته کاربری.
* **وابستگی به بیرون:** فاقد وابستگی به سایر پروژه‌ها؛ صرفاً توکن‌ها و تمپلیت‌های رابط کاربری.

### ۲.۷. پروژه `MonitoringXS.App`
* **مسئولیت:** نقطه ورود اصلی برنامه (WinUI 3 / Windows App SDK)، مدیریت پنجره و ناوبری، View-Modelها (`MainWindowViewModel`، `HistoryPageViewModel`، `DiagnosticsPageViewModel`، `SettingsPageViewModel`) و کلاس ترکیب نهایی سرویس‌ها (`ServiceRegistration`).
* **وابستگی به بیرون:** تمام پروژه‌های راهکار به عنوان Composition Root.

### ۲.۸. پروژه `MonitoringXS.PrivilegedBroker`
* **مسئولیت:** سرویس ویندوزی مستقل با دسترسی LocalSystem جهت اتصال به TraceEventSession هسته برای سناریوهای پایش سطح بالای دیسک و شبکه، با برقراری ارتباط از طریق Named Pipe ایزوله بر مبنای هش نشست کاربر.
* **وابستگی به بیرون:** `MonitoringXS.Core` و `MonitoringXS.Platform.Windows`.

---

## ۳. نقاط قوت معماری موجود

1. **مدل‌های داده تغییرناپذیر (Immutable Records):** اسنپ‌شات‌های تولیدشده (`MonitoringSnapshot`) پس از ساخت ویرایش نمی‌شوند، در نتیجه ریسک Race Condition بین ترد تولیدکننده و تردهای UI یا دیتابیس به حداقل رسیده است.
2. **پایپ‌لاین ماژولار متریک‌ها (`IMetricCaptureStage`):** کالکتورهای دیسک، شبکه و گرافیک هر کدام به عنوان یک مرحله مستقل در پایپ‌لاین عمل می‌کنند و تایم‌اوت محافظ ۷۵۰ میلی‌ثانیه‌ای دارند؛ خرابی یک مرحله موجب توقف سایر مراحل نمی‌شود.
3. **مرز محکم انتزاع پلتفرم:** لایه‌های Core، Collectors، Storage و Application کاملاً از وابستگی‌های P/Invoke جدا هستند و تست‌های آن‌ها روی هر محیطی بدون شبیه‌ساز سیستم‌عامل اجرا می‌شوند.
4. **محدودسازی صلب کش‌ها:** تمام بافرها، صف‌ها و کش‌های پروژه (کش متادیتا، کش آیکون‌ها، بافر PDH، صف درج SQLite) دارای سقف ظرفیت مشخص و منطق Eviction مستند هستند.

---

## ۴. نقاط ضعف و بدهی‌های فنی شناسایی‌شده

1. **انباشت مسئولیت در کلاس `SqliteMetricHistoryStore`:**
   * این کلاس هم بازسازی نشست‌های منطقی برنامه را انجام می‌دهد، هم بافر صف نوشتن غیرهمگام را نگه‌می‌دارد، هم کوئری‌های فیلترشده را اجرا می‌کند و هم عملیات نگه‌داری (Downsampling و Pruning) را هدایت می‌کند. شکستن آن به چند کلاس متمرکز خوانایی و پایداری را ارتقا می‌دهد.
2. **ادغام دیسک و شبکه در یک Event Source (`EtwPhysicalDiskEventSource`):**
   * به دلیل محدودیت کرنل ویندوز در تعداد نشست‌های فعال لاگر هسته، هر دو سورس در یک کلاس ادغام شده‌اند. با اینکه خطاهای شبکه و دیسک ایزوله هستند، اما تست و نگه‌داری این کلاس پیچیدگی بالایی دارد.
3. **وابستگی صلب `HistoryPageViewModel` به عدم نال بودن استور:**
   * در سازنده `HistoryPageViewModel` پارامتر `IMetricHistoryStore` باید غیرنال باشد، در حالی که در صورت عدم دسترسی به دیتابیس بهتر است استور پوچ (Null History Store) به شکل پیش‌فرض تزریق شود تا صفحه تاریخچه با وضعیت غیرفعال اما بدون کرش باز شود.

---

## ۵. نتیجه‌گیری ممیزی

معماری پروژه نمره کیفی **بسیار مطلوب (۸۸ از ۱۰۰)** را در حوزه تفکیک ماژولار و آزمون‌پذیری کسب می‌کند. تمام قوانین هدایت وابستگی‌ها در تست‌های ArchUnitNET رعایت شده‌اند و تفکیک لایه‌ای مانع از نشت پیچیدگی سیستم‌عامل به لایه‌های بالاتر شده است.
