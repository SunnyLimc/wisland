# Wisland Media Presentation Architecture — Design

状态：**设计稿（未实现）**
目标：把目前散落在 `MediaService`、`MainWindow.Media`、`IslandController`、`AiSongResolver`、`Notifications` 等处的"当前展示哪一首媒体、怎么过渡、怎么抗抖"这条链路收敛到一个**单一真相源（Single Source of Truth）——`MediaPresentationMachine`**，以根治当前两类偶现缺陷：

1. 切歌时 expanded view 的左右划动画不总是触发（新歌信息直接替换，没有动画）。
2. 切歌瞬间偶尔泄露 Chrome 其它标签页暂停态的 metadata / thumbnail。

---

## 1. 现状链条盘点

### 1.1 原始数据采集 — `Services/Media/MediaService*` + `Refresh`
订阅 GSMTC，维护 `TrackedSource` 的 title / artist / thumbnail / playback / timeline / presence / missingSince 等 raw 字段，并按需通过 `PrepareStateChange_NoLock + DispatchChange` 广播 `SessionsChanged` / `TrackChanged`。

### 1.2 切歌抗抖 — `MediaService.Stabilization`
混合三种 reason：
- `SkipTransition`：用户点 Skip 后抵挡 Chrome 其它标签页的 paused metadata。
- `NaturalEnding`：接近曲末时 arm，吞掉末尾噪声。
- 非 metadata write 触发 fresh‑track 时的 `StabilizationMetadataConfirmationHoldMs=80ms` 短 hold。
共享 `FrozenSnapshot`、`StabilizationBaselineTitle/Artist`、`StabilizationExpiresAtUtc`、`_stabilizationTimer`。

### 1.3 焦点仲裁 — `MediaFocusArbiter`
多 session 情况下决策"展示哪条"，带 `autoSwitchDebounce` + `missingSourceGrace`。内部持有 `_pendingAutoWinnerKey / _pendingAutoWinnerSinceUtc`。

### 1.4 用户选择锁 — `MainWindow.Media`
`_selectedSessionKey / _selectionLockUntilUtc / _selectionLockTimer`。滚轮 / session picker 点击后一段时间内禁止 arbiter 抢焦点。

### 1.5 视觉顺序 — `_sessionVisualOrderKeys / GetVisualOrderedSessions`
让 avatar strip / picker 顺序稳定。

### 1.6 自动切焦点计时器 — `_autoFocusTimer / SyncAutoFocusTimer`

### 1.7 动画方向 intent — `_pendingMediaTransitionDirection + _pendingMediaTransitionTimestamp`
一次性 token，`TrackSwitchIntentWindowMs=1600ms`；由 `SkipNext_Click / SkipPrevious_Click / TryCycleDisplayedSession / SelectSession` 写入，第一次 `contentChanged` 消费。

### 1.8 UI 身份去重 — `_lastDisplayedContentIdentity / _lastDisplayedProgressIdentity`
字符串 hash，当前把 `(switching|steady)` 也拼进 identity。

### 1.9 AI 改写 — `AiSongResolverService` + `ApplyAiOverride` + `_aiResolveCts`
对 `(sourceId, title, artist)` 异步改写；`_lastAiResolveContentIdentity / _lastAiOverrideLookupIdentity / _lastAiOverrideLookupResult`。

### 1.10 通知覆盖 — `MainWindow.Notifications` + `IslandController.IsNotifying`
`ShowNotification` 期间 `SyncMediaUI` 不调用 `UpdateMedia`。通知结束后没有"补放一次动画"。

### 1.11 进度条 reset — `RequestMediaProgressReset` / `_isMediaProgressResetPending` / `IslandProgressBar.SnapToZero`
单独一条身份 `_lastDisplayedProgressIdentity`，与主 identity 并行。

### 1.12 专辑图 / 调色盘 — `ImmersiveMediaView._lastAlbumArtIdentity / _isBusyTransport` + `AlbumArtColorExtractor`
UI 自己决定何时保留旧封面。`BuildAlbumArtIdentity` 自成一派。

### 1.13 Session Picker overlay — `MainWindow.SessionPicker` + `SessionPickerWindow`
通过 `IsTransientSurfaceOpen` 影响 island controller。

### 1.14 岛身形态 — `IslandController`
输入：`IsHovered / IsDragging / IsDocked / IsNotifying / IsForegroundMaximized / IsHoverPending / IsTransientSurfaceOpen / UseImmersiveDimensions`。输出：target W/H/Y/opacity。

### 1.15 前台监视 — `ForegroundWindowMonitor` → `IsForegroundMaximized`

### 1.16 渲染循环节流 — `UpdateRenderLoopState`

---

## 2. 当前设计的冲突点

