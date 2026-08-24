# 遗留代码：Node.js 版 Steam 接入实验

本目录原先位于仓库根目录的 `electron/`，是项目早期尝试用 Node.js（`steam-session` + protobuf）
接入 Steam 时留下的实验代码。

后端改用 .NET + SteamKit2 之后，这些文件：

- 没有被任何代码引用（不在 `tsconfig.app.json` 的 `include` 中，`src/` 里也没有 import）
- 不参与构建、不参与类型检查、不参与打包

保留而非删除，是因为 `steam/test/protobuf/proto/` 下的 `.proto` 定义在研究 Steam 协议时仍有参考价值。

> 现在的 Steam 接入实现在 `ElectronNet/ElectronNet/Services/` 下，基于 SteamKit2。
