# ADR-042: 性能极限根因澄清与优化优先级（2026-08-13）
- 日期: 2026-08-13 | 状态: 讨论中（等用户确认现场规模/丢数容忍/硬件后再定实施项）
- 背景: 用户问「注册多少设备/点位才能测出项目极限」→ 全链路复核后纠正 ADR-032 两处结论，并给出优化优先级；纯 review 无代码改动，未跑测试
- 关联: ADR-032（性能瓶颈扫描）、ADR-018（Channel DropOldest）、ADR-019（S7 逐点读）

## 极限模型（先于所有优化理解）
- 每台设备每轮（1s）产 1 批 = 该设备全部点位；MeasurementWriteHost 单消费者串行写 SQLite（每批独立连接 + 单事务）
- 极限交叉线: 写入吞吐（由每批点数决定）< 每秒批数（由设备数决定）→ Channel 满 1000 批 DropOldest 丢数
- 实测估: 50 点/台可持续约 200~300 台；500 点/台仅 12~30 台（点位数与设备数耦合，无固定数字）

## 对 ADR-032 的两处结论纠正
- 转发缓冲异步化此前被列为性价比最高 → 改为：异步化只解耦、不提上限（转发缓冲写与时序写共用同一把 SQLite 写锁）
- 新增风险: SinkDispatcher 事件通道与遥测同为 DropOldest（AlarmHostedService 走 IPointStoredSink）→ 超载丢告警 = 漏报，工业场景不可接受

## 优化优先级（性价比从高到低）
1. S7 批量读（最便宜、最确定）: S7Driver.cs ReadBatchAsync 逐点 await ReadAsync（每点一次 TCP 往返）→ 按 DB 区连续块 Read(byte[])，50 点 50 次往返→几次；同步修正能力声明（ADR-019 P3-4）
2. 事件通道分离/改策略（最高风险、最该早改）: SinkDispatcher.cs Channel(1000, DropOldest) → 告警通道独立，或改 Backpressure/弃最旧策略，杜绝漏报
3. 写入端批量合并（真想提设备数上限才做）: MeasurementWriteHost + SqliteMeasurementStore.WriteAsync 每批一事务 → 攒批合并（引入攒批延迟，改动中等）
4. 转发缓冲异步化（解耦、读更稳，不提上限）: DataDispatcher.cs DispatchAsync await _buffer.EnqueueAsync 压在采集线程 → 改 Channel 异步消费
5. MaxConcurrency 调大（暂不做）: CollectionOption.cs 默认 5；不配合减少每路 DB 写，调大只会加重 SQLite 写锁争抢

## 待用户确认（决定实施哪些）
- 现场规模: 目标多少台设备、多少点/台（200 台内建议不动架构）
- 丢数容忍: 遥测可丢、告警不可丢？认可则事件通道必改
- 硬件: 树莓派/工控机/虚拟机，直接决定写吞吐基准
