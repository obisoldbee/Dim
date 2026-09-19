# OB Dim 网络快照契约 v1（冻结）

- 状态：**已冻结**（2026-09-20，N0 阶段裁决）。
- 上游输入：研究包 `docs/research/20260920/Usage-Butler-Network-UI-v1 (1)/Usage-Butler-Network-UI/contracts/network.d.ts`（拟议 v1，未获原生验证）。本文档是其冻结后继，差异决议见 §12。
- 机器可校验 Schema：同目录 `network-snapshot.v1.schema.json`（JSON Schema draft 2020-12）。
- 脱敏 fixtures：`tests/ScreenTimeoutToggle.Tests/Fixtures/Network/*.json`（清单见 §14、§15）。
- 可执行校验器：`tools/NetworkProbe` 的 `validate` 模式（实现 §11 全部语义规则）。
- 约束来源：PRD §5（`docs/specs/2026-09-20-obdim-prd.md`）与实施计划 §5 N0（`docs/plans/2026-09-20-obdim-implementation-plan.md`）。本文档与 PRD 冲突时以 PRD 为准并回写修订。

## 1. 顶层结构 NetworkSnapshot

| 字段 | 类型 | 可空 | 说明 |
|---|---|---|---|
| schemaVersion | integer const 1 | 否 | 契约版本。路径 `/v1/` 与字段值必须一致。 |
| origin | enum `host` / `fixture` | 否 | 生产构建只接受 `host`；无宿主不回退 fixture。 |
| asOf | integer (unix ms) | 否 | 本快照的采样截止时刻（UTC Unix 毫秒）。 |
| captureStartedAt | integer (unix ms) | 否 | 当前采集会话（当前 snapshot epoch）的开始时刻。 |
| epoch | integer ≥ 0 | 否 | 采集会话 epoch，见 §3。 |
| sequence | integer ≥ 0 | 否 | 当前 epoch 内单调递增序号，从 0 开始。 |
| coverage | enum 六态 | 否 | 覆盖状态，见 §4。 |
| coverageReasons | string enum 数组 | 否 | 覆盖原因码；`active` 时为空数组，其余至少一条，见 §4。 |
| capabilities | object | 否 | 能力四布尔，见 §5。 |
| droppedEvents | integer ≥ 0 | **可空** | 当前 epoch 内因聚合/缓冲不足丢弃的原始事件数；未知为 null，绝不编码为 0。 |
| truncated | boolean | 否 | 任一列表/历史因 §9 上限被裁切时为 true。 |
| apps | AppObservation 数组 | 否 | 应用口径观测，上限 2000 条。 |
| interfaces | InterfaceObservation 数组 | 否 | 接口口径观测，上限 128 条。 |

语义规则（Schema 无法表达部分）集中在 §11，编号 S1–S8；校验器必须全部实现。

## 2. 时间与单位

- 所有时间戳为 UTC Unix 毫秒整数。
- 所有计数为**十进制字节**；速率为**字节/秒（B/s）**。UI 格式化使用十进制 B/KB/MB/GB（1000 进制），不与内存页的 1024 约定混用，不与 bit/s 混用。
- 速率必须用真实经过时间计算：`Δbytes / Δt`。跨 epoch 不得做差（§3）。

## 3. 版本化语义：schemaVersion / origin / sequence / epoch

### 3.1 schemaVersion 与 origin
- `schemaVersion` 恒为 1；不兼容变更新增 `v2` 目录与 schemaVersion=2，v1 至少并行保留 6 个月。
- `origin=host` 为唯一生产来源。`fixture` 仅出现在 `tests/` 与演示模式；生产构建拒绝 fixture 快照，且**禁止**在无宿主/无权限/未采集时回退 fixture 或假零。

### 3.2 sequence
- 每个 snapshot epoch 内从 0 开始、逐快照 +1。
- 消费方只在同一 `epoch` 内比较 sequence；跨 epoch 的 sequence 比较无意义。

### 3.3 epoch（三级，各自独立递增）
计数器回卷、进程重启、采集会话变更分别触发对应层级的 epoch 递增。**任何累计差值/速率只允许在同一层级同一 epoch 内计算；epoch 变化后首样本速率为 null，不得出现负速率。**

