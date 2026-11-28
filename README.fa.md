<div dir="rtl">

# Monitoring XS

[English](README.md) | **فارسی**

</div>

[![CI](https://github.com/rezamxs/MonitoringXS/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rezamxs/MonitoringXS/actions/workflows/ci.yml?query=branch%3Amain)
[![CodeQL](https://github.com/rezamxs/MonitoringXS/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/rezamxs/MonitoringXS/actions/workflows/codeql.yml)
[![Release](https://img.shields.io/github/v/release/rezamxs/MonitoringXS?include_prereleases&sort=semver)](https://github.com/rezamxs/MonitoringXS/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

<div dir="rtl">

> ببینید کدام **برنامه‌ها**، و نه ده‌ها پروسه‌ی کمکی‌شان، از CPU، حافظه، دیسک، شبکه و GPU ویندوز شما استفاده می‌کنند.

Monitoring XS یک مانیتور بومی دسکتاپ برای ویندوز است که به‌جای فهرست خام پروسه‌ها، بر پایه‌ی **برنامه‌های منطقی** کار می‌کند. پروسه‌های مرتبط (مثلاً پروسه‌های کمکی متعدد Chrome یا Visual Studio Code) کنار هم گروه‌بندی می‌شوند تا مصرف مجموع CPU، حافظه، دیسک، شبکه و GPU را برای همان برنامه‌ای ببینید که خودتان اجرا کرده‌اید.

پروژه در حال توسعه‌ی فعال است. پایش اصلی، تاریخچه (History)، Process Intelligence، صفحه‌ی Diagnostics، بسته‌ی نصبی و رابط کاربری انگلیسی/فارسی پیاده‌سازی شده‌اند. کارهای باقی‌مانده شامل صفحه‌هایی است که هنوز جای‌نگهدار هستند، اعتبارسنجی گسترده‌تر GPU روی سخت‌افزارهای مختلف، و امضای دیجیتال نسخه‌ها.

## قابلیت‌ها

آنچه در کد فعلی کار می‌کند:

- کشف برنامه‌های Win32 و MSIX، با جدا نگه داشتن برنامه‌های پرتابل/ثبت‌نشده
- گروه‌بندی برنامه‌های منطقی همراه با شواهد، میزان اطمینان، و نماهای Beginner/Advanced
- CPU، working set و I/O پروسه از شمارنده‌های پروسه‌ی ویندوز
- نسبت‌دادن مصرف دیسک فیزیکی و شبکه از طریق ETW کرنل (به Privileged Broker نیاز دارد)
- میزان استفاده از موتورهای GPU و حافظه‌ی اختصاصی/مشترک GPU به تفکیک پروسه، از طریق شمارنده‌های کارایی ویندوز
- یک runtime واحد برای جمع‌آوری داده (`MonitoringRuntime` / snapshot hub): بخش‌های رابط کاربری collector جداگانه راه نمی‌اندازند
- تاریخچه‌ی SQLite با نگهداری ۲۴ ساعته، نشست‌های برنامه/پروسه، و شناسایی با PID + زمان شروع
- صفحه‌ی History: بازه‌های ۵ دقیقه تا ۲۴ ساعت، نمودارهایی که فاصله‌ی داده‌ها را نشان می‌دهند، tooltip هنگام hover، و حالت‌های بارگذاری/خالی/خطا
- Process Intelligence: جزئیات، جستجو، ناشر، نسخه‌ی فایل، معماری، پروسه‌ی والد، threadها، handleها، کپی جزئیات
- اقدام روی پروسه با اعتبارسنجی دوباره‌ی PID + زمان شروع: End Task، End Process Tree، Open File Location
- مرکز Diagnostics روی همان snapshot/broker/history موجود (بدون حلقه‌ی پایش دوم)
- وضعیت در دسترس بودن هر معیار صریح است: **داده‌ی ناموجود هرگز صفر نشان داده نمی‌شود**
- بومی‌سازی انگلیسی و فارسی (راست‌به‌چپ)
- نصب‌کننده‌ی MSI با پشتیبانی از نصب، تعمیر، ارتقا و حذف ([جزئیات](docs/INSTALLER.md))

هنوز تمام نشده:

- صفحه‌های Dashboard و Portable Apps هنوز پیاده‌سازی نشده‌اند و در منوی ناوبری نمایش داده نمی‌شوند
- نمایش برنامه‌های متوقف‌شده و اقدام‌های بیشتر (بستن عادی، راه‌اندازی دوباره، حذف برنامه)
- اعتبارسنجی گسترده‌تر روی درایورها/سخت‌افزارهای GPU، نشست‌های remote و آداپتورهای مجازی
- اعتبارسنجی High Contrast، مقیاس ۱۵۰ تا ۲۰۰ درصد، Snap Layout و صفحه‌خوان‌ها
- امضای دیجیتال نسخه‌ها و آماده‌سازی انتشار عمومی

## نحوه‌ی کار

ویندوز معمولاً برای هر برنامه چند پروسه می‌سازد. Monitoring XS هر پروسه را با استفاده از متادیتای فایل اجرایی، هویت بسته (package identity) و رابطه‌ی والد/فرزند دسته‌بندی می‌کند و سپس معیارها را در سطح برنامه جمع می‌زند.

جمع‌آوری زنده از یک pipeline واحد انجام می‌شود و snapshotهای تغییرناپذیر منتشر می‌کند. تاریخچه به‌صورت ناهمگام در یک پایگاه داده‌ی محلی SQLite نوشته می‌شود. رابط کاربری History از `IMetricHistoryStore` پرس‌وجو می‌کند و خودش مستقیماً SQLite را باز نمی‌کند.

پایش دیسک فیزیکی و شبکه از یک سرویس محدودشده با حساب LocalSystem به نام `PrivilegedEtwBroker` استفاده می‌کند، چون ETW کرنل به دسترسی بالا نیاز دارد. خود برنامه بدون دسترسی مدیر (`asInvoker`) اجرا می‌شود. اگر broker نصب نباشد یا متوقف شده باشد، این معیارها «ناموجود» نشان داده می‌شوند و CPU، حافظه، I/O پروسه و GPU همچنان کار می‌کنند. ببینید: [امنیت](docs/SECURITY.md) و [ADR 0004](docs/adr/0004-privileged-etw-broker-service.md).

مقادیر GPU از شمارنده‌های `GPU Engine` و `GPU Process Memory` ویندوز روی درایورهای سازگار با WDDM خوانده می‌شوند. اگر شمارنده‌ای وجود نداشته باشد، پروسه محافظت‌شده باشد یا رندر نرم‌افزاری باشد، مقدار «ناموجود» یا «ناقص» می‌ماند. این ادعای پوشش کامل همه‌ی GPUها نیست.

## دانلود و نصب

نسخه‌ی فعلی **v1.0.1** است و به‌صورت **پیش‌انتشار (pre-release)** بتا منتشر شده: [MonitoringXS-1.0.1-x64.msi](https://github.com/rezamxs/MonitoringXS/releases/tag/v1.0.1) (همه‌ی نسخه‌ها: [Releases](https://github.com/rezamxs/MonitoringXS/releases)).

۱. فایل `MonitoringXS-1.0.1-x64.msi` را دانلود و اجرا کنید. نصب برای همه‌ی کاربران سیستم انجام می‌شود، پس یک بار تأیید مدیر (UAC) خواسته می‌شود.

۲. فایل MSI **هنوز امضای دیجیتال ندارد**، بنابراین احتمالاً Windows SmartScreen پیام *«Windows protected your PC»* را نشان می‌دهد. روی **More info** و سپس **Run anyway** کلیک کنید. این کار را فقط برای فایلی انجام دهید که از صفحه‌ی Releases همین مخزن دانلود کرده‌اید.

۳. **Monitoring XS** را از منوی Start اجرا کنید (میان‌بر دسکتاپ هنگام نصب اختیاری است).

نصب‌کننده مستقل است و به نصب جداگانه‌ی .NET یا Windows App SDK نیازی ندارد. برنامه و سرویس Privileged Broker در مسیر `%ProgramFiles%\Monitoring XS` نصب می‌شوند، پس معیارهای دیسک و شبکه بعد از دادن اجازه در دسترس قرار می‌گیرند (بخش [دسترسی آمار ویژه](#دسترسی-آمار-ویژه) را ببینید). خود برنامه همیشه بدون دسترسی مدیر اجرا می‌شود.

برای حذف از **Settings → Apps** استفاده کنید. تنظیمات و پایگاه داده‌ی تاریخچه در `%LOCALAPPDATA%\MonitoringXS` عمداً باقی می‌مانند؛ برای پاک‌سازی کامل، آن پوشه را خودتان حذف کنید. جزئیات: [مستندات نصب‌کننده](docs/INSTALLER.md).

## دسترسی آمار ویژه

آمار **دیسک فیزیکی** و **شبکه** برای هر برنامه از شمارنده‌هایی در ویندوز خوانده می‌شود که فقط یک سرویس با دسترسی مدیر (Administrator) می‌تواند آن‌ها را بخواند. برای همین، MonitoringXS در کنار برنامه یک سرویس کوچک به نام **Privileged Metrics Service** نصب می‌کند؛ خود برنامه همیشه با دسترسی کاربر عادی اجرا می‌شود.

این نسخه هنوز امضای دیجیتال (code signing) ندارد، پس ویندوز نمی‌تواند ناشر آن را تأیید کند و سرویس هم به‌طور خودکار به برنامه اعتماد نمی‌کند. به همین خاطر، اولین باری که MonitoringXS را باز می‌کنید، از شما اجازه می‌گیرد:

- با زدن **اجازه دادن**، ویندوز پنجره‌ی تأیید مدیر (UAC) را نشان می‌دهد و فقط یک تنظیم که تنها مدیر سیستم می‌تواند تغییرش دهد ذخیره می‌شود: `HKLM\SOFTWARE\MonitoringXS\PrivilegedMetrics` با مقدار `AllowUnsignedClient = 1`.
- با زدن **اکنون نه**، این مرحله رد می‌شود. CPU، حافظه، I/O پروسه‌ها، GPU و تاریخچه مثل قبل کار می‌کنند و فقط دیسک و شبکه «ناموجود» نشان داده می‌شوند.

این سرویس فقط شمارنده‌های دیسک و شبکه‌ی همین کامپیوتر را می‌خواند و هیچ داده‌ای به جایی ارسال نمی‌شود. هر وقت خواستید می‌توانید این اجازه را از **تنظیمات ← پایش پیشرفته** پس بگیرید، و با حذف MonitoringXS هم سرویس و هم این تنظیم پاک می‌شوند.

## پیش‌نیازها

برای اجرا:

- ویندوز ۱۰ نسخه‌ی 1809 (build 17763) یا جدیدتر، یا ویندوز ۱۱
- x64 (ARM64 در معماری پروژه هدف‌گذاری شده اما هنوز آزمایش یا بسته‌بندی نشده)

برای ساخت از سورس، علاوه بر موارد بالا:

- .NET 10 SDK (نسخه‌ی `10.0.100` یا feature band جدیدتر؛ ببینید [`global.json`](global.json))
- Visual Studio 2026 با workload **WinUI application development** و یک Windows SDK به‌روز
- حالت Developer Mode ویندوز، اگر Visual Studio هنگام استقرار محلی آن را بخواهد

## ساخت از سورس

</div>

```powershell
git clone https://github.com/rezamxs/MonitoringXS.git
cd MonitoringXS
dotnet restore MonitoringXS.sln
# The integration tests build this helper outside the solution, so restore it explicitly (CI does the same):
dotnet restore tests/MonitoringXS.ProcessActionTestHelper/MonitoringXS.ProcessActionTestHelper.csproj
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

<div dir="rtl">

از Visual Studio (پیکربندی x64) اجرا کنید یا:

</div>

```powershell
dotnet run --project src/MonitoringXS.App/MonitoringXS.App.csproj -c Debug -p:Platform=x64
```

<div dir="rtl">

تعداد فعلی نتایج restore/build/test بعد از هر مرحله در [docs/VALIDATION.md](docs/VALIDATION.md) ثبت می‌شود. عددهای قدیمی‌تر آن گزارش را اندازه‌ی فعلی مجموعه‌ی تست‌ها در نظر نگیرید.

اگر برنامه را از سورس اجرا می‌کنید (نه از MSI)، برای معیارهای دیسک و شبکه Privileged Broker را از یک PowerShell با دسترسی مدیر نصب کنید:

</div>

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Install
```

<div dir="rtl">

بررسی وضعیت (بدون دسترسی مدیر):

</div>

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Status
```

<div dir="rtl">

برای حذف آن از `-Mode Remove` استفاده کنید. برای ساخت محلی MSI، اسکریپت `scripts\installer\Build-Installer.ps1` را اجرا کنید (خروجی در `.artifacts\validation\installer-packaging\package` قرار می‌گیرد).

ببینید: [راه‌اندازی محیط توسعه](docs/DEVELOPMENT.md) و [مستندات نصب‌کننده](docs/INSTALLER.md).

## ساختار پروژه

- `MonitoringXS.Core`: مدل‌های تغییرناپذیر و interfaceها
- `MonitoringXS.Application`: runtime، snapshot hub و use caseها
- `MonitoringXS.Platform.Windows`: کشف برنامه‌ها، متادیتا، ETW/PDH، اقدام روی پروسه‌ها
- `MonitoringXS.Collectors`: نمونه‌برداری و تجمیع معیارها
- `MonitoringXS.Storage`: تاریخچه‌ی SQLite و تنظیمات JSON
- `MonitoringXS.DesignSystem`: توکن‌های بصری Precision Glass
- `MonitoringXS.App`: viewها و view modelهای WinUI
- `MonitoringXS.ElevatedHelper`: عملیات‌های نیازمند دسترسی بالا، فقط هنگام نیاز
- `MonitoringXS.PrivilegedBroker`: سرویس اختیاری ETW broker

جزئیات معماری: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## وضعیت فعلی

بتا. شاخه‌ی `main` خط اصلی محصول است؛ اولین نصب‌کننده، [v1.0.0](https://github.com/rezamxs/MonitoringXS/releases/tag/v1.0.0)، به‌صورت پیش‌انتشار بدون امضا منتشر شده است. تگ‌هایی که با `v*` شروع می‌شوند، MSI را می‌سازند و به‌طور خودکار یک پیش‌انتشار منتشر می‌کنند ([workflow انتشار](.github/workflows/release.yml)).

برخی داده‌ها برای پروسه‌های محافظت‌شده یا با سطح دسترسی بالاتر در دسترس نیستند. برنامه این را صادقانه گزارش می‌کند و به‌جای آن دسترسی مدیر دائمی نمی‌خواهد.

## مستندات

مستندات تفصیلی به زبان انگلیسی است:

- [مشخصات محصول](docs/PRODUCT_SPEC.md)
- [معماری](docs/ARCHITECTURE.md)
- [معنای معیارها](docs/METRICS.md)
- [نسبت‌دادن پروسه‌ها به برنامه‌ها](docs/APPLICATION_ATTRIBUTION.md)
- [امنیت](docs/SECURITY.md) و [حریم خصوصی](docs/PRIVACY.md)
- [کارایی](docs/PERFORMANCE.md)
- [سیستم طراحی](docs/DESIGN_SYSTEM.md)
- [نقاط عطف](docs/MILESTONES.md)
- [گزارش اعتبارسنجی](docs/VALIDATION.md)
- [عیب‌یابی](docs/TROUBLESHOOTING.md)
- [سبک مهندسی](docs/ENGINEERING_STYLE.md)
- [نصب‌کننده‌ی ویندوز](docs/INSTALLER.md)
- [تغییرات نسخه‌ها](CHANGELOG.md)

## بازخورد و گزارش باگ

- **باگ‌ها**: [Bug Report](https://github.com/rezamxs/MonitoringXS/issues/new?template=bug_report.yml)
- **ایده‌های جدید**: [Feature Request](https://github.com/rezamxs/MonitoringXS/issues/new?template=feature_request.yml)
- **آسیب‌پذیری‌های امنیتی**: [SECURITY.md](SECURITY.md). مسائل امنیتی را در issueهای عمومی مطرح نکنید.

## مشارکت

issueها و pull requestهای متمرکز خوشایندند. لطفاً پیش از ارسال، [CONTRIBUTING.md](CONTRIBUTING.md)، [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) و [SECURITY.md](SECURITY.md) را بخوانید.

## مجوز

منتشرشده تحت [مجوز MIT](LICENSE). ساخته و نگهداری‌شده توسط [rezamxs](https://github.com/rezamxs).

</div>
