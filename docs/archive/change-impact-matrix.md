# ماتریس اثرگذاری تغییرات و حذف ماژول‌ها (Change Impact Matrix)

این جدول مشخص می‌کند که تغییر یا حذف هر جزء اصلی از سیستم، چه ماژول‌ها و قابلیت‌هایی را تحت تأثیر قرار می‌دهد، چه میزان ریسک دارد و کدام مجموعه آزمون‌ها وظیفه محافظت از آن را بر عهده دارند.

---

## جدول جامع اثرگذاری ماژول‌ها

| ماژول هدف | لایه معماری | مصرف‌کنندگان مستقیم | قابلیت‌های متأثر از تغییر | امکان حذف / جایگزینی | شدت ریسک | آزمون‌های محافظ (Test Suite) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **`IProcessDiscoveryService`** | Core / Platform | `MonitoringCoordinator` | لیست کلی پردازه‌ها، پنجره‌های فعال، PIDهای زنده | **بالا** (با هر دیسکاوری دیگر) | **بحرانی** | `WindowsProcessDiscoveryServiceTests` |
| **`IApplicationAttributionService`** | Core / Platform | `MonitoringCoordinator` | گروه‌بندی برنامه‌ها، نام نمایشی، آیکون، وضعیت Portable/Installed | **بالا** (با کاتالوگ دیگر) | **بحرانی** | `ApplicationAttributionTests` |
| **`IProcessMetricCollector`** | Core / Collectors | `MonitoringCoordinator` | درصد مصرف CPU، مقدار Working Set، خواندن و نوشتن I/O پردازه | **متوسط** | **زیاد** | `ProcessMetricCollectorTests` |
| **`IPhysicalDiskMetricCollector`** | Core / Collectors | `PhysicalDiskMetricStage` | ستون‌های خواندن/نوشتن دیسک، وضعیت صف I/O | **بسیار بالا** (تایم‌اوت محافظتی دارد) | **متوسط** | `PhysicalDiskMetricCollectorTests` |
| **`INetworkMetricCollector`** | Core / Collectors | `NetworkMetricStage` | حجم ارسال و دریافت شبکه، شمارش سوکت‌های TCP/UDP | **بسیار بالا** (تنزل امن به وضعیت Unavailable) | **متوسط** | `NetworkMetricCollectorTests` |
| **`IGpuMetricCollector`** | Core / Collectors | `GpuMetricStage` | درصد فشار گرافیک، حافظه ویدئویی اختصاصی و اشتراکی | **بسیار بالا** (حذف آن پایپ‌لاین را متوقف نمی‌کند) | **متوسط** | `GpuMetricCollectorTests` |
| **`IMetricHistoryStore`** | Core / Storage | `MonitoringRuntime`، `HistoryPageViewModel` | تب تاریخچه، نمودارهای گذشته‌نگر ۲۴ ساعته | **بالا** (با In-Memory Store یا بدون استور) | **متوسط** | `SqliteMetricHistoryStoreTests` |
| **`IApplicationSettingsStore`** | Core / Storage | `App.OnLaunched`، `SettingsPageViewModel` | نگه‌داری بازه رفرش، زبان، تم تاریک/روشن، فیلترها | **بالا** (پایین آمدن روی مقادیر پیش‌فرض) | **کم** | `JsonApplicationSettingsStoreTests` |
| **`IProcessActionService`** | Core / Platform | `MainWindowViewModel` | پایان پردازه (End Task)، بستن درخت پردازه، باز کردن پوشه | **بالا** (قرارداد بسته و مستقل) | **زیاد** | `WindowsProcessActionServiceTests` |
| **`PrivilegedEtwBrokerClient`** | Platform | `PhysicalDiskEventSource`، `NetworkEventSource` | دریافت داده‌های دقیق سطح کرنل برای کاربران عادی | **بسیار بالا** (به عنوان سرویس اختیاری عمل می‌کند) | **متوسط** | `PrivilegedEtwBrokerTests` |
| **`LocalizationService`** | App | `MainWindow`، تمام کنترل‌های XAML | ترجمه واژگان انگلیسی/فارسی، جهت چینش RTL/LTR | **متوسط** | **کم** | `LocalizationServiceTests` |

---

## تحلیل سناریوهای حذف کنترل‌شده

### ۱. حذف یا خاموش بودن سرویس `PrivilegedBroker`
* **اثر مستقیم:** ارتباط پایپ برقرار نمی‌شود.
* **واکنش سیستم:** کلاس کلاینت قبل از تلاش برای اتصال، عدم وجود یا خاموش بودن سرویس را شناسایی می‌کند.
* **نتیجه در UI:** ستون‌های دیسک و شبکه وضعیت `Unavailable` نمایش می‌دهند.
* **پایداری:** سایر متریک‌ها (CPU، رم، پروسس I/O و GPU) با دقت کامل به کار خود ادامه می‌دهند. برنامه دچار کرش یا ایستایی نمی‌شود.

### ۲. حذف یا نقص فایل تنظیمات `settings.json`
* **اثر مستقیم:** لود شدن استور با شکست مواجه می‌شود.
* **واکنش سیستم:** در متد `App.OnLaunched` خطای بارگذاری در بلوک `catch` مدیریت شده و شیء `ApplicationSettings.Default` جایگزین می‌شود.
* **نتیجه در UI:** برنامه با بازه ۱ ثانیه، زبان سیستم و تم پیش‌فرض اجرا می‌شود و فایل معیوب قرنطینه می‌گردد.

### ۳. نبود یا خطای دسترسی به پایگاه داده `history.db`
* **اثر مستقیم:** درج نمونه‌های اسنپ‌شات در صف دیتابیس با شکست مواجه می‌شود.
* **واکنش سیستم:** صف درج دیتابیس در `MonitoringRuntime` به صورت آتش-و-فراموش (Best-effort fire-and-forget) مدیریت شده و خطای پایگاه داده به ترد پایش زنده سرایت نمی‌کند.
* **نتیجه در UI:** تب داشبورد زنده ۱۰۰٪ فعال است؛ تب تاریخچه با خطای خواندن مواجه شده یا وضعیت بدون داده نمایش می‌دهد.