| 层级 | 字段 | 递增触发 | 失效对象 |
|---|---|---|---|
| 快照 | NetworkSnapshot.epoch | 采集会话变更：采集服务/helper 重启、休眠恢复后重建、计数源整体重建 | sequence（归零）、droppedEvents（归零）、全部跨快照比较 |
| 接口 | InterfaceObservation.epoch | 该接口计数器回卷（32/64 位 wrap）、接口被移除后重新枚举 | rxBytes/txBytes 的跨快照差值 |
| 应用 | AppObservation.epoch | 同一稳定 appId 下进程重启导致应用口径累计归零 | uploadBytes/downloadBytes 的跨快照差值 |

- epoch 为非负整数，在其作用域内单调递增、不回退、不复用。
- 编码纪律：任一累计计数在达到 JS 安全整数上限（2^53−1 = 9007199254740991）之前**必须**开启新 epoch，使所有 JSON 数字保持安全整数；十进制字符串表示保留给未来协议升级，v1 不使用。

## 4. coverage 六态与 coverageReasons

| coverage | 进入条件 | 数据载荷规则 |
|---|---|---|
| active | 采集运行中且全部已启用数据源可用 | apps/interfaces 可非空；未知字段按 §7 置 null |
| partial | 采集运行中但至少一个数据源不可用（如 ETW 事件被拒、PID 连接表部分不可读、接口计数器不可用、代理解析不可用、域名解析不可用） | 可用部分如实给出；不可用字段按 §7 置 null |
| starting | 采集已请求、尚未产出首个完整样本 | apps=[]、interfaces=[] |
| stopped | 用户关闭观察（默认关闭） | apps=[]、interfaces=[] |
| denied | 权限被拒绝或被撤销 | apps=[]、interfaces=[] |
| disconnected | 宿主/helper 不可达（崩溃、IPC 断开且尚未重连） | apps=[]、interfaces=[] |

规则 R-COV：
1. `active` ⇒ coverageReasons 必须为空数组；其余五态 ⇒ 至少一条原因码。
2. 非数据态（starting/stopped/denied/disconnected）⇒ apps 与 interfaces 必须为空数组。保留旧快照与时间是 UI 职责，快照本身不携带陈旧数据。
3. 中断恢复时历史缺口不补零、不补末值；缺口在 history 中表现为时间空洞。

原因码枚举（coverageReasons 元素）：

| 原因码 | 适用 coverage |
|---|---|
| capture-initializing | starting |
| user-disabled | stopped |
| permission-denied | denied |
| host-unreachable | disconnected |
| helper-crashed | disconnected |
| etw-unavailable | partial |
| pid-table-partial | partial |
| interface-counters-unavailable | partial |
| proxy-correlation-unavailable | partial |
| domain-resolution-unavailable | partial |

## 5. capabilities

```json
"capabilities": { "observe": true, "block": false, "terminate": false, "permissions": true }
```

- 四个字段必须真实来自采集服务能力查询，且必须是 JSON 布尔值（拒绝 0/1/字符串）。
- `observe`：只读观测能力；`permissions`：宿主可表达/请求权限状态。
- `block`/`terminate`：**N2 只读阶段恒为 false**。能力探测到真实阻断/终止能力之前不得置 true；UI 依据 false 禁用入口（接续检查表 rule.configure / flows.terminate 行）。
- 权限请求送达不代表已授权；capabilities 以系统能力快照复核为准。

## 6. 身份模型

### 6.1 应用身份
- `AppObservation.id`（appId）：稳定应用身份，**绝不只含 PID**。进程重启后 appId 保持不变（此时 app epoch 递增）。
- 建议派生：规范化可执行路径或包族名的不可逆散列加可读前缀；Windows 建议形态 `exe-<sha256前16位>`、商店应用 `appx-<包族名散列>`。Schema 约束：`^[a-z0-9][a-z0-9._:-]{0,127}$`。
- `identitySource`：身份派生方式，`^[a-z0-9-]{1,64}$`，已知值 `executable-path`、`package-family`、`fallback-name`（最后者须在 README 记录降级原因）。
- `name` 为真实主名称；`bundleId` 在 Windows 上通常为 null（macOS 为 Bundle ID）；`path` 为可执行路径或 null。