| # | 冲突 / 漏点 | 现象 |
|---|---|---|
| C1 | `CreateContentIdentity` 把 `switching|steady` 塞进 identity | Pending 期间任意一次 `SessionsChanged` 都会"先用 switching 一跳消费 pending 方向"，真正的新歌跳变 fallback 到 `ApplyImmediately`，**动画丢失** |
| C2 | `TrackSwitchIntentWindowMs=1600ms` vs `SkipTransitionTimeoutMs=10000ms` | Chrome 慢切歌时 intent 先过期，**动画丢失** |
| C3 | `IsNotifying` 分支跳过 `UpdateMedia` 但仍更新 `_lastDisplayedContentIdentity` 并 `ClearPendingMediaTransitionDirection` | 通知期内到达的新歌**永久静默更新** |
| C4 | 非 metadata write 的 fresh‑track 短 hold 只有 80ms | 低于 Chrome 实际 metadata 到达延迟，到期后 raw 的**中间标签页状态泄露** |
| C5 | `ShouldShowTransportSwitchingHint=IsStabilizing` vs UI `showBusyTransportState = IsStabilizing && MissingSinceUtc.HasValue` | 身份判"切换中"但 UI 侧防护未开，专辑图/副标题可能提前更新 |
| C6 | `tracked.Thumbnail` 在稳定化期间仍被 raw 覆盖，叠加 `ArmSkipStabilization` 的 re‑arm 会把当前 raw 封进 frozen | 连点 Skip 时，B 标签页的 thumbnail/title 被写入 frozen baseline，释放后泄露 |

---

## 3. 职能归类

| 分类 | 成员 |
|---|---|
| **"当前展示什么媒体"决策的核心状态** | 1.1 / 1.2 / 1.3 / 1.4 / 1.6 / 1.7 / 1.8 / 1.9 / 1.10 / 1.11 / 1.12 |
| **岛身形态 / 交互**（只消费状态，不产生媒体状态） | 1.13 / 1.14 / 1.15 / 1.16 |
| **布局辅助** | 1.5 |

所有"核心状态"应集中到 `MediaPresentationMachine` + 其 Policies 中；外层只订阅 Frame。

---

## 4. 目标架构

### 4.1 命名空间布局

```
Services/Media/
  MediaService (保留 raw 数据层)
  Presentation/
    MediaPresentationMachine       // 单线程事件驱动
    MediaPresentationFrame         // 对外帧
    MediaTrackFingerprint          // Session×Title×Artist×Thumbnail
    PresentationKind               // Steady | Switching | Confirming | Missing | Empty | Notifying
    FrameTransitionKind            // None | Replace | Slide(direction) | Crossfade | ResumeAfterNotification
    SwitchIntent                   // Origin + Direction + DeadlineUtc
    Policies/
      FocusArbitrationPolicy       // 原 MediaFocusArbiter
      ManualSelectionLockPolicy    // 原 selection lock
      StabilizationPolicy          // 原 Stabilization.cs，拆成 Skip / NaturalEnding / Confirming
      AiOverridePolicy             // 原 AiSongResolver 注入逻辑
      NotificationOverlayPolicy    // 原 Notifications 覆盖
```

### 4.2 状态集

```
Idle
Steady(track, orderedSessions)
PendingUserSwitch(frozen, intent, deadlineUtc)
PendingNaturalSwitch(frozen, deadlineUtc)
Confirming(draft, firstSeenUtc, lastConfirmedUtc, pendingThumbnail, carriedIntent?)
NotifyingOverlay(inner: State)      // 正交包裹
```

### 4.3 事件集

```
GsmtcSessionsChanged(snapshot[])
UserSkipRequested(direction)
UserSelectSession(key, direction)
UserManualUnlock()
AutoFocusTimerFired
StabilizationTimerFired
MetadataSettleTimerFired
AiResolveCompleted(result)
NotificationBegin(payload)
NotificationEnd
SettingsChanged(scope)
```

所有事件单线程消费（`Channel<TEvent>` + 独立 worker 或 dispatcher post‑back）。

### 4.4 关键迁移

| From | Event | Guard | To | Action |
|---|---|---|---|---|
| `Idle` | `GsmtcSessionsChanged` | sessions ≥ 1 | `Steady` | pick via `FocusArbitrationPolicy`，emit `Replace` frame |
| `Steady` | `UserSkipRequested` | session 可稳定化 | `PendingUserSwitch` | 捕获 frozen；`intent = SwitchIntent(fingerprint, direction, now + SkipTransitionTimeoutMs)` |
| `Steady` | `GsmtcSessionsChanged` | 展示 session 处于 near‑end + metadata 将变 | `PendingNaturalSwitch` | 捕获 frozen |
| `Steady` | `GsmtcSessionsChanged` | 需换焦点（arbiter 判定） | `Steady` (new track) | `Transition = Replace`（无用户 intent） |
| `PendingUser/NaturalSwitch` | `GsmtcSessionsChanged` | raw 看起来像 paused‑tab（Paused or pos远≠0 or title空） | self | **不改 baseline，不对外发 frame**，不刷新 thumbnail baseline |
| `PendingUser/NaturalSwitch` | `GsmtcSessionsChanged` | raw Playing + pos≤3s + title 与 baseline 不同 + title 具体 | `Confirming` | `draft = raw`，`firstSeenUtc = now`，保留 intent |
| `PendingUser/NaturalSwitch` | `StabilizationTimerFired` | deadline 到 | `Steady(raw, fallback=true)` | intent 未过期 → `Slide(direction)`；否则 `Replace` |
| `Confirming` | `GsmtcSessionsChanged` | raw 与 draft 不一致 | `PendingUserSwitch`（保留 intent）| 丢弃 draft |
| `Confirming` | `GsmtcSessionsChanged / MetadataSettleTimerFired` | (now - firstSeenUtc) ≥ MetadataSettleMs | `Steady(confirmed)` | emit `Slide(intent.direction)` 或 `Replace`；提升 `pendingThumbnail` → thumbnail；消费 intent |
| 任意 | `NotificationBegin` |  | `NotifyingOverlay(inner=current)` | emit `FlagsOnly` kind=Notifying |
| `NotifyingOverlay` | `NotificationEnd` |  | inner（可能已在内部迁移到新 Steady） | emit `ResumeAfterNotification` |
| `NotifyingOverlay` | 其它事件 |  | 递归喂 inner | inner 迁移在幕后进行，不对外发 frame（累计到 Resume 时一起发）|

