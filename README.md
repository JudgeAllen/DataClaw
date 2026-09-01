# TwsHistory

从 TWS / IB Gateway 拉取历史 K 线(bar)并导出 CSV 的小工具,基于本仓库 `client/` 下的官方 IB C# API。
提供**图形界面**(推荐)和**命令行**两种用法。

## 图形界面 (TwsHistoryGui)

```powershell
dotnet build .\HistoryTool\TwsHistoryGui\TwsHistoryGui.csproj -c Release
# 运行:
.\HistoryTool\TwsHistoryGui\bin\Release\net9.0-windows\TwsHistoryGui.exe
```

两个标签页:

- **拉取数据**:填合约(支持股票/期权/期货/外汇/指数预设)、时长、K 线大小、
  数据类型、时间区间等,点"开始拉取";实时日志输出;"中止"可随时取消。
  连接参数(host/port/clientId)、数据目录、时区会记住(保存在 exe 旁的 `settings.json`)。
- **数据管理**:管理数据目录下的 CSV——列表显示文件名/大小/K 线数/区间,
  选中即可预览表格 + 收盘价走势图(附成交量);支持打开、删除、重命名、
  打开所在文件夹。默认数据目录:`我的文档\TwsHistoryData`。

自检模式(供验证,无需打开窗口交互):

```powershell
TwsHistoryGui.exe --selftest <数据目录>   # 真实拉取 1 天 AAPL 日线并刷新列表,退出码 0 = 成功
TwsHistoryGui.exe --dump-ui               # 输出控件布局树(诊断用)
```

## 命令行 (TwsHistory)

```powershell
dotnet build .\HistoryTool\HistoryTool.csproj -c Release
# 产物: HistoryTool\bin\Release\net9.0\TwsHistory.exe

.\TwsHistory.exe --symbol AAPL                          # 默认:1 年日线
.\TwsHistory.exe --symbol AAPL --duration "30 D"        # 30 天日线
.\TwsHistory.exe --symbol AAPL --bar-size "1 min" --duration "1 D"   # 1 分钟线(自动分页)
.\TwsHistory.exe --symbol ES --sectype FUT --exchange GLOBEX --last-trade-date 202609 --bar-size "1 day"
```

运行 `TwsHistory.exe --help` 查看全部参数。

# 期货(ES/NQ)示例

```powershell
# ES 9月合约 5分钟线;GLOBEX 解析失败会自动改用 CME 重试
.\TwsHistory.exe --symbol ES --sectype FUT --exchange GLOBEX --currency USD --last-trade-date 202609 --duration "1 D" --bar-size "5 mins" --client-id 99

# 直接指定中国区路由认识的交易所名
.\TwsHistory.exe --symbol ES --sectype FUT --exchange CME --currency USD --last-trade-date 202609 --duration "1 D" --bar-size "5 mins" --client-id 99

# 港股期货(HSI)
.\TwsHistory.exe --symbol HSI --sectype FUT --exchange HKFE --currency HKD --last-trade-date 202609 --duration "1 D" --bar-size "5 mins" --client-id 99
```

> 注:中国区路由的 TWS 把 E-mini 系列期货注册在 `CME` 而非 `GLOBEX` 下,工具在
> 遇到 "No security definition" 时会自动尝试 GLOBEX↔CME、NYMEX↔COMEX。

# Ticks 模式

历史 tick(逐笔成交)与 K 线是不同接口(`reqHistoricalTicks`,每次最多 1000 条),
用 `--bar-size "ticks"` 或 `--ticks` 进入 tick 模式,必须给 `--start`,时间按
TWS 登录时区解释,自动向前分页、按 (时间,价格,数量) 去重:

```powershell
# NQ 9月合约 最近 3 小时逐笔成交
.\TwsHistory.exe --symbol NQ --sectype FUT --exchange CME --currency USD --last-trade-date 202609 `
  --local-symbol NQU6 --trading-class NQ --bar-size "ticks" --start "20260901 08:00:00" --client-id 99
