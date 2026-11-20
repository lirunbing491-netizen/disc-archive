# 7-Zip 26.06：格式能力与边界

应用通过独立、可替换的 x64/x86 `7z.dll` 读取归档和文件系统视图。对应版本、完整源码包、许可及替换方式见仓库根目录的 `THIRD-PARTY-NOTICES.txt`。本说明解释格式边界，不作为真实光盘、受损介质或用户自然使用效果的验收记录。

| 类别 | 上游能力与使用边界 |
|---|---|
| 古典 HFS / Mac OS Standard | 上游 HFS 处理器不支持 old HFS；HFS wrapper 仅在含可识别的嵌入 HFS+ 时适用。不能把目录读取失败说成空盘。 |
| HFS+ / HFSX | 上游包含处理器；具体文件、大小写重名和数据完整性仍须逐项核验。 |
| HFS+ journal | 不能据现有目录读取能力声称支持日志重放、未提交事务恢复或文件系统修复。 |
| APM / GPT / MBR | 它们是分区容器，不是文件系统。须按边界读取分区内的视图，不能将整个映像直接视为单一卷。 |
| Hybrid | ISO、HFS、APM、UDF 可能提供不同名称或元数据。一个视图读取成功不能证明所有视图已恢复。 |
| ISO 9660 / Joliet | 上游包含 ISO 处理器。卷描述符与 Joliet 支持不等于已枚举物理盘全部会话。 |
| UDF | 上游包含 UDF 处理器；不据此承诺多会话、包写或坏块恢复效果。 |
| Mac resource fork / 属性 | 上游 HFS 处理器提供辅助流信息。仅复制 data fork 不代表资源叉与属性已完整保存；侧车文件及冲突处理需要单独核验。 |
| DVD-Video | ISO/UDF 可提供普通文件路径。保留 VIDEO_TS、IFO、BUP、VOB 层级不等于导航或播放成功，不承诺解密或坏盘修复。 |
| VCD / CD-XA | 文件系统目录与原始轨道有效载荷不同。2048 字节数据视图不代表已无损恢复 VCD；须另核验扇区模式和轨道数据。 |
| CD-DA / Mixed-mode | 音轨不是普通文件。原始轨道读取与 TOC 路径的构造测试不能替代实盘音频和混合模式验收。 |
| 多会话 | 多卷描述符不等于物理多会话。须结合会话 TOC、偏移和实盘验证，末会话可见目录不代表全盘。 |
| APFS | 上游包含 APFS 处理器，但存在不支持的特性分支；不承诺加密、所有快照或全部特性可读。 |

可公开核对的上游资料：

- [固定版本发布](https://github.com/ip7z/7zip/releases/tag/26.06)
- [HFS 处理器](https://github.com/ip7z/7zip/blob/26.06/CPP/7zip/Archive/HfsHandler.cpp)
- [ISO 处理器](https://github.com/ip7z/7zip/blob/26.06/CPP/7zip/Archive/Iso/IsoIn.cpp)
- [APFS 处理器](https://github.com/ip7z/7zip/blob/26.06/CPP/7zip/Archive/ApfsHandler.cpp)
- [Apple HFS Plus Volume Format](https://developer.apple.com/library/archive/technotes/tn/tn1150.html)
- [Microsoft 会话 TOC 查询](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddcdrm/ni-ntddcdrm-ioctl_cdrom_read_toc_ex)
- [Microsoft 原始轨道读取](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddcdrm/ni-ntddcdrm-ioctl_cdrom_raw_read)

仓库测试使用生成的合成素材；测试通过只覆盖对应构造情形。真实受损介质、具体光驱和相机、实际播放质量以及完整自然使用体验均须独立验证。