### 4.5 `SwitchIntent` 生命周期

```csharp
record SwitchIntent(
    MediaTrackFingerprint Origin,
    ContentTransitionDirection Direction,
    DateTimeOffset DeadlineUtc);
```

- 写入：`UserSkipRequested / UserSelectSession`。
- 保留：`now ≤ Deadline && CurrentFingerprint == Origin`。
- 消费：`Confirming → Steady` 且 `newFingerprint != Origin`。
- 覆盖：下一次用户 skip / select；保留 Origin 不变（连点仍指向原来的点击起点）。
- 丢弃：Deadline 过期；或 fallback 后仍过期。
- **状态标志翻转、其它 session 变化、通知覆盖均不消费 intent**。

### 4.6 Confirming 的 settle 规则（取代 `StabilizationMetadataConfirmationHoldMs`）

常量建议：
```
MetadataSettleMs            = 250  // 期待 draft 保持一致
StabilizationMetadataConfirmationHoldMs = 删除
SkipTransitionTimeoutMs     = 10000 (沿用)
NaturalEndingTransitionTimeoutMs = 3000 (沿用)
```

逻辑：
- 进入 `Confirming(draft, firstSeenUtc)`。
- 收到 metadata write：
  - 与 draft 一致 → `lastConfirmedUtc = now`；若 `now - firstSeenUtc ≥ MetadataSettleMs` → release。
  - 不一致 → 回到 `PendingUserSwitch`（保留 intent），重置 draft。
- 收到 thumbnail：写入 `pendingThumbnail`，不影响 settle 判定。
- 到 stabilization deadline 仍没 release → fallback。

### 4.7 对外契约

```csharp
public enum PresentationKind { Empty, Steady, Switching, Confirming, Missing, Notifying }
public enum FrameTransitionKind { None, Replace, SlideForward, SlideBackward, Crossfade, ResumeAfterNotification }

public readonly record struct MediaTrackFingerprint(
    string SessionKey, string Title, string Artist, string ThumbnailHash);

public sealed record MediaPresentationFrame(
    long Sequence,
    MediaSessionSnapshot Session,
    IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
    int DisplayIndex,
    PresentationKind Kind,
    FrameTransitionKind Transition,
    MediaTrackFingerprint Fingerprint,
    MediaTrackFingerprint? ProgressFingerprint,
    bool IsFallback,
    AiOverrideSnapshot? AiOverride);
```

**不变量**：
1. `Sequence` 严格递增；UI 忽略旧 frame。
2. `Transition != None ⇒ fingerprint 有变化 或 Transition == ResumeAfterNotification`。
3. Fingerprint 变化 ⇒ 必产生一个 `Transition != None`（动画机会不丢）。
4. `PresentationKind` 变化（如 Steady→Switching→Confirming）但 Fingerprint 不变 ⇒ `Transition = None`（UI 只更新 chip 文案，不动主视觉）。
5. `Pending*` 内部状态不对外发 frame（除非包在 `NotifyingOverlay` 里，作为 inner pass‑through 聚合到 Resume）。
6. `Thumbnail` 在 `Pending/Confirming` 期间只存活于 `pendingThumbnail`，不会被读进 Frame.Session。

### 4.8 MainWindow.Media 重新定位

`MainWindow.Media.cs` 仅剩：
- 订阅 `MediaPresentationMachine.FrameProduced`。
- 把 frame 喂给 `ExpandedMediaView.UpdateMedia(frame)` / `ImmersiveMediaView.UpdateMedia(frame)`。
- 根据 frame 驱动：
  - `IslandController.UseImmersiveDimensions`
  - `UpdateRenderLoopState`
  - progress reset（以 `frame.ProgressFingerprint` 为准）
- 转发用户输入事件到 Machine（`UserSkipRequested` / `UserSelectSession` / `UserManualUnlock`）。

全部移除：`_pendingMediaTransitionDirection`、`_selectedSessionKey`、`_selectionLockUntilUtc`、`_sessionVisualOrderKeys`、`_lastDisplayed*`、`_ai*` 等字段。

### 4.9 IslandController 边界收紧

- `IsNotifying` 改名 `IsForcedExpanded`，只由 `NotificationOverlayPolicy` 通过一个窄 API 设置。
- Session Picker、Notification 不再直接访问 `_controller.IsNotifying` / `IsTransientSurfaceOpen`；都走 policy → controller。

---

## 5. 事件流示例

### 5.1 Chrome 正常切歌（Skip Next）

