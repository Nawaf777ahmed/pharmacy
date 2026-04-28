using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PharmaSmartWeb.Models
{
    public partial class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext() { }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        // ==========================================================
        // ≡اأ ╪د┘╪ش╪»╪د┘ê┘ ╪د┘╪ث╪│╪د╪│┘è╪ر
        // ==========================================================
        public virtual DbSet<Stockaudits> Stockaudits { get; set; }
        public virtual DbSet<Stockauditdetails> Stockauditdetails { get; set; }
        public virtual DbSet<Vouchers> Vouchers { get; set; }
        public virtual DbSet<Accounts> Accounts { get; set; }

        public virtual DbSet<Branches> Branches { get; set; }
        public virtual DbSet<Branchinventory> Branchinventory { get; set; }
        public virtual DbSet<Customers> Customers { get; set; }
        public virtual DbSet<Drugs> Drugs { get; set; }
        public virtual DbSet<Drugtransferdetails> Drugtransferdetails { get; set; }
        public virtual DbSet<Drugtransfers> Drugtransfers { get; set; }
        public virtual DbSet<Employees> Employees { get; set; }
        public virtual DbSet<Forecasts> Forecasts { get; set; }
        public virtual DbSet<Fundtransfers> Fundtransfers { get; set; }
        public virtual DbSet<Journaldetails> Journaldetails { get; set; }
        public virtual DbSet<Journalentries> Journalentries { get; set; }
        public virtual DbSet<Purchasedetails> Purchasedetails { get; set; }
        public virtual DbSet<Purchases> Purchases { get; set; }
        public virtual DbSet<Saledetails> Saledetails { get; set; }
        public virtual DbSet<Sales> Sales { get; set; }
        public virtual DbSet<ItemGroups> ItemGroups { get; set; }
        public virtual DbSet<Screenpermissions> Screenpermissions { get; set; }
        public virtual DbSet<Seasonaldata> Seasonaldata { get; set; }
        public virtual DbSet<Stockmovements> Stockmovements { get; set; }
        public virtual DbSet<Suppliers> Suppliers { get; set; }
        public virtual DbSet<Systemscreens> Systemscreens { get; set; }
        public virtual DbSet<Userroles> Userroles { get; set; }
        public virtual DbSet<Users> Users { get; set; }
        public virtual DbSet<UserScreenPermissions> UserScreenPermissions { get; set; }
        public virtual DbSet<Shifts> Shifts { get; set; }
        public virtual DbSet<SystemLogs> Systemlogs { get; set; }
        public virtual DbSet<CompanySettings> CompanySettings { get; set; }
        
        // ==========================================================
        // ≡اؤْ ╪«╪╖╪╖ ╪د┘┘à╪┤╪ز╪▒┘è╪د╪ز ╪د┘╪░┘â┘è╪ر
        // ==========================================================
        public virtual DbSet<PurchasePlan> PurchasePlans { get; set; }
        public virtual DbSet<PurchasePlanDetail> PurchasePlanDetails { get; set; }
        public virtual DbSet<SystemNotification> SystemNotifications { get; set; }

        // ==========================================================
        // ≡اـ ╪د┘╪ش╪»╪د┘ê┘ ╪د┘╪ش╪»┘è╪»╪ر (╪د┘╪╣┘à┘╪د╪ز╪î ╪د┘┘à╪│╪ز┘ê╪»╪╣╪د╪ز╪î ╪د┘╪ذ╪د╪▒┘â┘ê╪»╪î ╪د┘╪»┘╪╣ ╪د┘┘à╪ز╪╣╪»╪»)
        // ==========================================================
        public virtual DbSet<Currencies> Currencies { get; set; }
        public virtual DbSet<Warehouses> Warehouses { get; set; }
        public virtual DbSet<Shelves> Shelves { get; set; }
        public virtual DbSet<DrugBatches> DrugBatches { get; set; }
        public virtual DbSet<BarcodeGenerator> BarcodeGenerator { get; set; }
        public virtual DbSet<SalePayments> SalePayments { get; set; }
        public virtual DbSet<AccountingTemplate> AccountingTemplates { get; set; }
        public virtual DbSet<AccountingTemplateLine> AccountingTemplateLines { get; set; }
        public virtual DbSet<AccountMapping> AccountMappings { get; set; }

        // ==========================================================
        // ≡اؤّ ┘à╪ص╪▒┘â ╪د┘╪ص╪░┘ ╪د┘┘à┘╪╖┘é┘è (Soft Delete Engine)
        // ==========================================================
        public override int SaveChanges()
        {
            ApplySoftDelete();
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            ApplySoftDelete();
            return base.SaveChangesAsync(cancellationToken);
        }

        private void ApplySoftDelete()
        {
            var entries = ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Deleted);

            foreach (var entry in entries)
            {
                var property = entry.Metadata.FindProperty("IsDeleted");
                if (property != null && property.ClrType == typeof(bool?))
                {
                    entry.State = EntityState.Modified; // ┘à┘†╪╣ ╪د┘„╪ص╪░┘  ╪د┘„┘ ╪╣┘„┘è
                    entry.CurrentValues["IsDeleted"] = true;

                    var deletedAtProp = entry.Metadata.FindProperty("DeletedAt");
                    if (deletedAtProp != null)
                    {
                        entry.CurrentValues["DeletedAt"] = DateTime.Now;
                    }
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ==========================================================
            // 1. ضبط دقة الأرقام العشرية للمبالغ المالية
            // ==========================================================
            foreach (var property in modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetProperties())
                .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            {
                property.SetColumnType("decimal(18,4)");
            }

            // ==========================================================
            // 2. حل مشكلة حالة الأحرف (Case Sensitivity) في Linux/Docker
            // ==========================================================
            modelBuilder.Entity<AccountingTemplate>().ToTable("accountingtemplates");
            modelBuilder.Entity<AccountingTemplateLine>().ToTable("accountingtemplatelines");
            modelBuilder.Entity<AccountMapping>().ToTable("accountmappings");
            modelBuilder.Entity<Accounts>().ToTable("accounts");
            modelBuilder.Entity<Branches>().ToTable("branches");
            modelBuilder.Entity<Branchinventory>().ToTable("branchinventory");
            modelBuilder.Entity<Customers>().ToTable("customers");
            modelBuilder.Entity<Drugs>().ToTable("drugs");
            modelBuilder.Entity<DrugBatches>().ToTable("drug_batches");
            modelBuilder.Entity<Drugtransfers>().ToTable("drugtransfers");
            modelBuilder.Entity<Drugtransferdetails>().ToTable("drugtransferdetails");
            modelBuilder.Entity<Employees>().ToTable("employees");
            modelBuilder.Entity<Forecasts>().ToTable("forecasts");
            modelBuilder.Entity<Fundtransfers>().ToTable("fundtransfers");
            modelBuilder.Entity<Journalentries>().ToTable("journalentries");
            modelBuilder.Entity<Journaldetails>().ToTable("journaldetails");
            modelBuilder.Entity<Purchases>().ToTable("purchases");
            modelBuilder.Entity<Purchasedetails>().ToTable("purchasedetails");
            modelBuilder.Entity<Sales>().ToTable("sales");
            modelBuilder.Entity<Saledetails>().ToTable("saledetails");
            modelBuilder.Entity<SalePayments>().ToTable("sale_payments");
            modelBuilder.Entity<ItemGroups>().ToTable("itemgroups");
            modelBuilder.Entity<Screenpermissions>().ToTable("screenpermissions");
            modelBuilder.Entity<Seasonaldata>().ToTable("seasonaldata");
            modelBuilder.Entity<Stockmovements>().ToTable("stockmovements");
            modelBuilder.Entity<Stockaudits>().ToTable("stockaudits");
            modelBuilder.Entity<Stockauditdetails>().ToTable("stockauditdetails");
            modelBuilder.Entity<Suppliers>().ToTable("suppliers");
            modelBuilder.Entity<Systemscreens>().ToTable("systemscreens");
            modelBuilder.Entity<Userroles>().ToTable("userroles");
            modelBuilder.Entity<Users>().ToTable("users");
            modelBuilder.Entity<Shifts>().ToTable("shifts");
            modelBuilder.Entity<SystemLogs>().ToTable("systemlogs");
            modelBuilder.Entity<CompanySettings>().ToTable("companysettings");
            modelBuilder.Entity<PurchasePlan>().ToTable("purchaseplans");
            modelBuilder.Entity<PurchasePlanDetail>().ToTable("purchaseplandetails");
            modelBuilder.Entity<SystemNotification>().ToTable("systemnotifications");
            modelBuilder.Entity<Currencies>().ToTable("currencies");
            modelBuilder.Entity<Warehouses>().ToTable("warehouses");
            modelBuilder.Entity<Shelves>().ToTable("shelves");
            modelBuilder.Entity<BarcodeGenerator>().ToTable("barcodegenerator");

            // ==========================================================
            // 3. فلاتر الحذف المنطقي (Soft Delete)
            // ==========================================================
            modelBuilder.Entity<Drugs>().Property<bool?>("IsDeleted").HasColumnType("tinyint(1)");
            modelBuilder.Entity<Drugs>().HasQueryFilter(e => EF.Property<bool?>(e, "IsDeleted") != true);

            modelBuilder.Entity<Accounts>().Property<bool?>("IsDeleted").HasColumnType("tinyint(1)");
            modelBuilder.Entity<Accounts>().HasQueryFilter(e => EF.Property<bool?>(e, "IsDeleted") != true);

            modelBuilder.Entity<Sales>().Property<bool?>("IsDeleted").HasColumnType("tinyint(1)");
            modelBuilder.Entity<Sales>().Property<DateTime?>("DeletedAt");
            modelBuilder.Entity<Sales>().HasQueryFilter(e => EF.Property<bool?>(e, "IsDeleted") != true);

            modelBuilder.Entity<Purchases>().Property<bool?>("IsDeleted").HasColumnType("tinyint(1)");
            modelBuilder.Entity<Purchases>().Property<DateTime?>("DeletedAt");
            modelBuilder.Entity<Purchases>().HasQueryFilter(e => EF.Property<bool?>(e, "IsDeleted") != true);

            modelBuilder.Entity<Journalentries>().Property<bool?>("IsDeleted").HasColumnType("tinyint(1)");
            modelBuilder.Entity<Journalentries>().Property<DateTime?>("DeletedAt");
            modelBuilder.Entity<Journalentries>().HasQueryFilter(e => EF.Property<bool?>(e, "IsDeleted") != true);

            // ==========================================================
            // 4. إعدادات العلاقات (Fluent API)
            // ==========================================================
            modelBuilder.Entity<Sales>(entity =>
            {
                entity.HasKey(e => e.SaleId).HasName("PRIMARY");
                entity.HasOne(d => d.Branch).WithMany(p => p.Sales).HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.ClientSetNull);
                entity.HasOne(d => d.Customer).WithMany(p => p.Sales).HasForeignKey(d => d.CustomerId);
                entity.HasOne(d => d.User).WithMany(p => p.Sales).HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.ClientSetNull);
            });

            modelBuilder.Entity<Accounts>(entity =>
            {
                entity.HasKey(e => e.AccountId).HasName("PRIMARY");
                entity.HasIndex(e => e.AccountCode).HasDatabaseName("AccountCode").IsUnique();
                entity.Property(e => e.IsActive).HasColumnType("tinyint(1)").HasDefaultValue(true);
                entity.Property(e => e.AccountNature).HasColumnType("tinyint(1)");
                entity.HasOne(d => d.ParentAccount).WithMany(p => p.SubAccounts).HasForeignKey(d => d.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(d => d.Branch).WithMany(p => p.Accounts).HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Branches>(entity =>
            {
                entity.HasKey(e => e.BranchId).HasName("PRIMARY");
                entity.HasIndex(e => e.BranchCode).HasDatabaseName("BranchCode").IsUnique();
                entity.Property(e => e.IsActive).HasColumnType("tinyint(1)").HasDefaultValue(true);
                entity.HasOne(d => d.DefaultCashAccount).WithMany("CashBranches").HasForeignKey(d => d.DefaultCashAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(d => d.DefaultSalesAccount).WithMany("SalesBranches").HasForeignKey(d => d.DefaultSalesAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(d => d.DefaultCOGSAccount).WithMany("CogsBranches").HasForeignKey(d => d.DefaultCOGSAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(d => d.DefaultInventoryAccount).WithMany("InventoryBranches").HasForeignKey(d => d.DefaultInventoryAccountId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Branchinventory>(entity =>
            {
                entity.HasKey(e => new { e.BranchId, e.DrugId }).HasName("PRIMARY");
                entity.Property(e => e.Abccategory).HasDefaultValueSql("'C'");
                entity.HasOne(d => d.Branch).WithMany(p => p.Branchinventory).HasForeignKey(d => d.BranchId);
                entity.HasOne(d => d.Drug).WithMany(p => p.Branchinventory).HasForeignKey(d => d.DrugId);
            });

            modelBuilder.Entity<Customers>(entity =>
            {
                entity.HasKey(e => e.CustomerId).HasName("PRIMARY");
                entity.Property(e => e.IsActive).HasColumnType("tinyint(1)").HasDefaultValue(true);
                entity.HasOne(d => d.Account).WithMany(p => p.Customers).HasForeignKey(d => d.AccountId).OnDelete(DeleteBehavior.ClientSetNull);
            });

            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
