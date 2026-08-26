# SteamKit2 协议研究草稿

这个目录保存的是**研究用草稿**，不参与编译、不参与 CI。

`SteamUserTests.cs` 原本放在 `ElectronNet.Tests/SteamKit2/` 下，但它并不是测试：

- 内容基本是 [SteamKit2 官方 Samples](https://github.com/SteamRE/SteamKit/tree/master/Samples) 的拷贝
- 会**真实连接 Steam 服务器**，并运行 `while (isRunning) manager.RunWaitCallbacks(...)` 循环
- 因此在 CI 中会耗时约 9 分钟、依赖外网、且结果不确定
- 含有 `"xxx"` / `"xxxxxx"` 之类的凭据占位符

保留它是因为里面记录了不少 unified message / 自定义 handler 的调用姿势，作为参考有价值。
真正的自动化测试请写在 `ElectronNet.Tests/` 下，要求：**纯函数、不依赖网络、不依赖本机 Steam 安装**。