```
t0      User clicks Skip
        Dispatch(UserSkipRequested(Forward))
        Steady(A) → PendingUserSwitch{ frozen=A, intent(A→?, Forward, deadline=t0+10s) }
        (no frame)
t0+300  Chrome 上报 paused tab B 的 title
        guard: Paused & 非 fresh → 保持 Pending；不改 baseline；pendingThumbnail 不写
        (no frame)
t0+800  Chrome 上报 Playing + pos=0.1s，但 raw.title 还是 B
        guard: title=B=baseline? 若等则 title 还没换，保持 Pending
t0+1100 Chrome 上报 real title C + Artist C
        guard: title=C, 具体, 与 baseline 不同 → Confirming{draft=C, firstSeenUtc=t0+1100}
        (no frame)
t0+1350 Chrome 再上报 title C（或无新 metadata 但 settle timer 到）
        now - firstSeenUtc = 250 ms ≥ MetadataSettleMs
        Confirming → Steady(C), Transition=SlideForward（intent 未过期且匹配）
        pendingThumbnail 提升为 thumbnail
        emit Frame(Sequence+1, C, Kind=Steady, Transition=SlideForward)
```

UI 收到 `SlideForward` → 左右划动画播放。
Chrome 慢也不怕：intent deadline 是 10 s。

### 5.2 通知覆盖期间切歌

```
t0      NotificationBegin(payload)
        Steady(A) → NotifyingOverlay(inner=Steady(A))
        emit Frame(Kind=Notifying, Transition=None)
t0+500  GSMTC 切歌到 C（走完 Pending → Confirming → Steady）
        inner: Steady(A) → ... → Steady(C, pendingTransition=Slide)
        对外不发 frame（被 overlay 遮住）
t0+2500 NotificationEnd
        NotifyingOverlay → Steady(C)
        emit Frame(Kind=Steady, Transition=ResumeAfterNotification, Fingerprint=C)
```

UI 看到 ResumeAfterNotification 可以选择 `Crossfade` 或 `Slide`（策略可配置），**动画不丢**。

### 5.3 连点 Skip 两次

```
Skip(1): Steady(A) → PendingUserSwitch{ frozen=A, intent₁(A→?, Forward, t₁) }
(800ms 后，raw 已被 Chrome 更新为 paused B，但 baseline 未改)
Skip(2): UserSkipRequested(Forward)
         re‑arm guard: raw 非 Playing/fresh → 拒绝把 baseline 换成 B
         intent = SwitchIntent(A, Forward, new deadline t₂)  ← Origin 仍是 A
最终 C 到来 → Confirming → Steady(C), SlideForward
```

不会把 B 封进 frozen baseline，也不会泄露 B。

### 5.4 稳定化超时兜底

```
Skip → PendingUserSwitch
... 10 s 内未收到 Playing + concrete title
StabilizationTimerFired → Steady(raw_fallback, IsFallback=true)
  if intent 未过期 → Transition=SlideForward
  else → Transition=Replace
```

UI 仍然拿得到动画机会（如果 intent 仍然有效）。

---

## 6. 关键不变量（可写成 debug assertion）

1. `frame.Sequence` 严格递增。
2. `frame.Transition == SlideForward | SlideBackward ⇒ frame.Fingerprint != previous.Fingerprint`。
3. `previous.Fingerprint != frame.Fingerprint ⇒ frame.Transition != None`。
4. `state ∈ {Pending*, Confirming}` 期间不发 frame（除非 overlay pass‑through 累计）。
5. `SwitchIntent` 仅当 `CurrentFingerprint == intent.Origin && now ≤ intent.Deadline` 时保留。
6. `frame.Session.Thumbnail` 永远来自已 settle 过的 metadata，而非 raw。

---

## 7. 分阶段实施

### P1 · 骨架迁移（行为不变）
- 新建 `Services/Media/Presentation/*` 骨架。
- `MediaFocusArbiter / Stabilization / SelectionLock / Ai override 缓存` 搬进 Policies，保持原行为。
- `MediaPresentationMachine` 作为 `MediaService.SessionsChanged` 的下游，输出 frame；`MainWindow.Media` 仍临时沿用旧逻辑双订阅，方便 A/B 对比。

### P2 · 动画三大根因
- `Fingerprint` 与 `PresentationKind` 分离，`CreateContentIdentity` 删除。
- `SwitchIntent` 替代 `_pendingMediaTransitionDirection`；Deadline = `SkipTransitionTimeoutMs`。
- `NotifyingOverlay` pass‑through + `ResumeAfterNotification`，彻底不再在通知中吞 identity。
- 删除 `TrackSwitchIntentWindowMs`（合并到 intent deadline）。

### P3 · 泄露三大根因
- 引入 `Confirming` + `MetadataSettleMs=250ms`。
- `MediaService` 新增 `pendingThumbnail`；`ArmSkipStabilization` re‑arm 时拒绝 baseline 被非 fresh raw 覆盖。
- 删除 `StabilizationMetadataConfirmationHoldMs=80ms` 路径。

### P4 · View 解耦
- `ExpandedMediaView.UpdateMedia(frame)` / `ImmersiveMediaView.UpdateMedia(frame)` 仅按 `frame.Transition` 执行；不再接 `ContentTransitionDirection direction` 参数。
- 专辑图 / progress / avatar 绑定 `frame.Fingerprint`；移除 `_lastAlbumArtIdentity / _isBusyTransport` 独立判定。

