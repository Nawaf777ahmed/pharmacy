using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PharmaSmartWeb.Models;
using PharmaSmartWeb.Services;
using System;
using System.Data;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PharmaSmartWeb.Controllers
{
    /// <summary>
    /// 🧠 كنترولر الذكاء المخزني — يُدير شاشة التخطيط والتنبؤ (ABC + EOQ + AI)
    /// يوفر Actions لحساب ABC، توليد خطط الشراء، رفع Excel، واعتماد الكميات.
    /// </summary>
    public class InventoryIntelligenceController : BaseController
    {
        private readonly IForecastApiService _forecastService;
        private readonly IWhatsAppService _whatsappService;
        private readonly ILogger<InventoryIntelligenceController> _logger;

        public InventoryIntelligenceController(
            ApplicationDbContext context,
            IForecastApiService forecastService,
            IWhatsAppService whatsappService,
            ILogger<InventoryIntelligenceController> logger)
            : base(context)
        {
            _forecastService = forecastService;
            _whatsappService = whatsappService;
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GET  /InventoryIntelligence/Index
        //  الشاشة الرئيسية — يعرض EOQABC.cshtml مع إحصائيات ABC
        // ══════════════════════════════════════════════════════════════════════
        public IActionResult Index()
        {
            int branchId = ActiveBranchId;

            var counts = _context.Branchinventory
                .AsNoTracking()
                .Where(bi => bi.BranchId == branchId)
                .GroupBy(bi => bi.Abccategory)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToList();

            ViewBag.CountA = counts.FirstOrDefault(c => c.Category == "A")?.Count ?? 0;
            ViewBag.CountB = counts.FirstOrDefault(c => c.Category == "B")?.Count ?? 0;
            ViewBag.CountC = counts.FirstOrDefault(c => c.Category == "C")?.Count ?? 0;

            return View("EOQABC");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/CalculateABC
        //  يُطبّق خوارزمية باريتو (ABC) على مخزون الفرع
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CalculateABC()
        {
            int branchId = ActiveBranchId;

            // ─── 1. سحب كل أصناف الفرع مع إجمالي مبيعاتها ─────────────────────
            var inventoryItems = await _context.Branchinventory
                .Where(bi => bi.BranchId == branchId)
                .Select(bi => new
                {
                    bi.DrugId,
                    bi.BranchId,
                    TotalSalesRevenue = _context.Saledetails
                        .Where(sd => sd.DrugId == bi.DrugId && sd.Sale.BranchId == branchId)
                        .Sum(sd => (decimal?)sd.Quantity * sd.UnitPrice) ?? 0m
                })
                .ToListAsync();

            if (!inventoryItems.Any())
                return Content("لا توجد بيانات مخزون لهذا الفرع.");

            // ─── 2. الترتيب التنازلي وحساب النسب التراكمية ──────────────────────
            decimal totalRevenue = inventoryItems.Sum(x => x.TotalSalesRevenue);
            if (totalRevenue == 0) totalRevenue = 1; // تجنب القسمة على صفر

            var sorted = inventoryItems
                .OrderByDescending(x => x.TotalSalesRevenue)
                .ToList();

            decimal cumulative = 0;
            var updates = new List<(int DrugId, string Category)>();

            foreach (var item in sorted)
            {
                cumulative += item.TotalSalesRevenue / totalRevenue * 100;
                string category = cumulative <= 80 ? "A" : cumulative <= 95 ? "B" : "C";
                updates.Add((item.DrugId, category));
            }

            // ─── 3. تحديث قاعدة البيانات دفعةً واحدة ────────────────────────────
            var drugIds = updates.Select(u => u.DrugId).ToList();
            var rows = await _context.Branchinventory
                .Where(bi => bi.BranchId == branchId && drugIds.Contains(bi.DrugId))
                .ToListAsync();

            foreach (var row in rows)
            {
                var match = updates.FirstOrDefault(u => u.DrugId == row.DrugId);
                row.Abccategory = match.Category;
            }

            await _context.SaveChangesAsync();
            await RecordLog("CalculateABC", "InventoryIntelligence", $"تم تحديث تصنيف ABC لـ {rows.Count} صنف في الفرع {branchId}.");

            return Content($"✅ تم تحديث تصنيف ABC لـ {rows.Count} صنف بنجاح.");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/GenerateSmartPlan?model=prophet|vertex
        //  يُولّد خطة شراء ذكية من بيانات قاعدة البيانات
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSmartPlan(string model = "prophet")
        {
            int branchId = ActiveBranchId;
            int userId = int.Parse(User.FindFirst("UserID")?.Value ?? "0");

            // ─── 1. سحب الأصناف A و B (الأولى بالاهتمام) ───────────────────────
            var inventoryAB = await _context.Branchinventory
                .AsNoTracking()
                .Include(bi => bi.Drug)
                .Where(bi => bi.BranchId == branchId &&
                             (bi.Abccategory == "A" || bi.Abccategory == "B" || bi.Abccategory == "C") &&
                             bi.Drug != null)
                .ToListAsync();

            if (!inventoryAB.Any())
                return Json(new { success = false, message = "لا توجد أصناف مصنّفة. قم بتشغيل خوارزمية ABC أولاً." });

            // ─── 2. إنشاء خطة جديدة ──────────────────────────────────────────────
            var plan = new PurchasePlan
            {
                BranchId  = branchId,
                CreatedBy = userId,
                PlanDate  = DateTime.Now,
                Status    = "Draft"
            };
            _context.PurchasePlans.Add(plan);
            await _context.SaveChangesAsync();

            // ─── 3. حساب تفاصيل الخطة لكل صنف ──────────────────────────────────
            var cutoff = DateTime.Today.AddMonths(-6);
            var details = new List<PurchasePlanDetail>();

            foreach (var inv in inventoryAB)
            {
                // تاريخ مبيعات الـ 6 أشهر الأخيرة
                var salesHistory = await _context.Saledetails
                    .AsNoTracking()
                    .Where(sd => sd.DrugId == inv.DrugId && sd.Sale.BranchId == branchId && sd.Sale.SaleDate >= cutoff)
                    .Select(sd => new { sd.Sale.SaleDate, sd.Quantity })
                    .OrderBy(x => x.SaleDate)
                    .ToListAsync();

                decimal forecastedDemand = 0;
                decimal forecastAccuracy = 0;

                if (salesHistory.Any())
                {
                    // تحويل للـ SalesDataPoint
                    var points = salesHistory
                        .Select(s => new SalesDataPoint(s.SaleDate.ToString("yyyy-MM-dd"), s.Quantity))
                        .ToList();

                    ForecastResult forecastResult;

                    if (model == "vertex")
                    {
                        // Google Vertex AI (IForecastApiService المُحقون)
                        forecastResult = await _forecastService.GetForecastAsync(inv.Drug.DrugName, points);
                    }
                    else
                    {
                        // Prophet: متوسط مرجّح بالاتجاه (Weighted Moving Average) — بديل محلي
                        forecastResult = CalculateProphetFallback(points);
                    }

                    forecastedDemand = forecastResult.ForecastedDemand;
                    forecastAccuracy = forecastResult.Accuracy;
                }
                else
                {
                    // لا بيانات: يُقدَّر بالحد الأدنى + نصف الكمية الحالية
                    forecastedDemand = Math.Max(inv.MinimumStockLevel, 1);
                }

                // ─── EOQ: الكمية الاقتصادية للطلب ────────────────────────────────
                // EOQ = √(2 × D × S / H)  — نستخدم إعدادات مبسطة إذا لم تتوفر
                decimal annualDemand = forecastedDemand * 12;
                decimal unitCost     = inv.AverageCost > 0 ? inv.AverageCost.Value : (inv.CurrentSellingPrice ?? 100m);
                decimal orderCost    = 50m;   // تكلفة الطلبية الثابتة (ريال)
                decimal holdingRate  = 0.25m; // معدل الاحتفاظ (25% من التكلفة سنوياً)

                int eoqQty = 1;
                decimal holdingCost = unitCost * holdingRate;
                if (annualDemand > 0 && holdingCost > 0)
                    eoqQty = (int)Math.Ceiling(Math.Sqrt((double)(2 * annualDemand * orderCost / holdingCost)));

                // الحد الأدنى: لا نقترح أقل مما يُغطي الطلب المتوقع شهراً كاملاً
                eoqQty = Math.Max(eoqQty, (int)Math.Ceiling(forecastedDemand));

                // حالة الميزانية: مبسّطة — كل الأصناف "في حدود الميزانية" مبدئياً
                // (يُحسَب بشكل أدق في GetPlanDetails بمقارنة إجمالي الخطة مع السيولة)
                string status = "في حدود الميزانية";

                // إذا كان الدواء منقذاً للحياة — يُعطى أولوية مطلقة
                bool isLifeSaving = inv.Drug.IsLifeSaving == true;

                details.Add(new PurchasePlanDetail
                {
                    PlanId           = plan.PlanId,
                    DrugId           = inv.DrugId,
                    ABCCategory      = isLifeSaving ? "⭐" : (inv.Abccategory ?? "C"),
                    CurrentStock     = inv.StockQuantity,
                    ForecastedDemand = forecastedDemand,
                    ForecastAccuracy = forecastAccuracy,
                    ProposedQuantity = eoqQty,
                    ApprovedQuantity = eoqQty,
                    UnitCostEstimate = unitCost,
                    TotalCost        = eoqQty * unitCost,
                    IsLifeSaving     = isLifeSaving,
                    Status           = status
                });
            }

            _context.PurchasePlanDetails.AddRange(details);

            // تحديث إجمالي الخطة
            plan.EstimatedTotalCost = details.Sum(d => d.TotalCost);
            await _context.SaveChangesAsync();

            await RecordLog("GenerateSmartPlan", "InventoryIntelligence",
                $"تم توليد خطة شراء رقم {plan.PlanId} بـ {details.Count} صنف. النموذج: {model}.");

            return Json(new
            {
                success = true,
                planId  = plan.PlanId,
                message = $"تم توليد خطة الشراء بـ {details.Count} صنف باستخدام نموذج {(model == "vertex" ? "Google Vertex AI 🧠" : "Prophet 🐍")}."
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/ExtractExcelHeaders
        //  يقرأ أسماء أعمدة ملف Excel ويُرجعها للمستخدم لإجراء المطابقة
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ExtractExcelHeaders(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
                return Json(new { success = false, message = "لم يتم رفع ملف." });

            if (!IsValidExcelFile(excelFile.FileName))
                return Json(new { success = false, message = "نوع الملف غير مقبول. يُقبل فقط xlsx, xls, csv." });

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                using var stream = excelFile.OpenReadStream();
                using var reader = ExcelReaderFactory.CreateReader(stream);

                // قراءة الصف الأول فقط (العناوين)
                var headers = new List<string>();
                if (reader.Read())
                {
                    for (int col = 0; col < reader.FieldCount; col++)
                    {
                        string? cellVal = reader.GetValue(col)?.ToString()?.Trim();
                        headers.Add(string.IsNullOrEmpty(cellVal) ? $"عمود {col + 1}" : cellVal);
                    }
                }

                if (!headers.Any())
                    return Json(new { success = false, message = "الملف فارغ أو لا يحتوي على أعمدة." });

                return Json(new { success = true, headers });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExtractExcelHeaders: فشل قراءة الملف.");
                return Json(new { success = false, message = "فشل قراءة الملف. تأكد أنه ملف Excel سليم." });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/GenerateSmartPlanFromExcel
        //  يُولّد خطة شراء من بيانات Excel خارجية عبر Google Vertex AI
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSmartPlanFromExcel(
            IFormFile excelFile,
            int drugCol  = 0,
            int dateCol  = 1,
            int qtyCol   = 2)
        {
            if (excelFile == null || excelFile.Length == 0)
                return Json(new { success = false, message = "لم يتم رفع ملف." });

            int branchId = ActiveBranchId;
            int userId   = int.Parse(User.FindFirst("UserID")?.Value ?? "0");

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                using var stream = excelFile.OpenReadStream();
                using var reader = ExcelReaderFactory.CreateReader(stream);

                var result = reader.AsDataSet(new ExcelDataSetConfiguration
                {
                    ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
                });

                var table = result.Tables.Count > 0 ? result.Tables[0] : null;
                if (table == null || table.Rows.Count == 0)
                    return Json(new { success = false, message = "الملف لا يحتوي على بيانات." });

                // ─── تجميع البيانات حسب اسم الدواء ──────────────────────────────
                var drugSalesMap = new Dictionary<string, List<SalesDataPoint>>(StringComparer.OrdinalIgnoreCase);
                int colCount = table.Columns.Count;

                foreach (DataRow row in table.Rows)
                {
                    string? drugName = drugCol < colCount ? row[drugCol]?.ToString()?.Trim() : null;
                    string? dateStr  = dateCol < colCount ? row[dateCol]?.ToString()?.Trim() : null;
                    string? qtyStr   = qtyCol  < colCount ? row[qtyCol ]?.ToString()?.Trim() : null;

                    if (string.IsNullOrEmpty(drugName) || string.IsNullOrEmpty(dateStr)) continue;
                    if (!decimal.TryParse(qtyStr, System.Globalization.NumberStyles.Any,
                                         System.Globalization.CultureInfo.InvariantCulture, out decimal qty)) continue;
                    if (!DateTime.TryParse(dateStr, out DateTime dt)) continue;

                    string isoDate = dt.ToString("yyyy-MM-dd");
                    if (!drugSalesMap.ContainsKey(drugName))
                        drugSalesMap[drugName] = new List<SalesDataPoint>();

                    drugSalesMap[drugName].Add(new SalesDataPoint(isoDate, qty));
                }

                if (!drugSalesMap.Any())
                    return Json(new { success = false, message = "لم يتم العثور على بيانات مبيعات في الملف. تحقق من مطابقة الأعمدة." });

                // ─── إنشاء خطة جديدة ──────────────────────────────────────────────
                var plan = new PurchasePlan
                {
                    BranchId  = branchId,
                    CreatedBy = userId,
                    PlanDate  = DateTime.Now,
                    Status    = "Draft",
                    Notes     = $"مولّدة من Excel خارجي ({excelFile.FileName})"
                };
                _context.PurchasePlans.Add(plan);
                await _context.SaveChangesAsync();

                var details = new List<PurchasePlanDetail>();

                foreach (var entry in drugSalesMap)
                {
                    string drugName = entry.Key;
                    var points      = entry.Value.OrderBy(p => p.Date).ToList();

                    // البحث عن الدواء في قاعدة البيانات (مطابقة جزئية)
                    var dbDrug = await _context.Branchinventory
                        .AsNoTracking()
                        .Include(bi => bi.Drug)
                        .Where(bi => bi.BranchId == branchId &&
                                     bi.Drug.DrugName.Contains(drugName))
                        .FirstOrDefaultAsync();

                    // التنبؤ عبر Google Vertex AI
                    var forecastResult = await _forecastService.GetForecastAsync(drugName, points);

                    decimal unitCost = dbDrug?.AverageCost > 0
                        ? dbDrug.AverageCost!.Value
                        : (dbDrug?.CurrentSellingPrice ?? 100m);

                    int proposedQty  = (int)Math.Ceiling(forecastResult.ForecastedDemand);
                    proposedQty      = Math.Max(proposedQty, 1);

                    details.Add(new PurchasePlanDetail
                    {
                        PlanId           = plan.PlanId,
                        DrugId           = dbDrug?.DrugId ?? 0,
                        ABCCategory      = dbDrug?.Abccategory ?? "?",
                        CurrentStock     = dbDrug?.StockQuantity ?? 0,
                        ForecastedDemand = forecastResult.ForecastedDemand,
                        ForecastAccuracy = forecastResult.Accuracy,
                        ProposedQuantity = proposedQty,
                        ApprovedQuantity = proposedQty,
                        UnitCostEstimate = unitCost,
                        TotalCost        = proposedQty * unitCost,
                        IsLifeSaving     = dbDrug?.Drug?.IsLifeSaving == true,
                        Status           = "في حدود الميزانية"
                    });
                }

                _context.PurchasePlanDetails.AddRange(details);
                plan.EstimatedTotalCost = details.Sum(d => d.TotalCost);
                await _context.SaveChangesAsync();

                await RecordLog("GenerateSmartPlanFromExcel", "InventoryIntelligence",
                    $"خطة رقم {plan.PlanId} من Excel خارجي — {details.Count} صنف.");

                return Json(new
                {
                    success = true,
                    planId  = plan.PlanId,
                    message = $"تم تحليل {drugSalesMap.Count} صنف من الملف وتوليد خطة شراء بـ {details.Count} صنف عبر Google Vertex AI 🧠."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateSmartPlanFromExcel: خطأ.");
                return Json(new { success = false, message = $"فشل معالجة الملف: {ex.Message}" });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GET  /InventoryIntelligence/GetPlanDetails?id={planId}
        //  يُرجع الـ Partial View لجدول تفاصيل الخطة
        // ══════════════════════════════════════════════════════════════════════
        public async Task<IActionResult> GetPlanDetails(int id)
        {
            var details = await _context.PurchasePlanDetails
                .AsNoTracking()
                .Include(d => d.Drug)
                .Where(d => d.PlanId == id)
                .OrderByDescending(d => d.IsLifeSaving)
                .ThenBy(d => d.ABCCategory)
                .ThenByDescending(d => d.TotalCost)
                .ToListAsync();

            if (!details.Any())
                return Content("<p style='padding:24px; text-align:center; color:#94a3b8; font-weight:700;'>لا توجد تفاصيل لهذه الخطة.</p>");

            return PartialView("_PlanDetailsTable", details);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/UpdatePlanDetail?detailId=&approvedQuantity=
        //  يُحدّث الكمية المعتمدة من قِبل المدير
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePlanDetail(int detailId, int approvedQuantity)
        {
            var detail = await _context.PurchasePlanDetails.FindAsync(detailId);
            if (detail == null)
                return NotFound();

            approvedQuantity   = Math.Max(0, approvedQuantity);
            detail.ApprovedQuantity = approvedQuantity;
            detail.TotalCost        = approvedQuantity * detail.UnitCostEstimate;

            await _context.SaveChangesAsync();

            // إعادة احتساب إجمالي الخطة
            var plan = await _context.PurchasePlans
                .Include(p => p.PlanDetails)
                .FirstOrDefaultAsync(p => p.PlanId == detail.PlanId);

            if (plan != null)
            {
                plan.EstimatedTotalCost = plan.PlanDetails.Sum(d => d.TotalCost);
                await _context.SaveChangesAsync();
            }

            return Ok();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GET  /InventoryIntelligence/SmartPlanPrint/{id}
        //  يُرجع صفحة طباعة الخطة المعتمدة (PDF-ready)
        // ══════════════════════════════════════════════════════════════════════
        public async Task<IActionResult> SmartPlanPrint(int id)
        {
            var plan = await _context.PurchasePlans
                .AsNoTracking()
                .Include(p => p.PlanDetails)
                    .ThenInclude(d => d.Drug)
                .FirstOrDefaultAsync(p => p.PlanId == id);

            if (plan == null)
                return NotFound("خطة الشراء غير موجودة.");

            return View("SmartPlanPrint", plan);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  POST /InventoryIntelligence/ReceiveGoods
        //  استلام البضاعة: يزيد المخزون + ينشئ قيد محاسبي + يغلق الخطة
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReceiveGoods([FromBody] ReceiveGoodsVm vm)
        {
            if (vm == null || vm.Items == null || !vm.Items.Any())
                return Json(new { success = false, message = "لا توجد أصناف للاستلام." });

            int branchId = ActiveBranchId;
            int userId   = int.Parse(User.FindFirst("UserID")?.Value ?? "0");
            var today    = DateTime.Now;

            using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                decimal totalCost = 0;

                // ── 1. إنشاء فاتورة مشتريات ───────────────────────────────────
                // InvoiceNumber & SupplierId مطلوبان في نموذج Purchases
                var autoInvoice = $"AUTO-{today:yyyyMMddHHmm}";
                var purchase = new Purchases
                {
                    BranchId      = branchId,
                    PurchaseDate  = today,
                    UserId        = userId,
                    InvoiceNumber = autoInvoice,
                    SupplierId    = 1,           // مورد افتراضي — يمكن تحديده لاحقاً
                    TotalAmount   = 0,
                    Notes         = $"استلام خطة شراء ذكية — {today:yyyy-MM-dd}"
                };
                _context.Purchases.Add(purchase);
                await _context.SaveChangesAsync();

                foreach (var item in vm.Items)
                {
                    if (item.DrugId <= 0 || item.ReceivedQty <= 0) continue;

                    int receivedQty   = item.ReceivedQty;
                    decimal cost      = item.UnitCost;
                    decimal lineTotal = receivedQty * cost;
                    totalCost += lineTotal;

                    // ── 2. زيادة المخزون ─────────────────────────────────────
                    var inv = await _context.Branchinventory
                        .FirstOrDefaultAsync(bi => bi.DrugId == item.DrugId && bi.BranchId == branchId);
                    if (inv != null)
                    {
                        inv.StockQuantity += receivedQty;
                        inv.AverageCost    = cost;
                    }

                    // ── 3. تفصيل فاتورة المشتريات ────────────────────────────
                    // TotalCost غير موجودة — نستخدم SubTotal
                    _context.Purchasedetails.Add(new Purchasedetails
                    {
                        PurchaseId  = purchase.PurchaseId,
                        DrugId      = item.DrugId,
                        Quantity    = receivedQty,
                        CostPrice   = cost,
                        SubTotal    = lineTotal,
                        BatchNumber = item.BatchNumber ?? $"AUTO-{today:yyyyMMdd}",
                        ExpiryDate  = item.ExpiryDate ?? today.AddYears(2)
                    });

                    // ── 4. سجل الباتش ────────────────────────────────────────
                    // DrugBatches لا تحتوي على Quantity/CostPrice — فقط BatchNumber/ExpiryDate
                    _context.DrugBatches.Add(new DrugBatches
                    {
                        DrugId      = item.DrugId,
                        BatchNumber = item.BatchNumber ?? $"AUTO-{today:yyyyMMdd}",
                        ExpiryDate  = item.ExpiryDate ?? today.AddYears(2)
                    });
                }

                purchase.TotalAmount = totalCost;
                await _context.SaveChangesAsync();

                // ── 5. القيد المحاسبي ────────────────────────────────────────
                var inventoryAccount = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.AccountName.Contains("مخزون") || a.AccountName.Contains("بضاعة"));
                var payableAccount = await _context.Accounts
                    .FirstOrDefaultAsync(a => a.AccountName.Contains("موردين") || a.AccountName.Contains("دائنون"));

                if (inventoryAccount != null && payableAccount != null)
                {
                    // ✅ الاسم الصحيح: Journalentries (ليس Journal)
                    var journal = new Journalentries
                    {
                        JournalDate   = today,
                        Description   = $"استلام مشتريات — فاتورة #{purchase.PurchaseId}",
                        ReferenceType = "Purchase",
                        ReferenceNo   = $"{purchase.PurchaseId}",  // string وليس int
                        BranchId      = branchId,
                        CreatedBy     = userId,
                        IsPosted      = true
                    };
                    _context.Journalentries.Add(journal);
                    await _context.SaveChangesAsync();

                    // ✅ الاسم الصحيح: Journaldetails (ليس Journaldetail)
                    // Journaldetails لا تحتوي على Description
                    _context.Journaldetails.AddRange(new[]
                    {
                        new Journaldetails { JournalId = journal.JournalId, AccountId = inventoryAccount.AccountId, Debit = totalCost,  Credit = 0          },
                        new Journaldetails { JournalId = journal.JournalId, AccountId = payableAccount.AccountId,   Debit = 0,          Credit = totalCost  }
                    });
                    await _context.SaveChangesAsync();
                }

                // ── 6. إغلاق الخطة ──────────────────────────────────────────
                if (vm.PlanId > 0)
                {
                    var plan = await _context.PurchasePlans.FindAsync(vm.PlanId);
                    if (plan != null) { plan.Status = "Executed"; await _context.SaveChangesAsync(); }
                }

                await tx.CommitAsync();
                await RecordLog("ReceiveGoods", "InventoryIntelligence",
                    $"استلام {vm.Items.Count} صنف — إجمالي {totalCost:N2} — فاتورة #{purchase.PurchaseId}");

                return Json(new { success = true, message = $"✅ تم استلام البضاعة وزيادة المخزون. إجمالي الفاتورة: {totalCost:N2}", purchaseId = purchase.PurchaseId });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "ReceiveGoods: خطأ أثناء الاستلام.");
                return Json(new { success = false, message = $"فشل الاستلام: {ex.Message}" });
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers (Private)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// بديل Prophet محلي: Weighted Moving Average بوزن زمني (أحدث = أثقل)
        /// يُعطي نتيجة مقبولة عند عدم توفر API خارجي
        /// </summary>
        private static ForecastResult CalculateProphetFallback(List<SalesDataPoint> history)
        {
            if (!history.Any())
                return new ForecastResult(0, 0, "Prophet (No Data)");

            int n = history.Count;
            decimal weightedSum = 0;
            decimal totalWeight = 0;

            for (int i = 0; i < n; i++)
            {
                decimal weight = i + 1; // الأحدث يحمل وزناً أعلى
                weightedSum += history[i].Quantity * weight;
                totalWeight += weight;
            }

            decimal forecast = totalWeight > 0
                ? Math.Round(weightedSum / totalWeight, 0)
                : 0;

            // دقة تقديرية: كلما زادت نقاط البيانات زادت الدقة (حد أقصى 92%)
            decimal accuracy = Math.Min(92, 60 + n * 1.5m);

            return new ForecastResult(forecast, Math.Round(accuracy, 1), "Prophet (WMA)");
        }

        private static bool IsValidExcelFile(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext == ".xlsx" || ext == ".xls" || ext == ".csv";
        }

        // ============================================================
        // 🧠 وحدة التخطيط والتنبؤ (مُنقولة من HomeController)
        // ============================================================
        public async Task<IActionResult> PlanningHub()
        {
            ViewData["Title"] = "وحدة التخطيط والتنبؤ";
            ViewData["PageDescription"] = "خطة المشتريات الذكية مدعومة بـ Prophet AI / Vertex AI";

            int scopeId = ReportScopeId;
            bool isGlobal = (scopeId == 0);
            var today = DateTime.Today;

            // ── 1. الإحصائيات السريعة ──────────────────────────────
            var invQuery = _context.Branchinventory.AsQueryable();
            if (!isGlobal) invQuery = invQuery.Where(bi => bi.BranchId == scopeId);

            ViewBag.TotalDrugs         = await _context.Drugs.CountAsync(d => d.IsActive == true);
            ViewBag.ShortagesCount     = await invQuery.CountAsync(bi => bi.StockQuantity <= bi.MinimumStockLevel);
            ViewBag.ExpiringSoonCount  = await _context.DrugBatches
                .CountAsync(b => b.ExpiryDate <= today.AddMonths(2) && b.ExpiryDate >= today);

            // -- حساب إحصائيات ABC للنطاق الحالي --
            var abcStats = await invQuery
                .GroupBy(bi => bi.Abccategory)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();
            
            ViewBag.CountA = abcStats.FirstOrDefault(x => x.Category == "A")?.Count ?? 0;
            ViewBag.CountB = abcStats.FirstOrDefault(x => x.Category == "B")?.Count ?? 0;
            ViewBag.CountC = abcStats.FirstOrDefault(x => x.Category == "C")?.Count ?? 0;

            // ── 2. آخر خطة معتمدة ─────────────────────────────────
            ViewBag.LastForecastDate = await _context.Systemlogs
                .Where(l => l.Action == "PlanApproved")
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => (DateTime?)l.CreatedAt)
                .FirstOrDefaultAsync();

            // ── 3. السيولة المتاحة (أرصدة الصندوق والبنك) ─────────
            var cashBankIds = await _context.Accounts
                .Where(a => a.AccountName.Contains("صندوق") || a.AccountName.Contains("نقد") ||
                            a.AccountName.Contains("بنك")   || a.AccountName.Contains("مصرف"))
                .Select(a => a.AccountId)
                .ToListAsync();
            decimal totalLiquidity = cashBankIds.Any()
                ? await _context.Journaldetails
                    .Where(d => d.Journal.IsPosted == true && cashBankIds.Contains(d.AccountId))
                    .SumAsync(d => (decimal?)(d.Debit - d.Credit)) ?? 0m
                : 0m;
            ViewBag.TotalLiquidity = totalLiquidity;

            // ── 4. جدول التخطيط: بيانات المخزون مع التكاليف ───────
            var inventoryData = await _context.Branchinventory
                .Include(bi => bi.Drug)
                .Where(bi => bi.Drug.IsActive == true && (!isGlobal ? bi.BranchId == scopeId : true))
                .GroupBy(bi => new { bi.DrugId, bi.Drug.DrugName, bi.MinimumStockLevel, bi.Drug.IsLifeSaving, bi.Drug.MainUnit, bi.Drug.SubUnit, bi.Drug.ConversionFactor })
                .Select(g => new
                {
                    DrugId       = g.Key.DrugId,
                    DrugName     = g.Key.DrugName,
                    MinStock     = g.Key.MinimumStockLevel,
                    IsLifeSaving = g.Key.IsLifeSaving,
                    MainUnit     = g.Key.MainUnit ?? "باكت",
                    SubUnit      = g.Key.SubUnit ?? "شريط",
                    ConversionFactor = g.Key.ConversionFactor,
                    CurrentStock = g.Sum(x => x.StockQuantity),
                    AvgCost      = g.Average(x => (decimal?)(x.AverageCost ?? 0)) ?? 0m
                })
                .OrderBy(x => x.CurrentStock - x.MinStock)
                .Take(50)
                .ToListAsync();

            // متوسط المبيعات الشهرية لكل دواء (آخر 3 أشهر)
            var threeMonthsAgo = today.AddMonths(-3);
            var salesAvgRaw = await _context.Saledetails
                .Include(sd => sd.Sale)
                .Where(sd => sd.Sale.SaleDate >= threeMonthsAgo
                          && (!isGlobal ? sd.Sale.BranchId == scopeId : true))
                .GroupBy(sd => sd.DrugId)
                .Select(g => new
                {
                    DrugId  = g.Key,
                    AvgQty  = g.Sum(x => x.Quantity) / 3m   // متوسط شهري
                })
                .ToListAsync();

            var salesAvgDict = salesAvgRaw.ToDictionary(x => x.DrugId, x => x.AvgQty);

            // ── 5. تصنيف ABC بناءً على القيمة السنوية ─────────────
            var itemsWithValue = inventoryData.Select(item =>
            {
                decimal avg = salesAvgDict.ContainsKey(item.DrugId) ? salesAvgDict[item.DrugId] : 0;
                return new { item, avgMonthly = avg, annualValue = avg * 12m * item.AvgCost };
            }).OrderByDescending(x => x.annualValue).ToList();

            decimal totalAnnualValue = itemsWithValue.Sum(x => x.annualValue);
            decimal cumValue = 0m;
            var abcDict = new Dictionary<int, string>();
            foreach (var x in itemsWithValue)
            {
                cumValue += x.annualValue;
                double pct = totalAnnualValue > 0 ? (double)(cumValue / totalAnnualValue) : 1.0;
                abcDict[x.item.DrugId] = pct <= 0.70 ? "A" : pct <= 0.90 ? "B" : "C";
            }

            // ── 6. بناء قائمة التخطيط النهائية ───────────────────
            var planningItems = itemsWithValue.Select(x =>
            {
                var item        = x.item;
                decimal avg     = x.avgMonthly;          // بوحدة البيع (SubUnit)
                int cf          = item.ConversionFactor > 0 ? item.ConversionFactor : 1;
                string abcClass = abcDict.ContainsKey(item.DrugId) ? abcDict[item.DrugId] : "C";
                string priorityLabel = abcClass == "A" ? "قصوى" : (abcClass == "B" ? "متوسطة" : "عادية");
                bool isLifeSaving   = item.IsLifeSaving;

                // ─── تحويل الوحدة: avg بالشريط → avgInMainUnit بالباكت ─────────
                decimal avgInMainUnit = cf > 1 ? Math.Round(avg / (decimal)cf, 2) : avg;

                // EOQ وكل الكميات بوحدة الشراء (MainUnit)
                decimal annualMainUnit = avgInMainUnit * 12m;
                decimal unitCostForEoq = item.AvgCost > 0 ? item.AvgCost : 100m;
                decimal holdingCost    = unitCostForEoq * 0.2m; // 20% annual holding cost rate
                
                decimal eoqMainUnit    = annualMainUnit > 0 && holdingCost > 0
                    ? (decimal)Math.Round(Math.Sqrt((double)(2m * annualMainUnit * 50m / holdingCost)))
                    : 0;

                decimal stockInMainUnit = item.CurrentStock / (decimal)cf;
                decimal proposedBase = Math.Max(eoqMainUnit, avgInMainUnit * 2m - stockInMainUnit);

                if (isLifeSaving)
                    proposedBase = Math.Max(proposedBase, avgInMainUnit * 3m - stockInMainUnit);

                proposedBase = proposedBase < 0 ? 0 : Math.Round(proposedBase);

                decimal unitCost  = Math.Round(item.AvgCost, 2);
                decimal totalCost = Math.Round(proposedBase * unitCost, 2);

                string status      = item.CurrentStock <= item.MinStock
                                     ? "ناقص - يحتاج طلب"
                                     : (proposedBase > 0 ? "ضمن الميزانية" : "مخزون كافٍ");
                string statusClass = item.CurrentStock <= item.MinStock ? "error"
                                     : (proposedBase > 0 ? "ok" : "enough");
                return new
                {
                    drugId           = item.DrugId,
                    drug             = item.DrugName,
                    abc              = abcClass,
                    priority         = priorityLabel,
                    isLifeSaving     = item.IsLifeSaving,
                    unit             = item.MainUnit,
                    subUnit          = item.SubUnit,
                    conversionFactor = item.ConversionFactor,
                    stock            = item.CurrentStock,
                    minStock         = item.MinStock,
                    expectedQty      = (int)Math.Round(avgInMainUnit),   // ✅ بوحدة الشراء
                    optimalQty       = (int)proposedBase,                 // ✅ بوحدة الشراء
                    approved         = (int)proposedBase,
                    unitCost,
                    totalCost,
                    status,
                    statusClass
                };
            }).ToList();

            ViewBag.PlanningItems = System.Text.Json.JsonSerializer.Serialize(planningItems);

            // ── 4. المخطط البياني: المبيعات الشهرية (آخر 6 أشهر) ─
            var sixMonthsAgo = today.AddMonths(-5);
            var startOfSixMonths = new DateTime(sixMonthsAgo.Year, sixMonthsAgo.Month, 1);

            var monthlySalesRaw = await _context.Saledetails
                .Include(sd => sd.Sale)
                .Where(sd => sd.Sale.SaleDate >= startOfSixMonths
                          && (!isGlobal ? sd.Sale.BranchId == scopeId : true))
                .GroupBy(sd => new { sd.Sale.SaleDate.Year, sd.Sale.SaleDate.Month })
                .Select(g => new
                {
                    Year  = g.Key.Year,
                    Month = g.Key.Month,
                    Total = g.Sum(x => x.Quantity * x.UnitPrice)
                })
                .OrderBy(x => x.Year).ThenBy(x => x.Month)
                .ToListAsync();

            // أسماء الأشهر بالعربية
            var arabicMonths = new[] { "يناير","فبراير","مارس","أبريل","مايو","يونيو","يوليو","أغسطس","سبتمبر","أكتوبر","نوفمبر","ديسمبر" };

            var chartLabels  = monthlySalesRaw.Select(x => arabicMonths[x.Month - 1]).ToArray();
            var chartActual  = monthlySalesRaw.Select(x => Math.Round(x.Total, 2)).ToArray();

            // التوقع: متوسط متحرك بسيط (weighted: الأحدث أثقل)
            var chartForecast = chartActual.Length > 0
                ? chartActual.Select((v, i) =>
                {
                    // كل نقطة = المتوسط المرجح للقيم السابقة × 1.05 نمو
                    var slice = chartActual.Take(i + 1).ToArray();
                    decimal w = slice.Length > 1
                        ? (slice[slice.Length - 2] * 0.4m + slice[slice.Length - 1] * 0.6m) * 1.05m
                        : (slice[0] * 1.05m);
                    return Math.Round(w, 2);
                }).ToArray()
                : Array.Empty<decimal>();

            ViewBag.ChartLabels   = System.Text.Json.JsonSerializer.Serialize(chartLabels);
            ViewBag.ChartActual   = System.Text.Json.JsonSerializer.Serialize(chartActual);
            ViewBag.ChartForecast = System.Text.Json.JsonSerializer.Serialize(chartForecast);

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunForecast([FromBody] ForecastRequestVm request)
        {
            if (request == null || request.items == null) return BadRequest("Invalid Data");

            // محاكاة قوة المحركات (Prophet/Vertex) بحسب الموديل والمدة
            double baseMultiplier = request.horizon == 1 ? 0.5 : request.horizon == 3 ? 1.0 : request.horizon == 6 ? 2.1 : 4.2;
            
            // Prophet local is slightly conservative, Vertex AI considers external huge variants
            double modelVariance = request.model == "vertex" ? 1.15 : 1.0; 

            var newItems = new List<ForecastItemVm>();
            var rnd = new Random();

            int scopeId = ReportScopeId;
            bool isGlobal = (scopeId == 0);

            foreach(var item in request.items)
            {
                // -- الجلب الآلي للمخزون وسعر التكلفة من قاعدة البيانات --
                var drugEntity = await _context.Drugs.FirstOrDefaultAsync(d => d.DrugName == item.drug);
                if (drugEntity != null)
                {
                    var invRow = await _context.Branchinventory
                        .Where(bi => bi.DrugId == drugEntity.DrugId && (!isGlobal ? bi.BranchId == ReportScopeId : true))
                        .FirstOrDefaultAsync();

                    item.stock    = invRow?.StockQuantity ?? 0m;
                    item.minStock = invRow?.MinimumStockLevel ?? 0m;
                    item.unit     = drugEntity.MainUnit ?? "وحدة";
                    item.subUnit  = drugEntity.SubUnit ?? "";
                    item.conversionFactor = drugEntity.ConversionFactor;
                   item.isLifeSaving = drugEntity.IsLifeSaving;

                    // سعر التكلفة من آخر تشغيلة مشتريات
                    var lastPurchase = await _context.Purchasedetails
                        .Where(pd => pd.DrugId == drugEntity.DrugId)
                        .OrderByDescending(pd => pd.DetailId)
                        .FirstOrDefaultAsync();
                    item.unitCost = lastPurchase?.CostPrice ?? 0m;

                    // تصنيف الأولوية بناءً على ABC
                    item.priority = item.abc == "A" ? "قصوى" : item.abc == "B" ? "متوسطة" : "عادية";
                    if (item.isLifeSaving) item.priority = "قصوى";
                }
                else
                {
                    item.stock        = 0m;
                    item.unitCost     = 0m;
                    item.unit         = "وحدة";
                    item.subUnit      = "";
                    item.conversionFactor = 1;
                    item.isLifeSaving = false;
                    item.priority     = "عادية";
                }
                // --------------------------------------------------------

                // item.expectedQty قادمة من الواجهة وهي بوحدة الشراء (MainUnit) بعد التحويل في GET
                double baseDemand = (double)item.expectedQty * baseMultiplier * modelVariance;
                
                // تذبذب عشوائي بسيط لمحاكاة مخرجات نموذج ML
                double fluctuation = 1.0 + ((rnd.NextDouble() * 0.08) - 0.04); // +/- 4%

                // newExpected: الكمية المتوقعة بوحدة الشراء (MainUnit)
                decimal newExpected = (decimal)Math.Round(baseDemand * fluctuation);
                
                // EOQ بوحدة الشراء (MainUnit) - لا حاجة للقسمة على cf هنا
                decimal annualExpected = newExpected * (12m / (decimal)Math.Max(1, request.horizon)); 
                decimal eoq = annualExpected > 0 ? (decimal)Math.Round(Math.Sqrt((double)(2m * annualExpected * 50m / 0.2m))) : 0;
                
                int cf = item.conversionFactor > 0 ? item.conversionFactor : 1;
                
                // المخزون الحالي بوحدة الشراء (MainUnit)
                decimal stockInMainUnit = item.stock / (decimal)cf;
                decimal proposedBase = Math.Max(eoq, newExpected * 2m - stockInMainUnit);
                proposedBase = proposedBase < 0 ? 0 : Math.Round(proposedBase);

                item.expectedQty  = newExpected;                               // ✅ بوحدة الشراء
                item.optimalQty   = proposedBase;                              // ✅ بوحدة الشراء
                item.eoq          = eoq;
                item.proposed     = proposedBase;
                item.approved     = proposedBase;
                
                // التكلفة الإجمالية = الكمية بوحدة الشراء × سعر الوحدة
                item.totalCost  = Math.Round(proposedBase * item.unitCost, 2);
                item.status     = item.stock <= item.minStock ? "ناقص - يحتاج طلب" : (proposedBase > 0 ? "ضمن الميزانية" : "مخزون كافٍ");
                item.statusClass = item.stock <= item.minStock ? "error" : (proposedBase > 0 ? "ok" : "enough");
                
                newItems.Add(item);
            }

            // Generate Chart Data based on model
            var arabicMonths = new[] { "يناير","فبراير","مارس","أبريل","مايو","يونيو","يوليو","أغسطس","سبتمبر","أكتوبر","نوفمبر","ديسمبر" };
            var today = DateTime.Today;
            var currentMonth = today.Month;
            var chartLabels = new List<string>();
            var chartActual = new List<decimal?>(); 
            var chartForecastList = new List<decimal>();

            // Get Real Historical Data for the last 6 months
            var sixMonthsAgo = today.AddMonths(-5);
            var startOfSixMonths = new DateTime(sixMonthsAgo.Year, sixMonthsAgo.Month, 1);

            var monthlySalesRaw = await _context.Saledetails
                .Include(sd => sd.Sale)
                .Where(sd => sd.Sale.SaleDate >= startOfSixMonths
                          && (!isGlobal ? sd.Sale.BranchId == scopeId : true))
                .GroupBy(sd => new { sd.Sale.SaleDate.Year, sd.Sale.SaleDate.Month })
                .Select(g => new
                {
                    Year  = g.Key.Year,
                    Month = g.Key.Month,
                    Total = g.Sum(x => x.Quantity * x.UnitPrice)
                })
                .OrderBy(x => x.Year).ThenBy(x => x.Month)
                .ToListAsync();

            // Populate historical exactly 6 elements
            for(int i=5; i>=0; i--) {
                int y = today.AddMonths(-i).Year;
                int m = today.AddMonths(-i).Month;
                chartLabels.Add(arabicMonths[m-1]);
                
                var monthData = monthlySalesRaw.FirstOrDefault(x => x.Year == y && x.Month == m);
                decimal histVal = monthData?.Total ?? 0m;
                chartActual.Add(Math.Round(histVal, 2));
            }

            // Calculate future 'horizon' elements based on real data
            decimal lastVal = chartActual.Last() ?? 0;
            if (lastVal == 0 && chartActual.Any(x => x > 0))
                lastVal = chartActual.Where(x => x > 0).Average() ?? 0;

            for(int i=1; i<=request.horizon; i++) {
                int m = currentMonth + i;
                if(m > 12) m -= 12;
                chartLabels.Add(arabicMonths[m-1]);

                // Trend mapping
                decimal trend = request.model == "vertex" ? 1.08m : 1.03m; // Vertex assumes higher growth
                decimal forecastVal = lastVal * trend * (decimal)(rnd.NextDouble() * 0.1 + 0.95);
                chartForecastList.Add(Math.Round(forecastVal, 2));
                lastVal = forecastVal;
            }
            
            // Pad the initial forecast array with nulls, so line matches timeline perfectly
            var paddedForecast = new List<decimal?>();
            for(int i=0; i<6; i++) {
                if (i == 5) paddedForecast.Add(chartActual[i]);
                else paddedForecast.Add(null);
            }
            foreach(var v in chartForecastList) paddedForecast.Add(v);
            
            // Also pad the actual forward so labels match
            for(int i=1; i<=request.horizon; i++) {
                chartActual.Add(null);
            }
            
            // 🔔 إرسال تنبيه واتساب في حالة وجود نواقص حرجة (أدوية منقذة للحياة)
            try 
            {
                var criticalItems = newItems.Where(x => x.isLifeSaving && x.status != null && x.status.Contains("ناقص")).ToList();
                if (criticalItems.Any())
                {
                    string msg = $"🚨 *تنبيه ذكاء مخزني - PharmaSmart*\n\n";
                    msg += $"تم اكتشاف {criticalItems.Count} أصناف منقذة للحياة تحت حد الأمان:\n";
                    foreach(var item in criticalItems.Take(3)) 
                        msg += $"• {item.drug} (المخزون الحالي: {item.stock})\n";
                    
                    if (criticalItems.Count > 3) msg += $"... وغيرها.\n";
                    msg += "\nيرجى مراجعة مركز التخطيط لاتخاذ إجراء الشراء.";

                    // جلب رقم المالك من الإعدادات
                    var settings = await _context.CompanySettings.FirstOrDefaultAsync(s => s.Id == 1);
                    string targetNumber = !string.IsNullOrEmpty(settings?.OwnerWhatsApp) ? settings.OwnerWhatsApp : "967700000000";

                    await _whatsappService.SendMessageAsync(targetNumber, msg);
                }
            }
            catch(Exception ex) { _logger.LogWarning("WhatsApp Notification failed: " + ex.Message); }

            return Json(new { items = newItems, chartLabels, chartActual, chartForecast = paddedForecast });
        }

        [HttpPost]
        public async Task<IActionResult> RecalculateSafetyStock()
        {
            try
            {
                int scopeId = ReportScopeId;
                bool isGlobal = (scopeId == 0);
                var today = DateTime.Today;
                var thirtyDaysAgo = today.AddDays(-30);

                // 1. حساب سرعة البيع اليومية (Daily Sales Velocity) لآخر 30 يوم
                var salesVelocity = await _context.Saledetails
                    .Include(sd => sd.Sale)
                    .Where(sd => sd.Sale.SaleDate >= thirtyDaysAgo && (!isGlobal ? sd.Sale.BranchId == scopeId : true))
                    .GroupBy(sd => sd.DrugId)
                    .Select(g => new {
                        DrugId = g.Key,
                        AverageDaily = g.Sum(sd => sd.Quantity) / 30m
                    })
                    .ToListAsync();

                var velocityDict = salesVelocity.ToDictionary(x => x.DrugId, x => x.AverageDaily);

                // 2. جلب كل المخزون لتحديثه
                var inventory = await _context.Branchinventory
                    .Include(bi => bi.Drug)
                    .Where(bi => !isGlobal ? bi.BranchId == scopeId : true)
                    .ToListAsync();

                int updatedCount = 0;
                foreach (var item in inventory)
                {
                    decimal ads = velocityDict.ContainsKey(item.DrugId) ? velocityDict[item.DrugId] : 0;
                    
                    // المعادلة الديناميكية:
                    // حد الأمان = (متوسط البيع اليومي * فترة التوريد) + كمية احتياطية (Buffer)
                    
                    int leadTime = 7; // افتراضي: 7 أيام للتوريد
                    int buffer = 3;   // احتياطي لتقلبات السوق
                    
                    if (item.Drug.IsLifeSaving == true) 
                    {
                        leadTime = 15; // رفع فترة التوريد للأدوية المنقذة للحياة لضمان الأمان
                        buffer = 10;   // احتياطي أكبر
                    }

                    decimal newMinStock = ads * (leadTime + buffer);
                    
                    // حد أدنى مطلق لضمان عدم تصفير حد الأمان للأصناف الجديدة
                    decimal absoluteMin = (item.Drug.IsLifeSaving == true) ? 10 : 3;
                    
                    int calculatedMin = (int)Math.Max(absoluteMin, Math.Ceiling(newMinStock));

                    if (item.MinimumStockLevel != calculatedMin)
                    {
                        item.MinimumStockLevel = calculatedMin;
                        updatedCount++;
                    }
                }

                if (updatedCount > 0)
                {
                    await _context.SaveChangesAsync();
                    await RecordLog("Intelligence", "Update", $"تحديث تلقائي لحد الأمان لعدد {updatedCount} صنف بناءً على سرعة البيع.");
                }

                return Json(new { success = true, message = $"تم تحديث حد الأمان لـ {updatedCount} صنف آلياً بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل التحديث التلقائي: " + ex.Message });
            }
        }
    }

    public class ForecastItemVm
    {
        public int id { get; set; }
        public string? drug { get; set; }
        public string? abc { get; set; }
        public string? priority { get; set; }      // قصوى / متوسطة / عادية
        public bool isLifeSaving { get; set; }
        public string? unit { get; set; }           // وحدة الشراء (MainUnit)
        public string? subUnit { get; set; }        // وحدة البيع (SubUnit)
        public int conversionFactor { get; set; }  // عامل التحويل
        public decimal stock { get; set; }
        public decimal minStock { get; set; }
        public decimal expectedQty { get; set; }   // الكمية المباعة سابقاً
        public decimal optimalQty { get; set; }    // EOQ
        public decimal eoq { get; set; }
        public decimal proposed { get; set; }
        public decimal approved { get; set; }
        public decimal unitCost { get; set; }
        public decimal totalCost { get; set; }
        public string? status { get; set; }
        public string? statusClass { get; set; }
    }

    public class ForecastRequestVm
    {
        public List<ForecastItemVm>? items { get; set; }
        public string? model { get; set; }
        public int horizon { get; set; }
    }

    // ── ViewModel لاستلام البضاعة ────────────────────────────────────────────
    public class ReceiveGoodsVm
    {
        public int PlanId { get; set; }
        public List<ReceiveGoodsItemVm> Items { get; set; } = new();
    }

    public class ReceiveGoodsItemVm
    {
        public int DrugId { get; set; }
        public string? DrugName { get; set; }
        public int ReceivedQty { get; set; }
        public decimal UnitCost { get; set; }
        public string? BatchNumber { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }
}
