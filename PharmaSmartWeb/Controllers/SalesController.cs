using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PharmaSmartWeb.Models;
using PharmaSmartWeb.Filters;
using PharmaSmartWeb.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace PharmaSmartWeb.Controllers
{
    [Authorize]
    public class SalesController : BaseController
    {
        private readonly IAccountingEngine _accountingEngine;
        private readonly IWhatsAppService _whatsappService;
        private readonly NotificationEngine _notificationEngine;
        private readonly ILogger<SalesController> _logger;

        public SalesController(ApplicationDbContext context, IAccountingEngine accountingEngine, IWhatsAppService whatsappService, NotificationEngine notificationEngine, ILogger<SalesController> logger) : base(context)
        {
            _accountingEngine = accountingEngine;
            _whatsappService = whatsappService;
            _notificationEngine = notificationEngine;
            _logger = logger;
        }

        private async Task<int> GetValidUserIdAsync()
        {
            var userIdClaim = User.FindFirst("UserID")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int parsedId))
            {
                if (await _context.Users.AnyAsync(u => u.UserId == parsedId)) return parsedId;
            }
            throw new Exception("انتهت صلاحية الجلسة أو تعذر التحقق من هوية المستخدم. يرجى تسجيل الدخول مجدداً.");
        }

        [HttpGet]
        [HasPermission("Sales", "View")]
        public async Task<IActionResult> Index(int page = 1)
        {
            int pageSize = 50;
            var today = DateTime.Today;

            // 🚀 هندسة الأداء (Performance): استعلام الإحصائيات باستخدام SQL Projection لمنع سحب الكائنات
            var statsQuery = await _context.Sales
                .AsNoTracking()
                .Where(s => s.BranchId == ActiveBranchId && s.SaleDate >= today && s.IsReturn == false)
                .GroupBy(s => 1)
                .Select(g => new {
                    TotalAmount = g.Sum(x => x.NetAmount),
                    Count = g.Count()
                }).FirstOrDefaultAsync();

            ViewBag.TotalSalesToday = statsQuery?.TotalAmount ?? 0;
            ViewBag.TotalInvoicesToday = statsQuery?.Count ?? 0;
            ViewBag.AverageCartValueToday = (statsQuery?.Count > 0) ? (statsQuery.TotalAmount / statsQuery.Count) : 0;

            // 🚀 نظام التقسيم الذكي (Server-Side Pagination)
            var baseQuery = _context.Sales
                .AsNoTracking()
                .Include(s => s.Customer)
                .Include(s => s.User)
                .Include(s => s.SalePayments)
                .Where(s => s.BranchId == ActiveBranchId && s.IsReturn == false);

            int totalRecords = await baseQuery.CountAsync();
            
            var salesList = await baseQuery
                .OrderByDescending(s => s.SaleDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize);

            return View(salesList);
        }

        [HttpGet]
        [HasPermission("Sales", "View")]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var sale = await _context.Sales
                .Include(s => s.Customer)
                .Include(s => s.User)
                .Include(s => s.Saledetails)
                    .ThenInclude(d => d.Drug)
                .Include(s => s.SalePayments)
                    .ThenInclude(sp => sp.Account)
                .FirstOrDefaultAsync(m => m.SaleId == id && m.BranchId == ActiveBranchId);

            if (sale == null) return RedirectToAction("AccessDenied", "Home");

            ViewBag.SystemSettings = await _context.CompanySettings.FirstOrDefaultAsync();

            return View(sale);
        }

        [HttpGet]
        [HasPermission("Sales", "View")]
        public async Task<IActionResult> PrintPartial(int id)
        {
            var sale = await _context.Sales
                .Include(s => s.Customer)
                .Include(s => s.User)
                .Include(s => s.Saledetails).ThenInclude(d => d.Drug)
                .Include(s => s.SalePayments).ThenInclude(sp => sp.Account)
                .FirstOrDefaultAsync(m => m.SaleId == id && m.BranchId == ActiveBranchId);

            if (sale == null) return NotFound();

            ViewBag.SystemSettings = await _context.CompanySettings.FirstOrDefaultAsync();
            return PartialView("_InvoicePartial", sale);
        }

        [HttpGet]
        [HasPermission("Sales", "Add")]
        public async Task<IActionResult> GetCustomersList()
        {
            var customers = await _context.Customers
                .Where(c => c.IsActive == true && (c.BranchId == ActiveBranchId || c.BranchId == 1))
                .Select(c => new { id = c.CustomerId, name = c.FullName })
                .ToListAsync();

            return Json(customers);
        }

        [HttpGet]
        [HasPermission("Sales", "Add")]
        public async Task<IActionResult> Create()
        {
            var userId = await GetValidUserIdAsync();
             var openShift = await _context.Shifts.FirstOrDefaultAsync(s => s.UserId == userId && s.BranchId == ActiveBranchId && s.Status == "Open");

            if (openShift == null)
            {
                TempData["Warning"] = "لا يمكنك الدخول إلى شاشة البيع بدون وردية مفتوحة. الرجاء فتح وردية أولاً.";
                return RedirectToAction("OpenShift", "Shifts");
            }

            ViewBag.Customers = new SelectList(_context.Customers.Where(c => c.IsActive == true && (c.BranchId == ActiveBranchId || c.BranchId == 1)), "CustomerId", "FullName");
            ViewBag.CashAccounts = _context.Accounts.Where(a => a.IsActive == true && a.IsParent == false && (a.AccountName.Contains("صندوق") || a.AccountName.Contains("نقد")) && a.BranchId == ActiveBranchId).ToList();
            ViewBag.BankAccounts = _context.Accounts.Where(a => a.IsActive == true && a.IsParent == false && (a.AccountName.Contains("بنك") || a.AccountName.Contains("حساب")) && (a.BranchId == ActiveBranchId || a.BranchId == null)).ToList();

            // 🚀 التحديث الهندسي: جلب أسماء الوحدات ومعامل التحويل ليتمكن الـ POS من قسمة السعر آلياً
            var inventoryItems = _context.Branchinventory
                .Include(b => b.Drug)
                .Where(b => b.BranchId == ActiveBranchId && b.StockQuantity > 0 && b.Drug.IsActive == true)
                .Select(b => new
                {
                    id = b.DrugId,
                    name = b.Drug.DrugName,
                    barcode = b.Drug.Barcode,
                    price = b.CurrentSellingPrice ?? 0,
                    cost = b.AverageCost ?? 0,
                    stock = b.StockQuantity,
                    mainUnit = string.IsNullOrEmpty(b.Drug.MainUnit) ? "باكت" : b.Drug.MainUnit,
                    subUnit = string.IsNullOrEmpty(b.Drug.SubUnit) ? "حبة" : b.Drug.SubUnit,
                    convFactor = b.Drug.ConversionFactor > 0 ? b.Drug.ConversionFactor : 1
                }).ToList();

            ViewBag.DrugsJson = System.Text.Json.JsonSerializer.Serialize(inventoryItems);

            return View(new Sales { SaleDate = DateTime.Now, ShiftId = openShift.ShiftId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HasPermission("Sales", "Add")]
        public async Task<IActionResult> Create(Sales sale, decimal CashAmount, int? CashAccountId, decimal BankAmount, int? BankAccountId)
        {
            ModelState.Remove("Customer"); ModelState.Remove("User"); ModelState.Remove("Branch"); ModelState.Remove("Shift");

            if (sale.Saledetails != null)
            {
                var details = sale.Saledetails.ToList();
                for (int i = 0; i < details.Count; i++) { ModelState.Remove($"Saledetails[{i}].Sale"); ModelState.Remove($"Saledetails[{i}].Drug"); }
            }

            bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";

            if (sale.Saledetails == null || !sale.Saledetails.Any())
            {
                if (isAjax) return Json(new { success = false, message = "الفاتورة فارغة!" });
                ViewBag.Error = "الفاتورة فارغة!";
                return ReloadCreateView(sale);
            }

            if (ModelState.IsValid)
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                try
                {
                    // ✅ التحقق من وجود وردية مفتوحة قبل الترحيل
                    var userIdForShift = await GetValidUserIdAsync();
                    var openShift = await _context.Shifts.FirstOrDefaultAsync(s => s.UserId == userIdForShift && s.BranchId == ActiveBranchId && s.Status == "Open");
                    if (openShift == null)
                    {
                        if (isAjax) return Json(new { success = false, message = "لا توجد وردية مفتوحة. الرجاء فتح وردية أولاً." });
                        ViewBag.Error = "لا توجد وردية مفتوحة. الرجاء فتح وردية أولاً.";
                        return ReloadCreateView(sale);
                    }

                    await strategy.ExecuteAsync(async () =>
                    {
                        using var transaction = await _context.Database.BeginTransactionAsync();
                        try
                        {
                            sale.UserId = userIdForShift;
                            sale.BranchId = ActiveBranchId;
                            sale.SaleDate = DateTime.Now;
                            sale.IsReturn = false;
                            sale.ShiftId = openShift.ShiftId;

                            decimal grossTotal = 0;
                            decimal totalCogs = 0;

                            var drugIds = sale.Saledetails.Select(x => x.DrugId).Distinct().ToList();
                            var inventoriesList = await _context.Branchinventory
                                .Include(b => b.Drug)
                                .Where(b => b.BranchId == ActiveBranchId && drugIds.Contains(b.DrugId))
                                .ToListAsync();
                            
                            var inventoriesDict = inventoriesList.ToDictionary(b => b.DrugId);

                            foreach (var item in sale.Saledetails)
                            {
                                grossTotal += Math.Round(item.Quantity * item.UnitPrice, 2);
                                if (!inventoriesDict.TryGetValue(item.DrugId, out var inventory))
                                    throw new Exception($"الصنف (DrugId: {item.DrugId}) غير موجود في مخزون الفرع الحالي ({ActiveBranchId}).");

                                if (inventory.StockQuantity < item.Quantity)
                                    throw new Exception($"الكمية ({item.Quantity}) المطلوبة للصنف '{inventory.Drug?.DrugName}' تتجاوز المخزون المتوفر ({inventory.StockQuantity}).");

                                // 🚀 بما أن السيرفر يستقبل (الكمية بالوحدة الصغرى) و(التكلفة للوحدة الصغرى)، فالحسابات هنا دقيقة 100%
                                decimal costPerPill = (inventory.AverageCost ?? 0) / (inventory.Drug.ConversionFactor > 0 ? inventory.Drug.ConversionFactor : 1);
                                totalCogs += (item.Quantity * costPerPill);

                                inventory.StockQuantity -= item.Quantity; // خصم الحبات المباعة من المخزون
                                _context.Branchinventory.Update(inventory);

                                _context.Stockmovements.Add(new Stockmovements { BranchId = ActiveBranchId, DrugId = item.DrugId, MovementDate = DateTime.Now, MovementType = "Sale Out", Quantity = -item.Quantity, UserId = sale.UserId, Notes = "مبيعات POS" });
                            }

                                sale.TotalAmount = Math.Round(grossTotal, 2);
                            sale.NetAmount = Math.Round(sale.TotalAmount - sale.Discount + sale.TaxAmount, 2);
                            _context.Sales.Add(sale);
                            await _context.SaveChangesAsync();

                                                     decimal amountPaid = Math.Round(CashAmount + BankAmount, 2);
                            decimal remainingAmount = Math.Round(sale.NetAmount - amountPaid, 2);

                            bool hasCustomer = sale.CustomerId is int cid && cid > 0;

                            if (remainingAmount > 0 && !hasCustomer)
                                throw new Exception("المبلغ المدفوع أقل من الصافي، يرجى اختيار العميل لتسجيل المديونية!");

                            if (CashAmount > 0)
                            {
                                if (!(CashAccountId is int cId && cId > 0)) throw new Exception("يرجى اختيار حساب الصندوق.");
                                _context.SalePayments.Add(new SalePayments { SaleId = sale.SaleId, PaymentMethod = "Cash", AccountId = CashAccountId, Amount = CashAmount });
                            }
                            if (BankAmount > 0)
                            {
                                if (!(BankAccountId is int bId && bId > 0)) throw new Exception("يرجى اختيار حساب البنك.");
                                _context.SalePayments.Add(new SalePayments { SaleId = sale.SaleId, PaymentMethod = "Bank", AccountId = BankAccountId, Amount = BankAmount });
                            }
                            if (remainingAmount > 0)
                            {
                                _context.SalePayments.Add(new SalePayments { SaleId = sale.SaleId, PaymentMethod = "Credit", Amount = remainingAmount });
                            }

                            await _context.SaveChangesAsync();

                            var payload = new AccountingPayload
                            {
                                TransactionType = TransactionType.SalesInvoice,
                                BranchId = ActiveBranchId,
                                UserId = sale.UserId,
                                ReferenceNo = sale.SaleId.ToString(),
                                Description = $"مبيعات POS فاتورة #{sale.SaleId}",
                                CustomerId = sale.CustomerId,
                                SpecificCashAccountId = CashAccountId > 0 ? CashAccountId : null,
                                SpecificBankAccountId = BankAccountId > 0 ? BankAccountId : null
                            };

                            payload.Amounts.Add(AmountSource.NetTotalAmount, sale.NetAmount);
                            payload.Amounts.Add(AmountSource.PaidCashAmount, CashAmount);
                            payload.Amounts.Add(AmountSource.PaidBankAmount, BankAmount);
                            payload.Amounts.Add(AmountSource.CreditAmount, remainingAmount);
                            payload.Amounts.Add(AmountSource.COGSAmount, totalCogs);

                            await _accountingEngine.ProcessTransactionAsync(payload);

                            await transaction.CommitAsync();
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            await transaction.RollbackAsync();
                           throw new Exception("حدث تعارض في تحديث المخزون (ربما تم بيع الصنف من جهاز آخر في نفس اللحظة). يرجى تحديث الصفحة والمحاولة مرة أخرى.");
                        }
                        catch (Exception) { await transaction.RollbackAsync(); throw; }
                    });

                    await RecordLog("Add", "Sales", $"إصدار فاتورة مبيعات POS رقم {sale.SaleId}");

                    // 🔔 تشغيل محرك الإشعارات فوراً للتحقق من النواقص بعد البيع
                    _ = Task.Run(async () => {
                        try {
                            using var scope = HttpContext.RequestServices.CreateScope();
                            var engine = scope.ServiceProvider.GetRequiredService<NotificationEngine>();
                            await engine.GenerateAndSaveNotificationsAsync(ActiveBranchId);
                        } catch { /* ignore notification errors to not break sale UI flow */ }
                    });
                    // 🚀 هندسة SPA: إذا كان الطلب AJAX (من الـ POS)، نرجع JSON بدلاً من Redirect
                    if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    {
                        return Json(new { success = true, saleId = sale.SaleId });
                    }

                    return RedirectToAction(nameof(Details), new { id = sale.SaleId, print = "true" });
                }
                catch (Exception ex) 
                { 
                    if (isAjax) return Json(new { success = false, message = ex.Message });
                    ViewBag.Error = ex.Message; 
                }
            }

            if (isAjax) 
            {
                string errors = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                return Json(new { success = false, message = string.IsNullOrEmpty(ViewBag.Error) ? errors : ViewBag.Error });
            }

            return ReloadCreateView(sale);
        }

        private IActionResult ReloadCreateView(Sales sale)
        {
            ViewBag.Customers = new SelectList(_context.Customers.Where(c => c.IsActive == true), "CustomerId", "FullName", sale.CustomerId);
            ViewBag.CashAccounts = _context.Accounts.Where(a => a.IsActive == true && a.IsParent == false && (a.AccountName.Contains("صندوق") || a.AccountName.Contains("نقد")) && a.BranchId == ActiveBranchId).ToList();
            ViewBag.BankAccounts = _context.Accounts.Where(a => a.IsActive == true && a.IsParent == false && (a.AccountName.Contains("بنك") || a.AccountName.Contains("حساب")) && (a.BranchId == ActiveBranchId || a.BranchId == null)).ToList();
            var inventoryItems = _context.Branchinventory.Include(b => b.Drug).Where(b => b.BranchId == ActiveBranchId && b.StockQuantity > 0 && b.Drug.IsActive == true).Select(b => new { id = b.DrugId, name = b.Drug.DrugName, barcode = b.Drug.Barcode, price = b.CurrentSellingPrice ?? 0, cost = b.AverageCost ?? 0, stock = b.StockQuantity, mainUnit = string.IsNullOrEmpty(b.Drug.MainUnit) ? "باكت" : b.Drug.MainUnit, subUnit = string.IsNullOrEmpty(b.Drug.SubUnit) ? "حبة" : b.Drug.SubUnit, convFactor = b.Drug.ConversionFactor > 0 ? b.Drug.ConversionFactor : 1 }).ToList();
            ViewBag.DrugsJson = System.Text.Json.JsonSerializer.Serialize(inventoryItems);
            return View(sale);
        }

        // تم نقل دوال الوردية إلى ShiftsController المستقل

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HasPermission("Sales", "Add")]
        public async Task<IActionResult> SyncOfflineSale([FromForm] string saleJson)
        {
            if (string.IsNullOrEmpty(saleJson))
                return BadRequest(new { success = false, message = "بيانات الفاتورة فارغة." });

            try
            {
                  var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };
                var offlineData = System.Text.Json.JsonSerializer.Deserialize<OfflineSaleDto>(saleJson, options);
                if (offlineData == null)
                    return BadRequest(new { success = false, message = "فاتورة غير صالحة." });

                var strategy = _context.Database.CreateExecutionStrategy();
                int newSaleId = 0;

                // ✅ قائمة لتتبع الأصناف التي تم تخطيها بسبب نقص المخزون
                var skippedItems = new List<object>();

                await strategy.ExecuteAsync(async () =>
                {
                    using var transaction = await _context.Database.BeginTransactionAsync();
                    try
                    {
                        var userId = await GetValidUserIdAsync();

                        var sale = new Sales
                        {
                            UserId = userId,
                            BranchId = ActiveBranchId,
                            SaleDate = DateTime.Now,
                            IsReturn = false,
                            CustomerId = offlineData.CustomerId > 0 ? offlineData.CustomerId : null,
                            Discount = offlineData.Discount,
                            TaxAmount = offlineData.TaxAmount,
                        };

                        decimal grossTotal = 0, totalCogs = 0;
                        sale.Saledetails = new List<Saledetails>();

                        var drugIds = offlineData.Items.Select(i => i.DrugId).Distinct().ToList();
                        var inventoriesList = await _context.Branchinventory
                            .Include(b => b.Drug)
                            .Where(b => b.BranchId == ActiveBranchId && drugIds.Contains(b.DrugId))
                            .ToListAsync();
                        var inventoriesDict = inventoriesList.ToDictionary(b => b.DrugId);

                        foreach (var item in offlineData.Items)
                        {
                            int itemQty = (int)Math.Round(item.Quantity);

                            inventoriesDict.TryGetValue(item.DrugId, out var inventory);

                    // ✅ الإصلاح الجوهري:
                    // الصنف لا يُضاف للفاتورة إلا إذا تحقق الشرطان معاً:
                    // 1. المخزون موجود في الفرع
                    // 2. الكمية المتاحة كافية للبيع
                    if (inventory != null && inventory.StockQuantity >= itemQty)
                    {
                        // ✅ إضافة قيمة الصنف للإجمالي فقط بعد التحقق من توفر المخزون
                                     grossTotal += Math.Round(item.Quantity * item.UnitPrice, 2);

                        decimal costPerUnit = (inventory.AverageCost ?? 0)
                            / (inventory.Drug?.ConversionFactor > 0 ? inventory.Drug.ConversionFactor : 1);
                        totalCogs += item.Quantity * costPerUnit;

                        // ✅ خصم المخزون فعلياً قبل إضافة الصنف للفاتورة
                        inventory.StockQuantity -= itemQty;
                        _context.Branchinventory.Update(inventory);

                        _context.Stockmovements.Add(new Stockmovements
                        {
                            BranchId = ActiveBranchId,
                            DrugId = item.DrugId,
                            MovementDate = DateTime.Now,
                            MovementType = "Sale Out (Offline Sync)",
                            Quantity = -itemQty,
                            UserId = userId,
                            Notes = "فاتورة مزامنة أوفلاين"
                        });

                        // ✅ الصنف يُضاف للفاتورة فقط هنا — داخل الشرط، بعد نجاح الخصم
                        sale.Saledetails.Add(new Saledetails
                        {
                            DrugId = item.DrugId,
                            Quantity = itemQty,
                            UnitPrice = item.UnitPrice
                        });
                    }
                    else
                    {
                        // ✅ تسجيل الأصناف التي تم تخطيها بسبب نقص المخزون
                        // لا نرمي Exception — نكمل الفاتورة بالأصناف المتاحة فقط
                        string drugName = inventory?.Drug?.DrugName ?? $"DrugId={item.DrugId}";
                        int availableQty = inventory?.StockQuantity ?? 0;
                        skippedItems.Add(new
                        {
                            drugId = item.DrugId,
                            drugName = drugName,
                            requestedQty = itemQty,
                            availableQty = availableQty,
                            reason = availableQty == 0 ? "الصنف نفد من المخزون" : "الكمية المطلوبة أكبر من المتاح"
                        });
                    }
                }

                // ✅ حماية من حفظ فاتورة فارغة تماماً (جميع الأصناف نفدت)
                if (!sale.Saledetails.Any())
                {
                    await transaction.RollbackAsync();
                    // لا نرمي exception بل نُعيد استجابة واضحة للـ PWA
                    return; // سيُعالَج أدناه
                }

                      sale.TotalAmount = Math.Round(grossTotal, 2);
                sale.NetAmount = Math.Round(sale.TotalAmount - sale.Discount + sale.TaxAmount, 2);


                _context.Sales.Add(sale);
                await _context.SaveChangesAsync();
                newSaleId = sale.SaleId;

                         decimal cashAmt = Math.Round(offlineData.CashAmount, 2);
                decimal bankAmt = Math.Round(offlineData.BankAmount, 2);
                decimal credit = Math.Round(sale.NetAmount - cashAmt - bankAmt, 2);
                if (credit < 0) credit = 0;

                if (cashAmt > 0 && offlineData.CashAccountId > 0)
                    _context.SalePayments.Add(new SalePayments
                    {
                        SaleId = sale.SaleId,
                        PaymentMethod = "Cash",
                        AccountId = offlineData.CashAccountId,
                        Amount = cashAmt
                    });
                if (bankAmt > 0 && offlineData.BankAccountId > 0)
                    _context.SalePayments.Add(new SalePayments
                    {
                        SaleId = sale.SaleId,
                        PaymentMethod = "Bank",
                        AccountId = offlineData.BankAccountId,
                        Amount = bankAmt
                    });
                if (credit > 0)
                    _context.SalePayments.Add(new SalePayments
                    {
                        SaleId = sale.SaleId,
                        PaymentMethod = "Credit",
                        Amount = credit
                    });

                await _context.SaveChangesAsync();

                var payload = new AccountingPayload
                {
                    TransactionType = TransactionType.SalesInvoice,
                    BranchId = ActiveBranchId,
                    UserId = userId,
                    ReferenceNo = sale.SaleId.ToString(),
                    Description = $"مبيعات POS (أوفلاين مزامنة) فاتورة #{sale.SaleId}",
                    CustomerId = sale.CustomerId,
                    SpecificCashAccountId = offlineData.CashAccountId > 0 ? offlineData.CashAccountId : null,
                    SpecificBankAccountId = offlineData.BankAccountId > 0 ? offlineData.BankAccountId : null
                };
                payload.Amounts.Add(AmountSource.NetTotalAmount, sale.NetAmount);
                payload.Amounts.Add(AmountSource.PaidCashAmount, cashAmt);
                payload.Amounts.Add(AmountSource.PaidBankAmount, bankAmt);
                payload.Amounts.Add(AmountSource.CreditAmount, credit);
                payload.Amounts.Add(AmountSource.COGSAmount, totalCogs);

                await _accountingEngine.ProcessTransactionAsync(payload);
                            await transaction.CommitAsync();
                        }
                        catch (DbUpdateConcurrencyException)
                        {
                            await transaction.RollbackAsync();
                            throw new Exception("تعارض في المزامنة: البيانات تغيرت على الخادم. سيتم إعادة المحاولة لاحقاً.");
                        }
                        catch (Exception) { await transaction.RollbackAsync(); throw; }
                    });

        // ✅ حالة: جميع الأصناف نفدت — لم تُحفظ أي فاتورة
        if (newSaleId == 0)
        {
            return Ok(new
            {
                success = false,
                saleId = 0,
                message = "لم تُحفظ الفاتورة: جميع الأصناف نفدت من المخزون.",
                skippedItems = skippedItems
            });
        }

        await RecordLog("OfflineSync", "Sales", $"مزامنة فاتورة أوفلاين #{newSaleId}، أصناف متخطاة: {skippedItems.Count}");

        return Ok(new
        {
            success = true,
            saleId = newSaleId,
            // ✅ إعلام الـ PWA بالأصناف التي لم تُحسب في الفاتورة
            skippedItems = skippedItems,
            hasSkippedItems = skippedItems.Any()
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { success = false, message = ex.Message });
    }
}

        // DTO لاستقبال بيانات الفاتورة الأوفلاين
        private class OfflineSaleDto
        {
            public int? CustomerId { get; set; }
            public decimal Discount { get; set; }
            public decimal TaxAmount { get; set; }
            public decimal CashAmount { get; set; }
             public int? CashAccountId { get; set; }
            public decimal BankAmount { get; set; }
             public int? BankAccountId { get; set; }
            public List<OfflineSaleItemDto> Items { get; set; } = new();
        }
        private class OfflineSaleItemDto
        {
            public int DrugId { get; set; }
            public decimal Quantity { get; set; }
            public decimal UnitPrice { get; set; }
        }
    }
}