### 6.2 进程身份
- `ProcessIdentity = pid + startTime + name + parentPid? + executablePath?`。
- PID 会复用：**pid + startTime 联合**才是进程实例身份；仅用 PID 关联连接属违约（AN02 PID 重用场景的依据）。
- `parentPid`、`executablePath` 不可获得时为 null。

### 6.3 连接观测身份与方向
- `ConnectionObservation.id`：稳定方向性观测 ID（非显示序号），同一传输连接的收、发是两个观测、两个 ID。
- `direction`（`up`/`down`）：**字节方向**——up=本机到远端，down=远端到本机；不是 TCP 发起者身份。
- `initiator`（`local`/`remote`/`unknown`）：**连接发起方向**，与 direction 分离；无证据时为 unknown。
- `bytes`：该方向已观测字节数（非包数），未知为 null。
- 结束事件只结算一次；重复结束事件幂等（消费方按 id 去重）。

### 6.4 接口身份
- `InterfaceObservation.id`：稳定接口实例身份（Windows 建议 InterfaceGuid；变化即新 id）。`kind` ∈ `physical` / `tunnel` / `loopback` / `unknown`，分类证据不足时必须是 unknown。

## 7. 可空语义（冻结决议）

**未知绝不编码为 0。** 下列字段全部可空，null=未知/不可用：

| 字段 | v1 拟议状态 | 冻结决议 |
|---|---|---|
| InterfaceObservation.upRate / downRate | 非空 | **改为可空**（计数器暂不可读、epoch 首样本） |
| InterfaceObservation.rxBytes / txBytes | 不存在 | 新增，可空；当前接口 epoch 内累计收发字节 |
| AppObservation.uploadBytes / downloadBytes | 可空 | 维持可空（当前 app epoch 内、自 captureStartedAt 起累计） |
| AppObservation.upRate / downRate | 可空 | 维持可空 |
| AppObservation.connectionCount | 非空 integer | **改为可空**（连接表不可读时不得报 0） |
| ConnectionObservation.bytes | 非空 | **改为可空** |
| ConnectionObservation.hostname / ip / proxyFlowId | 可空 | 维持可空 |
| TrafficPoint.up / down | 各自可空，但原型校验要求同点同时有效或同时未知 | **上下行可分别未知**，见下 |
| NetworkSnapshot.droppedEvents | 不存在 | 新增，可空 |

**TrafficPoint 决议（消除包内不一致）**：拟议 v1 类型允许 `up`/`down` 分别为 null，但原型 `validateSnapshot` 要求同一采样点两者同时有效或同时未知，二者矛盾。N0 裁决：**同一采样点上下行可分别未知**（例如仅接口计数器可读上行、下行未知是合法状态），校验器按字段独立校验；UI 对单侧未知的点不连线、不外推。原型校验随之作废，以本契约与 `network-snapshot.v1.schema.json` 为准。

## 8. 统计口径（硬规则）

1. 接口流量（interfaces）与应用/连接流量（apps）是两个口径，**分别标注、绝不相加**；物理、TUN、loopback、原应用、代理转发不得盲目相加；不得把接口流量统称互联网用量。
2. 域名必须有来源：`domainSource` ∈ `system` / `proxy` / `unknown`；无证据即 unknown，hostname 为 null 时展示 IP 或"未知"。hostname 非 null 时 domainSource 不得为 unknown（S3）。
3. Mihomo/代理关联只能通过 `proxyFlowId` 表达，且仅在存在真实关联证据时填写；凭时间相近或字节相近配对属违约。该字段可选。
4. 看见发送元数据不等于看见 HTTPS 内容；本契约只描述元数据，不解密、不记录载荷。

## 9. 历史与资源上限（双限）

历史保留采用**时间与记录/字节双限**，任一触发即裁切最旧数据并置 `truncated=true`：

