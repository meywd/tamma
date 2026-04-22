using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProviderHealthCircuitBreakerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CircuitOpenUntil",
                table: "provider_health",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FailureWindowStart",
                table: "provider_health",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HalfOpenInProgress",
                table: "provider_health",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CircuitOpenUntil",
                table: "provider_health");

            migrationBuilder.DropColumn(
                name: "FailureWindowStart",
                table: "provider_health");

            migrationBuilder.DropColumn(
                name: "HalfOpenInProgress",
                table: "provider_health");
        }
    }
}
