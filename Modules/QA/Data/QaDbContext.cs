using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.QA.Models;

namespace SmartFactory.MES.Modules.QA.Data;

/// <summary>
/// 手冊 Q&amp;A 的獨立資料庫上下文（規格 §17.3）。
///
/// <para><b>為什麼一定要拆，而不是併進 <c>ApplicationDbContext</c>：</b>
/// EF 的 migration 粒度是 DbContext，不是 bundle——<c>Program.cs</c> 的
/// <c>db.Database.Migrate()</c> 會套用該 context 的**全部** pending migration，不看任何 Features 開關。
/// 把選用功能的表放進主 context，等於每一個部署都得建這張表；
/// 而這張表會隨手冊重建大量寫入與刪除，生命週期與業務資料完全不同。</para>
///
/// <para>連線字串預設沿用主連線（同一個資料庫、不同的 migration 歷史表
/// <c>__EFMigrationsHistory_Qa</c>），可由 <c>Features:QaAssistant:ConnectionString</c> 指向另一台。</para>
///
/// <para><b>本 context 沒有任何攔截器</b>（無 <c>AuditInterceptor</c>／<c>PasswordHistoryInterceptor</c>）：
/// 索引重建一次寫入數百列，稽核那些寫入沒有意義，而且會把稽核表灌滿手冊內容。</para>
/// </summary>
public class QaDbContext : DbContext
{
    /// <summary>此 context 專屬的 migration 歷史表。與主 context 的 <c>__EFMigrationsHistory</c> 分離。</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_Qa";

    public QaDbContext(DbContextOptions<QaDbContext> options) : base(options) { }

    public DbSet<DocChunk> DocChunks => Set<DocChunk>();

    /// <summary>自然語言查詢的追問狀態與稽核（§11.3）。<b>不是向量庫</b>，見 <see cref="QaTurn"/>。</summary>
    public DbSet<QaTurn> QaTurns => Set<QaTurn>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DocChunk>(e =>
        {
            e.ToTable("DocChunks");

            // 同一頁、同一語系、同一序號只會有一列。這個唯一鍵是增量重建的正確性依據：
            // 重建時先刪該檔全部列再插入，中途失敗會由此擋下重複列。
            e.HasIndex(x => new { x.FilePath, x.Lang, x.ChunkIndex }).IsUnique();

            // 檢索一律先以 Lang 硬過濾（§10.4），再加 MenuKey 權限過濾（§10.10）。
            e.HasIndex(x => new { x.Lang, x.MenuKey });

            // 每則提問的第一道條件已變成 (Corpus, Lang)：權限規則依 Corpus 分流（§10.11.7）。
            e.HasIndex(x => new { x.Corpus, x.Lang });

            // 預設值寫在資料庫端而不只在 C# 端：擷取物的匯入路徑會走 raw SQL／COPY
            // （建置期產索引，§10.11.6），那條路徑不經過 EF 的實體預設值。
            e.Property(x => x.Corpus).HasDefaultValue(Contracts.DocCorpus.Manual);
            e.Property(x => x.Approved).HasDefaultValue(false);
            e.Property(x => x.IsStale).HasDefaultValue(false);

            // 增量重建時以 FilePath 比對 ContentHash。
            e.HasIndex(x => x.ContentHash);

            // 向量存 real[]（Npgsql 原生），不是 pgvector 的 vector 型別——
            // 563 列全載進記憶體算精確餘弦是微秒等級，ANN 索引買不到東西（§10.6）。
            // 同理不對 EmbeddingModel 建索引：全表掃描比維護索引便宜。
            e.Property(x => x.Embedding).HasColumnType("real[]");
        });

        builder.Entity<QaTurn>(e =>
        {
            e.ToTable("QaTurns");

            // 唯一的讀取路徑是「某人的某一輪，且尚未定局」（§11.3）。
            // UserId 排在前面是因為它永遠是等值條件，Id 由呼叫端帶入。
            e.HasIndex(x => new { x.UserId, x.Id });

            // 保留期修剪（DocIndexBackgroundService 啟動時執行）掃的是這一欄。
            e.HasIndex(x => x.CreatedAt);

            // 稽核用途要看得出同一次釐清對話的兩輪，故 ThreadId 也建索引。
            e.HasIndex(x => x.ThreadId);
        });

        // 檢索結果的投影。ToView(null)：它沒有對應的資料表或檢視，只由 FromSql 填充，
        // 不加這一句 EF 會把它當成需要建表的實體而生出一張憑空的 migration DDL。
        builder.Entity<DocChunkHit>().HasNoKey().ToView(null);
        builder.Entity<DocChunkVector>().HasNoKey().ToView(null);

        // 刻意不建 tsvector 的 GIN 索引：全表 563 列（§10.6 實測），
        // 循序掃描是微秒等級，GIN 只會多一份要維護的東西。
        // 語料規模由手冊頁數決定且業務資料絕不進入（§8.7），所以它不會長大。
    }
}
