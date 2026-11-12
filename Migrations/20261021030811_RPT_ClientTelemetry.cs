using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class RPT_ClientTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientTelemetryEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlantId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Page = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    MaxMs = table.Column<int>(type: "integer", nullable: true),
                    Under1s = table.Column<int>(type: "integer", nullable: true),
                    Under3s = table.Column<int>(type: "integer", nullable: true),
                    Under10s = table.Column<int>(type: "integer", nullable: true),
                    Over10s = table.Column<int>(type: "integer", nullable: true),
                    Failures = table.Column<int>(type: "integer", nullable: true),
                    QueuePending = table.Column<int>(type: "integer", nullable: true),
                    QueueOldestMinutes = table.Column<int>(type: "integer", nullable: true),
                    NetType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    RttMs = table.Column<int>(type: "integer", nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientTelemetryEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientTelemetryEvents_DeviceId_ReceivedAt",
                table: "ClientTelemetryEvents",
                columns: new[] { "DeviceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientTelemetryEvents_PlantId_ReceivedAt",
                table: "ClientTelemetryEvents",
                columns: new[] { "PlantId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientTelemetryEvents");
        }
    }
}