```

输出列:`time,price,size`(时间为 TWS 登录时区墙上时间)。

> ⚠️ 规模警告:tick 数据量巨大(实测 NQ 夜间约 1000 条/7~40 分钟,活跃时段更密),
> 且受 60 请求/10 分钟/客户端的频率限制。**只适合小时级到 1~2 天的短区间**;
> 长区间请用 K 线模式。150 天的逐笔数据需要上万次请求,不现实。

## 说明

- **连接**:默认 `127.0.0.1:7497`(TWS 模拟盘)。TWS 实盘 7496、Gateway 实盘
  4001 / 模拟盘 4002,用 `--port` 指定。TWS 里需在
  File > Global Configuration > API > Settings 勾选 "Enable ActiveX and Socket Clients"。
- **clientId**:默认 0。如果 0 被其他 API 程序占用,TWS 会直接掐断新连接(表现为
  连接后立刻被断开),换 `--client-id 99` 之类的值即可。
- **分页**:TWS 单次请求能返回的数据量有限(新版约 2.8 万根 bar,超出会报 165 或
  挂起)。工具自动按最早一根 bar 的 UTC 时刻往前翻页(与登录时区无关)、按时间戳
  去重;请求过大时自动缩减时长重试。`--max-pages` 是安全上限。
- **时间范围**:勾选"指定开始/结束时间"后,结果会被**严格裁剪到该区间内**
  (即使时长填得很大,也只会多拉再裁掉,日志会提示 Trimmed N bars)。开始/结束
  时间与 CSV 时间戳一样,都是 **TWS 登录时区的墙上时间**(与 TWS 图表一致)。
  只勾选开始时间时,结束 = 现在。默认时长已改为 `1 D`。
- **时区**:CSV 时间戳是 TWS 登录时区的墙上时间(`yyyy-MM-dd HH:mm:ss`,与 TWS
  图表一致)。注意新版 TWS 会在日内 bar 的时间戳上附加登录时区后缀
  (如 `20260814 16:00:00 Asia/Shanghai`),工具会自动处理。
- **延迟数据**:没有实时行情订阅的账户,实时模式下会拉不到数据。工具会:
  ① 首次请求为空时自动切延迟模式重试;② 也可用 `--delayed 1` 或 GUI 中
  直接强制延迟。延迟账户的数据会比实时慢约 15 分钟,历史部分不受影响。
- **空 bar**:TWS 对某些请求会返回 OHLC 全为 -1 的空 bar,工具会跳过并计数提示。
- **数据权限**:历史数据(尤其日内)需要账户有相应数据订阅。权限不足时可能
  静默返回 0 根 bar,或报 162/200 等错误(错误会打印并中止)。用诊断探针自查:

```powershell
.\TwsHistory.exe --symbol AAPL --probe-mkt --client-id 99    # 实时行情类型/是否有 tick
.\TwsHistory.exe --symbol AAPL --probe-mkt-d --client-id 99  # 延迟行情(无实时订阅时用)
.\TwsHistory.exe --symbol AAPL --probe-ticks --client-id 99  # 历史 tick
.\TwsHistory.exe --symbol AAPL --probe-head --client-id 99   # 最早可用数据时间
```

- CSV 列:`time,open,high,low,close,volume,wap,count`。

## 目录结构

```
HistoryTool/
  TwsHistory.Core/          # 共享库:IB API 源码 + 拉取/分页/CSV 逻辑
    HistoryFetcher.cs       #   连接、分页拉取(CLI 与 GUI 共用)
    TwsWrapper.cs           #   EWrapper 实现
    CsvExporter.cs          #   CSV 写入
    ProtobufCompatShim.cs   #   旧版 protobuf 兼容 shim
  HistoryTool.csproj        # 命令行工具 (TwsHistory.exe)
  TwsHistoryGui/            # 图形界面 (TwsHistoryGui.exe)
    MainForm.cs             #   主窗体:拉取页 + 数据管理页
    CsvFile.cs              #   CSV 读取/预览
    AppSettings.cs          #   设置持久化
```

> 离线构建说明:本机无法访问 nuget.org,而 IB API 工程要求的 Google.Protobuf
> 3.29.5 不在本地缓存中,因此 `TwsHistory.Core` 直接把 `client/` 的源码编译进库
> (不改动 vendor 代码),并引用本地缓存里已有的 Google.Protobuf 3.15.8。
> `ProtobufCompatShim.cs` 为 protoc 3.29 生成代码缺失的 `MapField.MergeFrom`
> 提供了等价实现。有网络时可改回 ProjectReference。
> 构建时的 NU1900 警告是"无法联网查漏洞数据",可忽略。
