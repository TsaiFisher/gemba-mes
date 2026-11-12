using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartFactory.MES.Data
{
    /// <summary>
    /// Npgsql 10 已不提供 UseXminAsConcurrencyToken()；以同名擴充方法還原原本的對應，
    /// 產生的模型與既有 ModelSnapshot 完全相同（uint 影子屬性 xmin、xid、IsConcurrencyToken），不會生出 migration。
    /// </summary>
    public static class XminConcurrencyExtensions
    {
        public static EntityTypeBuilder<TEntity> UseXminAsConcurrencyToken<TEntity>(this EntityTypeBuilder<TEntity> builder)
            where TEntity : class
        {
            builder.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            return builder;
        }
    }
}
