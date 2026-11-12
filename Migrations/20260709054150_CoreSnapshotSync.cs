using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class CoreSnapshotSync20260709 : Migration
    {
        // 註：PasswordHistories 表的 DDL 已由 20260707085216_PasswordHistory_L4（自 Core 同步）建立。
        // 本 migration 僅用於收斂 ProjectZ cumulative snapshot（納入 PasswordHistory entity），
        // Up()/Down() 刻意留空以避免 fresh DB 重複建表。詳見 CLAUDE.md「merge 後 snapshot 收斂」。
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