| 约束 | 冻结值 | 性质 |
|---|---|---|
| 历史时间窗 | 7200000 ms（2 小时） | **输入参考，待原生资源验收**（探针报告确认后转正式） |
| 单序列历史点 | ≤ 7200（对齐约 1s 采样 × 2h） | 同上，Schema maxItems 硬限 |
| interfaces | ≤ 128 | 输入参考，待原生资源验收 |
| apps | ≤ 2000 | 输入参考，待原生资源验收 |
| 单 app connections | ≤ 2000 | 输入参考 |
| 单 app processes | ≤ 256 | 输入参考 |
| 全快照总记录（connections+history+processes） | ≤ 100000 | 语义规则 S7 |
| 全消息序列化字节 | ≤ 16 MiB（UTF-8） | 语义规则 S8，硬上限 |
| 字符串长度 | 见 Schema 各字段 maxLength（id≤128、name≤512、hostname≤253、path≤1024 等） | Schema 硬限 |

- 采集默认约 1s 聚合一个采样点；原始事件在后台聚合，UI 每秒至多消费一个有界快照，不逐包重绘。
- 无历史时保留开始前空白，不复制旧值铺满窗口；休眠/停止/计数器重置形成真实缺口。

## 10. 传输与消息边界

- `snapshot.get` 返回一份完整快照；`snapshot.updated` 事件载荷为**完整替换**——消费方丢弃旧快照整体替换，禁止增量合并、禁止与旧累计二次相加。
- 快照必须通过 §11 全部校验才被接受；校验失败保持上一份有效快照并将其 coverage 视为 disconnected（UI 层行为）。
- 陈旧防护：消费方按 `(epoch, sequence)` 拒绝回退快照；跨 epoch 快照直接替换且不继承任何累计差值。
- 数值一律为 JS 安全整数范围内的 JSON number（速率允许非整数）；超过 2^53−1 的值按 §3.3 以新 epoch 规避，v1 不出现字符串编码数字。
- 桥接 8 秒超时只界定传输失败，不代表任何默认放行/拒绝语义。

## 11. 语义校验规则（Schema 之外的强制项）

校验器（tools/NetworkProbe validate）与未来宿主校验必须实现：

| 编号 | 规则 |
|---|---|
| S1 | apps 内 id 唯一；interfaces 内 id 唯一；单 app 内 connections 的 id 唯一；单 app 内 processes 的 (pid,startTime) 唯一 |
| S2 | hostname 非 null ⇒ domainSource ∈ {system, proxy}；proxyFlowId 非 null ⇒ domainSource = proxy |
| S3 | 每个 history 序列 t 严格递增，且 t ≤ 所属快照 asOf |
| S4 | captureStartedAt ≤ asOf |
| S5 | 非数据 coverage（starting/stopped/denied/disconnected）⇒ apps 与 interfaces 均为空数组（§4 R-COV-2），从而非数据态下不存在任何流量数值（防 0 冒充未知） |
| S6 | coverage=active ⇒ coverageReasons 为空数组；其余五态 ⇒ 非空（§4 R-COV-1） |
| S7 | 全快照 connections+history+processes 总记录数 ≤ 100000 |
| S8 | 快照 JSON 序列化 UTF-8 字节数 ≤ 16777216（16 MiB） |

JSON Schema 已表达的（required、类型、枚举、范围、maxItems、maxLength、pattern、additionalProperties=false、coverage/coverageReasons 的 if-then）由 Schema 直接校验，不重复列入。

## 12. 与拟议 v1 的差异决议汇总

1. 接口 upRate/downRate 非空 → 可空；新增 rxBytes/txBytes 可空累计。
2. AppObservation.connectionCount 非空 → 可空。
3. ConnectionObservation.bytes 非空 → 可空。
4. TrafficPoint 同点双有效约束 → 上下行独立可空（§7 决议）。
5. 新增三级 epoch、sequence、droppedEvents、truncated、coverageReasons。
6. 新增 initiator 字段，direction 明确为字节方向。
7. 原型 `validateSnapshot`（局部数组限额）作废，以 Schema + §11 为完整宿主校验基线。
8. capabilities 明确布尔校验与 N2 恒 false 规则。
9. 原型 `icon` 字段（演示资产）不进入契约；正式图标由原生层从本机应用读取。
10. 应用 watched/关注状态为本地 UI 偏好，不进入快照。

