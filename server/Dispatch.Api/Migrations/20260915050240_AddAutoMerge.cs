using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_merge",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_merge",
                table: "tickets");
        }
    }
}