### P5 · 观测 & 测试
- Machine 输出结构化日志 `{seq, state_from, state_to, reason, fingerprint, intent}`。
- 单测覆盖 §4.4 全部迁移 + §5 全部场景，沿用 `MediaFocusArbiterTests.cs` 的风格。
- 在 `UpdateMedia` 里写入 §6 不变量的 debug assertion。

---

## 8. 与现有组件的映射

| 现有 | 去向 | 处置 |
|---|---|---|
| `MediaService.Stabilization.cs` | `StabilizationPolicy` + `Pending*/Confirming` 状态 | Reason 枚举保留 |
| `MediaFocusArbiter` | `FocusArbitrationPolicy` | 保留，签名不变 |
| `_selectedSessionKey / _selectionLockUntilUtc / _selectionLockTimer` | `ManualSelectionLockPolicy` | 删 MainWindow 字段 |
| `_pendingMediaTransitionDirection / _pendingMediaTransitionTimestamp` | `SwitchIntent` | 删 MainWindow 字段 |
| `_lastDisplayedContentIdentity / _lastDisplayedProgressIdentity` | `Frame.Fingerprint / ProgressFingerprint` | 删 |
| `_lastAiResolveContentIdentity / _lastAiOverrideLookupIdentity / _lastAiOverrideLookupResult` | `AiOverridePolicy` 内部缓存 | 删 |
| `ShouldShowTransportSwitchingHint` / `CreateContentIdentity(...,switching)` | `PresentationKind.Switching` 独立字段 | 删原函数 |
| `_controller.IsNotifying` | `IslandController.IsForcedExpanded` + `NotificationOverlayPolicy` | 收窄语义 |
| `_sessionVisualOrderKeys / GetVisualOrderedSessions` | Machine 内部输出整理 | 保留实现 |
| `_autoFocusTimer / SyncAutoFocusTimer` | Machine 调度 | 删 MainWindow 字段 |
| `ImmersiveMediaView._lastAlbumArtIdentity / _isBusyTransport` | `frame.Fingerprint / frame.Kind == Switching` | 简化 |
| `StabilizationMetadataConfirmationHoldMs` 常量 | 删除 | `MetadataSettleMs` 代替 |
| `TrackSwitchIntentWindowMs` 常量 | 删除 | `SkipTransitionTimeoutMs` 代替 |

---

## 9. 风险与取舍

1. **线程模型**：Machine 必须单线程消费事件（`Channel<TEvent>` + 专用 worker）。GSMTC 事件来自任意线程；UI 事件来自 DispatcherQueue；需要 dispatcher 桥 & back‑post 机制。
2. **AI 改写时序**：AI 缓存未命中时先发未改写 frame，AI 完成后再发一次 `Transition=None, Kind` 不变的 frame 覆盖标题 —— 避免为 AI 改写多做一次 Slide。
3. **P1 期间双订阅**：旧逻辑与 Machine 并行运行，用 debug log 对比 frame 与旧 identity。完成 A/B 验证后才切 P2。
4. **NotifyingOverlay pass‑through** 是设计里最巧的一环：inner state 累计多次迁移时，Resume 只发最终 frame；如果期间已存在有效 intent 且 fingerprint 已变，Resume 应尽量播 `SlideX`，其它情况退化到 `Crossfade`。
5. **测试覆盖**：迁移每条都要单测，初期工作量显著增加，但可以彻底消灭目前的偶现 bug。
6. **Thumbnail hash**：`MediaTrackFingerprint.ThumbnailHash` 不能是引用等价（`IRandomAccessStreamReference` 每次可能重新发），需要 Service 在取到 thumbnail 时计算一个稳定的 hash（例如前 N 字节的 xxhash），否则"封面换但 fingerprint 不变"问题无解。

---

## 10. 决策点（已决策）

### D1 · Resume 帧默认动画 — **已决策**
- 有有效 `SwitchIntent` 且 `fingerprint` 变化 → `SlideForward / SlideBackward`
- 其余情况（通知期间未切歌 / intent 已过期）→ `Crossfade`
- 绝不使用无动画的 `Replace`（会把 Resume 降级成硬切，体验差）
- 实现要点：`NotifyingOverlay` inner 需保留"待发 Slide"标志（累计的 intent 在 Resume 时一次性消费）

### D2 · `MetadataSettleMs` — **已决策 = 250 ms**
- 常量位置：`IslandConfig.MediaMetadataSettleMs = 250`（可改）
- 依据：Spotify <100 ms / YouTube Music 300–600 ms / 用户感知阈值 ~400 ms
- 不采用动态 settle，固定值实现简单且已覆盖 90% 场景；慢路径由 `SkipTransitionTimeoutMs` fallback 兜底

### D3 · `ThumbnailHash` 算法与触发点 — **已决策**
- 算法：**xxhash64(thumbnail 流前 4 KB)**，异步执行
- 触发点：`MediaService.UpdateMediaPropertiesAsync` 拿到新 thumbnail 后异步算 hash，写入 `TrackedSource.pendingThumbnailHash`；`Confirming → Steady` 提升时才读到 `Frame.Fingerprint`
- 失败回退：`(Title, Artist, Duration, SourceAppId)` 作为弱 hash，`Frame` 上标记 `ThumbnailHashIsFallback = true`
- 关键约束：frame 发出前 fingerprint 必须 ready，否则会出现 "相同内容 Slide" 的误判

