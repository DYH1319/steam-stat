using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class DeduplicateFriendStatusRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 只清数据，不改表结构：删掉值和上一条相同的好友状态记录（会话重连时重复写入的）
            migrationBuilder.Sql(ElectronNet.Features.Friends.Persistence.FriendStatusRecordCleanup.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 删掉的记录不能恢复（迁移前的数据库备份里有）

        }
    }
}
