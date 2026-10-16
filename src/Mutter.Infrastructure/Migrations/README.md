# 冻结的迁移目录

这个目录里只有一份很旧的基线，运行时并不用它。**别在这里 `dotnet ef migrations add`**，
原因写在下面。

## 基线缺了什么

`20260730084209_InitialCreate` 是 2026-07-30 生成的，之后没再生成过任何迁移。它只建
4 张表：`AiConfigs`、`ChatMessages`、`GroupConfigs`、`GroupInfos`。而 `AppDbContext`
现在的模型已经有十几个实体，下面这些表都不在基线里：

```
PrivateChatConfigs     KnownGroups        KnownUsers
ActiveMessageQuotas    CustomCharacters   ProactiveChatConfigs
ProactiveChatLogs
```

（`preview-features` 上的群记忆合入后，还会多一个 `GroupMemories`。）

基线里已经有那 4 张表，内部也对不上：

- `GroupConfigs.Keywords` 已改名为 `BotMentionOpenId`；
- `ChatMessages.MessageId`、`ChatMessages.UserId`、`ChatMessages.GroupId`、
  `GroupConfigs.GroupId`、`GroupInfos.GroupId` 这 5 个键列，都从 `INTEGER` 变成了 `TEXT`
  —— 群号从纯数字换成 openid 那轮改的；
- 另外还多了一批普通列，比如 `ChatMessages.IsProactive`、`ChatMessages.PeerUserId`、
  `GroupConfigs.AiProactiveEnabled`、`AiConfigs.WebSearchEnabled`。

## 运行时实际怎么建表

新库由 `Database.EnsureCreatedAsync()` 按当前模型一次建好，之后的加表、加列由
`AppDbContext.EnsureSchemaUpgradedAsync()` 用幂等 SQL 补齐，在已有库上可以反复执行。
修仙那几张表（`GroupWorlds`、`PlayerProfiles`、`PlayerInventories`、`GameCooldowns`、
`CultivationEventLogs`）只由原生 SQL 维护，EF 模型里连对应实体都没有。

全仓搜不到 `Database.Migrate` / `MigrateAsync` 的调用，仓库里现有的几个 `.db` 里也查不到
`__EFMigrationsHistory` 表。迁移从来没被执行过，这份基线是死代码。

## 为什么不能直接 add

`dotnet ef migrations add Xxx` 会拿当前模型减去这份 7 月的快照，把两个多月积压的改动
打成一个迁移，里面混着列改名和 5 处主键列的 `INTEGER → TEXT` 重建。SQLite 的
`AlterColumn` 是靠重建整张表实现的，这份迁移落到已有库上，轻则撞
`duplicate column name` 半路失败，重则按错误的类型重建核心表。

## 要改表结构怎么办

加表、加列、改列一律去改 `AppDbContext.EnsureSchemaUpgradedAsync()`，写成能在已有库上
重复执行的幂等 SQL，这是本项目一贯的做法（CONTRIBUTING 1.6 有记）。

真想改用 EF 迁移，得先删掉这份基线重新生成，再把启动路径从 `EnsureCreatedAsync` 换成
`Migrate()`，同时给已经在跑的部署准备收编方案。这是一件独立的事，不要顺手做。