### D4 · `NotificationOverlayPolicy` 范围 — **已决策：职责切开**
- Policy 只管：通知生命周期（Begin/End/Duration/Cancel）、Machine 覆盖语义、与 inner state 的 pass‑through 累计
- `MainWindow.Notifications.cs` 保留：把 `NotificationPayload` 渲染成实际 UI（文案、header 图标、持续时间取法）
- 对外契约：Frame 携带 `Kind = Notifying` 时附带 `NotificationPayload`；View 负责渲染

### D5 · Machine `IDisposable` — **已决策：是**
- Machine 持有 `Channel<TEvent>` + 专用 worker Task + 两个 timer（stabilization / metadataSettle）+ AI in‑flight token
- `Dispose()` 责任：取消 worker（CancellationToken）、dispose timers、取消 AI 请求、从 `MediaService` 退订
- 生命周期顺序：`MainWindow.Lifetime.cs` 关闭路径里 **`Machine.Dispose() → MediaService.Dispose()`**（否则 worker 可能在 dispose 后收到 stale 事件）
- Worker 循环模式：`await channel.Reader.WaitToReadAsync(ct)`，token 取消即干净退出

---

## 11. API 类型签名（最终契约）

所有类型位于 `wisland.Services.Media.Presentation` 命名空间（除非另行标注）。Model 类型可放 `wisland.Models`。

### 11.1 Frame / Fingerprint / 枚举

```csharp
namespace wisland.Models;

public enum PresentationKind
{
    Empty,
    Steady,
    Switching,       // Pending* 时的 UI hint（machine 本身 Pending 不发 frame，
                     // 这个枚举值通过 NotifyingOverlay inner pass-through 或
                     // fallback 时携带给 UI 显示 "Switching" chip 文字）
    Confirming,
    Missing,
    Notifying
}

public enum FrameTransitionKind
{
    None,
    Replace,
    SlideForward,
    SlideBackward,
    Crossfade,
    ResumeAfterNotification
}

public readonly record struct MediaTrackFingerprint(
    string SessionKey,
    string Title,
    string Artist,
    string ThumbnailHash)
{
    public static MediaTrackFingerprint Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty);

    public static MediaTrackFingerprint From(MediaSessionSnapshot session, string thumbnailHash)
        => new(session.SessionKey, session.Title, session.Artist, thumbnailHash);
}

public sealed record AiOverrideSnapshot(string Title, string Artist);

public sealed record NotificationPayload(
    string Title,
    string Message,
    string Header,
    int DurationMs);

public sealed record MediaPresentationFrame(
    long Sequence,
    MediaSessionSnapshot? Session,
    IReadOnlyList<MediaSessionSnapshot> OrderedSessions,
    int DisplayIndex,
    PresentationKind Kind,
    FrameTransitionKind Transition,
    MediaTrackFingerprint Fingerprint,
    MediaTrackFingerprint? ProgressFingerprint,
    bool IsFallback,
    bool ThumbnailHashIsFallback,
    AiOverrideSnapshot? AiOverride,
    NotificationPayload? Notification);
```

### 11.2 SwitchIntent

```csharp
namespace wisland.Services.Media.Presentation;

public readonly record struct SwitchIntent(
    MediaTrackFingerprint Origin,
    ContentTransitionDirection Direction,
    DateTimeOffset DeadlineUtc)
{
    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc > DeadlineUtc;
    public bool MatchesOrigin(MediaTrackFingerprint current)
        => Origin.SessionKey == current.SessionKey
        && Origin.Title == current.Title
        && Origin.Artist == current.Artist;
}
```

### 11.3 事件

```csharp
namespace wisland.Services.Media.Presentation;

public abstract record PresentationEvent;

public sealed record GsmtcSessionsChangedEvent(IReadOnlyList<MediaSessionSnapshot> Sessions) : PresentationEvent;
public sealed record UserSkipRequestedEvent(ContentTransitionDirection Direction) : PresentationEvent;
public sealed record UserSelectSessionEvent(string SessionKey, ContentTransitionDirection Direction) : PresentationEvent;
public sealed record UserManualUnlockEvent : PresentationEvent;
public sealed record AutoFocusTimerFiredEvent : PresentationEvent;
public sealed record StabilizationTimerFiredEvent : PresentationEvent;
public sealed record MetadataSettleTimerFiredEvent : PresentationEvent;
public sealed record AiResolveCompletedEvent(string SourceAppId, string Title, string Artist, AiSongResult? Result) : PresentationEvent;
public sealed record NotificationBeginEvent(NotificationPayload Payload) : PresentationEvent;
public sealed record NotificationEndEvent : PresentationEvent;
public sealed record SettingsChangedEvent(SettingsChangeScope Scope) : PresentationEvent;

public enum SettingsChangeScope { AiOverride, Language, ImmersiveMode, Other }
```

### 11.4 Machine

```csharp
namespace wisland.Services.Media.Presentation;

public sealed class MediaPresentationMachine : IDisposable
{
    public MediaPresentationMachine(
        MediaService mediaService,
        IReadOnlyList<IPresentationPolicy> policies,   // 注入顺序决定求解顺序
        AiSongResolverService? aiResolver,
        IDispatcherPoster dispatcherPoster);           // 把 frame post 回 UI 线程

    public event Action<MediaPresentationFrame>? FrameProduced;

    // 输入
    public void Dispatch(PresentationEvent evt);

    // 生命周期
    public void Start();
    public void Dispose();
}

public interface IDispatcherPoster
{
    void Post(Action action);
}
```

