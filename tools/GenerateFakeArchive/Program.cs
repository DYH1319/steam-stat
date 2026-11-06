using ElectronNet.Features.DemoArchive;

// 用法：GenerateFakeArchive <用户数据目录> [demo|decade]
if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("demo" or "decade")))
{
    Console.Error.WriteLine("Usage: GenerateFakeArchive <user-data-directory> [demo|decade]");
    return 2;
}

var profile = args.Length == 2 && args[1] == "decade" ? FakeArchiveProfile.Decade : FakeArchiveProfile.Demo;
var result = await FakeArchiveGenerator.GenerateAsync(Path.GetFullPath(args[0]), profile);
Console.WriteLine($"Generated {result.Sessions} sessions, {result.FriendRecords} friend records, {result.Achievements} achievements, {result.Games} games in {result.Elapsed.TotalSeconds:0.0}s");
return 0;