## 13. 只读阶段（N2）约束

- 不阻断、不终止连接、不提醒：capabilities.block = capabilities.terminate = false 恒成立。
- 不解密 HTTPS、不记录载荷、不上传元数据；默认只保留本机元数据，导出由用户显式触发且默认脱敏。
- 主 UI 普通权限；需要提权的采集由受控 helper 承担（边界见平台能力探针报告 `docs/research/260920-network-capability-probe-architect.md`）。
- 生产 origin=host；无宿主/无权限/未采集显示真实 coverage，绝不回退 fixture 或假零。

## 14. fixtures 清单（tests/ScreenTimeoutToggle.Tests/Fixtures/Network/）

全部脱敏：域名用 `*.example`，IPv4 用 TEST-NET-3（203.0.113.0/24），IPv6 用文档前缀 2001:db8::/32，进程名用 appA.exe/appB.exe/proxyd.exe/svchost-svc.exe 等虚构名。

| 文件 | 场景 | 期望校验 |
|---|---|---|
| normal.json | active；3 应用 2 接口（physical+tunnel）；含代理关联与双向观测 | 通过 |
| partial-coverage.json | partial（etw-unavailable）；接口速率在、应用字节/连接数 null；droppedEvents>0 | 通过 |
| denied.json | denied；capabilities.observe=false；空数组 | 通过 |
| disconnected.json | disconnected（host-unreachable）；空数组 | 通过 |
| starting.json | starting（capture-initializing）；空数组 | 通过 |
| stopped.json | stopped（user-disabled）；空数组 | 通过 |
| epoch-rollover.json | 采集会话与进程重启：snapshot.epoch=1 且 sequence=0；app epoch 递增、累计归零、新 PID+startTime | 通过 |
| counter-wrap.json | 接口计数器回卷：interface.epoch=1、rxBytes/txBytes 为回卷后小值、速率正常 | 通过 |
| tun-and-loopback.json | tunnel 与 loopback 接口并存；代理应用经 loopback 的观测用 proxyFlowId 关联 | 通过 |
| ipv6-quic.json | IPv6 文档地址 + UDP/443（QUIC 形态，protocol=UDP） | 通过 |
| unknown-domain.json | hostname=null、domainSource=unknown、route=unknown | 通过 |
| single-sample-history.json | 单点历史；同点 up 有效、down=null（§7 独立可空决议的直接证据） | 通过 |
| invalid-zero-as-unknown.json | denied 却携带 apps 数据且以 0 冒充未知（违反 S5） | **必须失败** |
| invalid-missing-epoch.json | 应用缺 epoch 必填字段（Schema required 违反） | **必须失败** |

## 15. X1 种子：跨端同义 fixture 清单

以下 fixture 的语义与未来 macOS 端共用（同 schemaVersion、同 coverage/原因码、同可空与 epoch 语义），Mac 端实现后应以这些 fixture 做逐字段同义校验；本阶段不在 Mac 侧做任何改动：

1. normal.json（active 全量语义）
2. partial-coverage.json（partial + 原因码语义）
3. denied.json（权限拒绝语义）
4. disconnected.json（断连语义）
5. epoch-rollover.json（三级 epoch 语义）
6. counter-wrap.json（计数器回卷语义）
7. unknown-domain.json（域名来源语义）
8. single-sample-history.json（TrafficPoint 独立可空语义）

仅样式变量与 fixture 语义跨端共享；采集实现与 UI 运行时不共享（PRD R38）。

## 16. 变更纪律

先改契约（本文档 + Schema + fixtures + 校验器），再改实现；实现与契约冲突时以契约为准或回写契约修订并留痕。契约的端到端验证：`tools/NetworkProbe validate` 对 §14 全部 fixture 给出 12 通过 / 2 必须失败的结果（命令与退出码见平台能力探针报告附录）。