### 11.5 Policy 抽象

```csharp
namespace wisland.Services.Media.Presentation;

public interface IPresentationPolicy
{
    // 有状态 policy 在此声明自己维护的 "shadow state" 读接口
    // Machine 在特定状态迁移时调用 OnEvent 给 policy 注入事件或问询
    void OnAttach(MediaPresentationMachineContext context);
    void OnEvent(PresentationEvent evt, MediaPresentationMachineContext context);
    void OnTick(DateTimeOffset nowUtc, MediaPresentationMachineContext context);
}

public sealed class MediaPresentationMachineContext
{
    public DateTimeOffset NowUtc { get; internal set; }
    public IReadOnlyList<MediaSessionSnapshot> Sessions { get; internal set; } = Array.Empty<MediaSessionSnapshot>();

    // Policy 可写：arbiter / manual lock / stabilization / ai / notification
    public string? ManualLockedSessionKey { get; internal set; }
    public bool HasManualLock { get; internal set; }
    public StabilizationDirective StabilizationDirective { get; internal set; }
    public AiOverrideSnapshot? ActiveAiOverride { get; internal set; }
    public NotificationPayload? ActiveNotification { get; internal set; }

    // Machine 工具
    public void ScheduleStabilizationTimer(DateTimeOffset dueUtc);
    public void ScheduleMetadataSettleTimer(DateTimeOffset dueUtc);
    public void ScheduleAutoFocusTimer(DateTimeOffset? dueUtc);
}

public readonly record struct StabilizationDirective(
    MediaSessionStabilizationReason Reason,
    DateTimeOffset ExpiresAtUtc,
    MediaSessionSnapshot? FrozenSnapshot);
```

### 11.6 具体 Policy 类

```csharp
public sealed class FocusArbitrationPolicy : IPresentationPolicy { /* 包 MediaFocusArbiter */ }
public sealed class ManualSelectionLockPolicy : IPresentationPolicy { /* SelectionLockDurationMs */ }
public sealed class StabilizationPolicy : IPresentationPolicy
{
    // 吸收 ArmSkipStabilization / TryArmNaturalEndingStabilization / Confirming 流程
    // 持有 pendingThumbnail / pendingThumbnailHash 以解 C6 泄露
}
public sealed class AiOverridePolicy : IPresentationPolicy
{
    // 原 ApplyAiOverride + GetCachedAiOverride + TryRequestAiResolveAsync
    // 异步完成后 Dispatch(AiResolveCompletedEvent)
}
public sealed class NotificationOverlayPolicy : IPresentationPolicy
{
    // 只管生命周期和覆盖语义
}
```

### 11.7 MediaService 扩展（最小改动）

```csharp
public sealed partial class MediaService
{
    // 新增：pendingThumbnail + hash
    // TrackedSource 里增加:
    //   internal string? PendingThumbnailHash;
    //   internal IRandomAccessStreamReference? PendingThumbnail;

    // 新增一个纯输出管道：不再依赖 SessionsChanged 多路径
    public event Action<IReadOnlyList<MediaSessionSnapshot>>? RawSessionsChanged;

    // ArmSkipStabilization re-arm 时拒绝在 raw 非 fresh 状态下刷 baseline
    // ComputeThumbnailHashAsync(thumbnail): xxhash64(前 4 KB) | 失败返回 null
}
```

---

## 12. 按阶段的逐文件改动清单

> 每阶段都可独立合并、独立发布。编号 = 改动序号。

### P1 · 骨架迁移（零行为变化；并行双订阅）

新增：
1. `Services/Media/Presentation/MediaPresentationMachine.cs`（骨架 + event loop，不发 frame）
2. `Services/Media/Presentation/MediaPresentationMachineContext.cs`
3. `Services/Media/Presentation/IPresentationPolicy.cs`
4. `Services/Media/Presentation/PresentationEvent.cs`（上 §11.3 所有 record）
5. `Services/Media/Presentation/SwitchIntent.cs`
6. `Services/Media/Presentation/Policies/FocusArbitrationPolicy.cs`（包 `MediaFocusArbiter`）
7. `Services/Media/Presentation/Policies/ManualSelectionLockPolicy.cs`
8. `Services/Media/Presentation/Policies/StabilizationPolicy.cs`（仅搬 Stabilization.cs 逻辑，未改规则）
9. `Services/Media/Presentation/Policies/AiOverridePolicy.cs`
10. `Services/Media/Presentation/Policies/NotificationOverlayPolicy.cs`
11. `Models/MediaPresentationFrame.cs`（含 `PresentationKind`、`FrameTransitionKind`、`MediaTrackFingerprint`、`AiOverrideSnapshot`、`NotificationPayload`）
12. `wisland.Tests/MediaPresentationMachineTests.cs`（空，占位）

修改：
13. `MainWindow.xaml.cs`：实例化 Machine 但**不接它的 frame**，仅订阅打日志。
14. `MainWindow.Lifetime.cs`：关闭时先 `machine.Dispose()`，再 `mediaService.Dispose()`。

