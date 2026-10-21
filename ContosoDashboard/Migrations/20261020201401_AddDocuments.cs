using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContosoDashboard.Migrations
{
    /// <inheritdoc />
    public partial class AddDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentActivities",
                columns: table => new
                {
                    DocumentActivityId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentTitle = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentActivities", x => x.DocumentActivityId);
                    table.ForeignKey(
                        name: "FK_DocumentActivities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UploaderUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScanAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScanRequestedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanCompletedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScanDetail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_Documents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Documents_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Documents_Users_UploaderUserId",
                        column: x => x.UploaderUserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentShares",
                columns: table => new
                {
                    DocumentShareId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedWithUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    SharedWithDepartment = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SharedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentShares", x => x.DocumentShareId);
                    table.CheckConstraint("CK_DocumentShare_ExactlyOneTarget", "(SharedWithUserId IS NOT NULL AND SharedWithDepartment IS NULL) OR (SharedWithUserId IS NULL AND SharedWithDepartment IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DocumentShares_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentShares_Users_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentShares_Users_SharedWithUserId",
                        column: x => x.SharedWithUserId,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_Action_Timestamp",
                table: "DocumentActivities",
                columns: new[] { "Action", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_DocumentId",
                table: "DocumentActivities",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_UserId",
                table: "DocumentActivities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Category",
                table: "Documents",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_FilePath",
                table: "Documents",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ProjectId",
                table: "Documents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ScanStatus_ScanRequestedDate",
                table: "Documents",
                columns: new[] { "ScanStatus", "ScanRequestedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TaskId",
                table: "Documents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UploadDate",
                table: "Documents",
                column: "UploadDate");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UploaderUserId",
                table: "Documents",
                column: "UploaderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId_SharedWithDepartment",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "SharedWithDepartment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId_SharedWithUserId",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "SharedWithUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_SharedByUserId",
                table: "DocumentShares",
                column: "SharedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_SharedWithDepartment",
                table: "DocumentShares",
                column: "SharedWithDepartment");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_SharedWithUserId",
                table: "DocumentShares",
                column: "SharedWithUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentActivities");

            migrationBuilder.DropTable(
                name: "DocumentShares");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}
