using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class VerifyAfterEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingName",
                table: "AccountActions");

            migrationBuilder.DropColumn(
                name: "PendingPasswordHash",
                table: "AccountActions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingName",
                table: "AccountActions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PendingPasswordHash",
                table: "AccountActions",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