Phase 出口判定：build + 现有测试通过；Machine 日志输出事件序列可与旧 `SyncMediaUI` 行为比对。

### P2 · 动画三大根因（C1/C2/C3）

修改：
15. `Models/MediaPresentationFrame.cs`：`Transition/Fingerprint/Kind` 正式启用。
16. `Services/Media/Presentation/MediaPresentationMachine.cs`：输出 `FrameProduced` 事件，完整覆盖 §4.4 迁移表（其中 Confirming 可先用固定 0ms settle 通过测试，P3 再收紧）。
17. `MainWindow.Media.cs`：
    - 删除 `_pendingMediaTransitionDirection / _pendingMediaTransitionTimestamp` 字段 + 相关函数
    - 删除 `_lastDisplayedContentIdentity / _lastDisplayedProgressIdentity`
    - 删除 `ShouldShowTransportSwitchingHint / CreateContentIdentity / CreateProgressIdentity`
    - `SyncMediaUI` 改名 `OnFrameProduced(MediaPresentationFrame frame)`，仅做 UI 分发
18. `Views/ExpandedMediaView.xaml.cs`：新增 `UpdateMedia(MediaPresentationFrame)`，保留旧重载兼容到 P4。
19. `Views/ImmersiveMediaView.xaml.cs`：同上。
20. `MainWindow.Notifications.cs`：`ShowNotification` 改为 `Dispatch(NotificationBeginEvent/End)`；`_controller.IsNotifying` 改名 `IsForcedExpanded` 或由 `NotificationOverlayPolicy` 推送。
21. `Services/IslandController.cs`：`IsNotifying` → `IsForcedExpanded`，引用点更新。
22. 删除常量：`IslandConfig.TrackSwitchIntentWindowMs`。

Phase 出口判定：手测连续切歌 10+ 次动画稳定；单测覆盖 §4.4 关键行。

### P3 · 泄露三大根因（C4/C5/C6）

修改：
23. `Services/Media/MediaService.SourceTracking.cs`：`TrackedSource` 增加 `PendingThumbnail / PendingThumbnailHash`。
24. `Services/Media/MediaService.Refresh.cs`：thumbnail 写入改为写 `pendingThumbnail`，并启动 `ComputeThumbnailHashAsync`；完成后写 `pendingThumbnailHash`。
25. `Services/Media/MediaService.Stabilization.cs`：
    - `ArmSkipStabilization` re‑arm 增加 "raw 必须 Playing + fresh 才允许更新 baseline" 守卫
    - 删除 `StabilizationMetadataConfirmationHoldMs` 相关分支
26. `Services/Media/Presentation/Policies/StabilizationPolicy.cs`：实现 Confirming settle（§4.6）
27. `Models/IslandConfig.cs`：新增 `MediaMetadataSettleMs = 250`；删除 `StabilizationMetadataConfirmationHoldMs`。
28. `Services/Media/MediaService.State.cs`：`CreateSnapshot` 的 `Thumbnail` 字段仅在非 Confirming/Pending 时读 raw；否则读 frozen。

Phase 出口判定：多标签页 Chrome skip 压测，连点 5 次不再泄露 B tab 信息。

### P4 · View 解耦

修改：
29. `Views/ExpandedMediaView.xaml.cs`：删除旧 `UpdateMedia(MediaSessionSnapshot?, …)` 重载；完全基于 frame。
30. `Views/ImmersiveMediaView.xaml.cs`：同上；`_lastAlbumArtIdentity / _isBusyTransport` 改用 `frame.Fingerprint / frame.Kind`。
31. `Controls/DirectionalContentTransitionCoordinator.cs`：新增 `ApplyByFrameKind(FrameTransitionKind)` 或等价分发函数。

### P5 · 观测 & 测试

32. `wisland.Tests/MediaPresentationMachineTests.cs`：
    - `Steady → PendingUserSwitch → Confirming → Steady(Slide)`
    - `PendingUserSwitch + Paused B tab → 保持 Pending`
    - 连点 Skip → intent 更新但 Origin 不变
    - NotificationBegin/End pass‑through → Resume 动画
    - StabilizationTimer 兜底路径
    - AiResolve 完成二次发 frame
33. `Services/Media/Presentation/MediaPresentationMachine.cs`：结构化日志 `{seq, state_from, state_to, reason, fingerprint, intent}`。
34. §6 不变量以 `Debug.Assert` 写入 Machine `EmitFrame`。

---

## 13. 常量对照

| 常量 | 当前 | 目标 |
|---|---|---|
| `TrackSwitchIntentWindowMs` | 1600 | **删除** |
| `SkipTransitionTimeoutMs` | 10000 | 保留（作 SwitchIntent.Deadline）|
| `NaturalEndingTransitionTimeoutMs` | 3000 | 保留 |
| `SkipTransitionFreshTrackPositionSeconds` | 3.0 | 保留 |
| `StabilizationMetadataConfirmationHoldMs` | 80 | **删除** |
| `MediaMetadataSettleMs` | — | **新增 = 250** |
| `MediaAutoSwitchDebounceMs` | 已有 | 保留 |
| `MediaMissingGraceMs` | 已有 | 保留 |
| `SelectionLockDurationMs` | 已有 | 保留 |
