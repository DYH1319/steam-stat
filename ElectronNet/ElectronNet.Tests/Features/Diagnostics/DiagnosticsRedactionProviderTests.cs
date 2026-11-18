using ElectronNet.Features.Diagnostics;
using ElectronNet.Models;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace ElectronNet.Tests.Features.Diagnostics;

public sealed class DiagnosticsRedactionProviderTests : InsightHostFixture
{
    [Test]
    public async Task FriendAndFamilyNamesAreRedacted()
    {
        await using (var db = DbFactory.CreateDbContext())
        {
            db.FriendStatusRecordTable.Add(new FriendStatusRecord { AccountName = "me", FriendSteamId = "76561198000000009", FriendPersonaName = "Gordon", ChangeType = "persona", Timestamp = 1 });
            db.FamilyMemberTable.Add(new FamilyMemberRecord { FamilyGroupId = "1", SteamId = "76561198000000008", Name = "Alyx" });
            await db.SaveChangesAsync();
        }
        var provider = new DiagnosticsRedactionProvider(DbFactory, NullLogger<DiagnosticsRedactionProvider>.Instance);

        var redactor = await provider.RefreshAsync();

        redactor.Redact("Gordon joined Alyx").Should().Be("[persona] joined [persona]");
    }
}
