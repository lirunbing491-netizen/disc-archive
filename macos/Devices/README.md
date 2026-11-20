# 只读设备桥（macOS 13 及以上）

此目录只负责发现设备和核对介质实例，不弹出、不挂载、不卸载、不写源盘，也不要求管理员权限。发布后的程序使用系统框架，用户无须安装额外运行环境；下面的构建与测试命令需要开发者的 Xcode Command Line Tools。

在项目根目录构建（架构跟随当前编译器；发布通用包时分别构建 arm64/x86_64 再合并）：

```sh
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -mmacosx-version-min=13.0 -dynamiclib macos/Devices/DiscDevices.m -framework Foundation -framework IOKit -framework DiskArbitration -framework DiscRecording -o libDiscDevices.dylib
```

纯合成测试，不查询真实设备、不调用 `da_devices`：

```sh
xcrun clang -fobjc-arc -fblocks -Wall -Wextra -mmacosx-version-min=13.0 -DDA_DEVICES_TEST macos/Devices/DiscDevices.m -framework Foundation -framework IOKit -framework DiskArbitration -framework DiscRecording -o DiscDevicesTests
./DiscDevicesTests
```

测试覆盖：真实设备节点格式限定、未知状态不当成空盘、介质转换期间不就绪、同卷标换盘、同介质节点换光驱、介质集合顺序稳定、无媒体不发 token、未就绪不通过校验。合成测试不能证明某款光驱能被 macOS 正确识别或读取。

## 调用约定

- `da_devices()`：返回由 `malloc` 分配、以 NUL 结尾的 UTF-8 JSON。失败返回 NULL，调用方应显示发现失败并暂停，不能将其当成“没有设备”。用 `da_free()` 释放返回值。可在后台线程定期调用；每次重新查询 IOKit 和挂载卷，不缓存设备列表。
- `da_verify(drive, token)`：通过 IOKit 核对当前 provider ID、全部 IOMedia ID 及有效逻辑节点是否仍存在，匹配才返回 1。为重复读取设计，不枚举目标卷、不建立 DA 会话、不轮询 DiscRecording 状态、不读盘面，不运行子进程。它不能证明驱动器当前没有忙碌或介质错误；此检查应在每个有界读取之前和之后调用，仍需处理检查与后续 open/read 间的竞态、ENXIO、EIO 和节点被替换。核对失败时不能把该读取块写成有效恢复结果。
- `da_tracks(drive, token)`：返回 `{uncovered:bool,note:string}`，使用 IOMedia 已发布且长度完整的 CD TOC 检查音轨及多会话。仅可靠证据显示音轨或多个会话时 `uncovered=true`；普通单一数据视图或没有可靠 TOC 时返回 false，说明覆盖未知，不将普通数据 DVD 仅因缺少 TOC 判成失败或部分完成。false 也不是完整物理覆盖证明；实际可见数据的成功状态由恢复流程判定。前后核对相同介质，不发出底层读取轨道命令。返回值同样由 `da_free()` 释放。
- token 绑定当前进程会话随机 UUID、光驱 IORegistry entry ID 及全部介质 IORegistry entry ID，不依赖卷标、BSD 名或挂载路径，不用于判断两次插入是否为同一内容。UI 只传 drive ID 给 core；每个 core 必须调用自己的 `da_devices` 取得 token，再调用同进程 `da_verify`，不能拿 UI 或另一 worker 的 token 校验。验证函数会在查询硬件前拒绝来自其他会话、空值、无效 UTF-8 及超长/不合法 ID。应用重启或介质重新发布时需取得新 token；该字段不能当成跨进程/跨启动的恢复任务永久 ID。
- 无法区分驱动未报告的物理换盘。身份保障基于 macOS 发布/撤销 IOMedia 实例这一实际边界；不声称能识别硬件或系统从未报告的变化。

## 字段

`drives` 仅匹配 `IOCDBlockStorageDevice` 及子类，普通可移动硬盘不因 removable 属性进入光驱列表。该 provider 在空托盘时仍可存在。物理拔掉后，下次快照移除该行；调用方应将此前选中的消失 ID 显示为已断连。

`state` 有 `empty`、`not-ready`、`ready` 三种值。只有 DiscRecording 明确报告无媒体才标记 `empty=true`；查询失败、没有 IOMedia、正在旋转/转换、无有效逻辑节点/大小/扇区信息都不能当成就绪。`ready` 表示系统已发布逻辑媒体信息，不证明实际读成功或当前用户已获得原始设备读取权限。原始读取的权限拒绝应如实反馈，不能自动提权或更改系统设置。

`raw` 是从 IOMedia 的 BSD 名构成的真实 `/dev/rdisk…` 节点，`bytes` / `sector` 来自 IOMedia；即使未挂载也可返回。此桥不为获取状态而扫描盘面。主节点优先 whole media，再按大小排序。`coverage=published-logical-media-only` 明确只覆盖系统公布的逻辑媒体，不保证音轨、隐藏会话、导入区、子通道或整张物理光盘全部被表示。不能把一份逻辑镜像宣称为完整音频/混合模式光盘保全。

附加 `partitions` 包含发现的全部 IOMedia，保留 `id/raw/mount/label/format/bytes/sector/whole/offset`。`offset` 是相对选中主节点的字节偏移；只有祖先关系可以证明时提供非负值，否则为 -1。不要把同一数据的 whole 与子分区简单拼接，也不要把 -1 当作 0。主行 `mount/label/format` 只是首个已挂载介质的便利显示；需要处理多会话/分区时应使用全部数组。

`targets` 只返回已挂载、本地、非只读、当前用户通过可写权限预检查、由真实块设备承载且明确标记 external 的卷。未知/缺失 external 证据的卷保守排除，网络卷、普通磁盘镜像及光驱不列入。挂载期间也可能移除、只读或写满，实际输出前仍须校验目标并处理错误；`access` 不是写入成功保证。APFS 外接卷和不同桥接芯片的实际枚举需要硬件验收。

手动选择目录由上层系统目录选择器提供，不限制固定卷名或路径。用户所选目录可能不在自动目标列表中，上层需单独确认其路径、可写性、剩余空间和当前介质身份；此桥不建立保存目录、不写探测文件。

## 尚需在 Mac 上验证

Windows 开发环境没有 Apple SDK，未在此环境执行 Objective-C 编译或任何真实硬件测试。必须在实际 Mac 上验证构建、空托盘、插入/弹出、未挂载光盘、同卷标连续换盘、USB 拔插、多个光驱、多块轮换外接盘、权限拒绝、APFS/HFS+/exFAT 目标、音频/混合模式及损坏盘。不能用合成测试结果代替这些验收。

官方接口依据：

- [Apple 光驱 provider 定义](https://github.com/apple-oss-distributions/IOCDStorageFamily/blob/main/IOCDBlockStorageDevice.h)
- [Apple IOMedia 属性](https://github.com/apple-oss-distributions/IOStorageFamily/blob/main/IOMedia.h)
- [Apple Disk Arbitration 获取设备及卷信息](https://developer.apple.com/library/archive/documentation/DriversKernelHardware/Conceptual/DiskArbitrationProgGuide/ManipulatingDisks/ManipulatingDisks.html)
- DiscRecording 状态常量及 C 函数以本机 SDK `DiscRecording/DRCoreDevice.h` 为准；仅调用 CopyDevice/CopyStatus，不调用刻录、擦除、托盘或预留接口。
