// macOS 13+, ARC. Only system frameworks; no subprocesses and no privilege changes.
// Discovery: Apple IOCDBlockStorageDevice superclass includes DVD/BD providers.
// https://github.com/apple-oss-distributions/IOCDStorageFamily/blob/main/IOCDBlockStorageDevice.h
// https://developer.apple.com/library/archive/documentation/DriversKernelHardware/Conceptual/DiskArbitrationProgGuide/ManipulatingDisks/ManipulatingDisks.html
#import "DiscDevices.h"
#import <Foundation/Foundation.h>
#import <IOKit/IOKitLib.h>
#import <IOKit/storage/IOMedia.h>
#import <DiskArbitration/DiskArbitration.h>
#import <DiscRecording/DiscRecording.h>
#import <dispatch/dispatch.h>
#include <stdint.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <dirent.h>
#include <errno.h>
#include <stddef.h>

static NSString *DAString(id value) { return [value isKindOfClass:NSString.class] ? value : @""; }
static NSNumber *DANumber(id value) { return [value isKindOfClass:NSNumber.class] ? value : @0; }
static NSDictionary *DAProperties(io_registry_entry_t entry) {
    CFMutableDictionaryRef p = NULL;
    if (IORegistryEntryCreateCFProperties(entry, &p, kCFAllocatorDefault, 0) != KERN_SUCCESS) return @{};
    return CFBridgingRelease(p) ?: @{};
}
static NSString *DAID(io_registry_entry_t entry) {
    uint64_t value = 0;
    if (IORegistryEntryGetRegistryEntryID(entry, &value) != KERN_SUCCESS || !value) return @"";
    return [NSString stringWithFormat:@"%llu", (unsigned long long)value];
}
static NSDictionary *DADescription(DASessionRef session, io_service_t media) {
    if (!session) return @{};
    DADiskRef disk = DADiskCreateFromIOMedia(kCFAllocatorDefault, session, media);
    if (!disk) return @{};
    NSDictionary *d = CFBridgingRelease(DADiskCopyDescription(disk));
    CFRelease(disk);
    return d ?: @{};
}
static NSString *DAPath(id value) { return [value isKindOfClass:NSURL.class] ? [(NSURL *)value path] ?: @"" : @""; }
static NSString *DARaw(NSString *bsd) {
    // Never convert arbitrary strings into device paths.
    NSRegularExpression *r = [NSRegularExpression regularExpressionWithPattern:@"^disk[0-9]+(s[0-9]+)*$" options:0 error:NULL];
    return [r numberOfMatchesInString:bsd options:0 range:NSMakeRange(0, bsd.length)] == 1 ? [@"/dev/r" stringByAppendingString:bsd] : @"";
}
static BOOL DAOptical(io_service_t entry) { return IOObjectConformsTo(entry, "IOCDBlockStorageDevice"); }

// Public IOMedia.h exposes Size/Whole/Preferred Block Size, but no Base key.
// Registry ancestry alone cannot prove a partition's byte displacement. Only
// the selected media itself has a proven zero offset; all others remain unknown.
// Preserve every partition/raw/mount independently even when its offset is -1.
static NSNumber *DAOffset(NSString *mediaID, NSString *wholeID) {
    return mediaID.length && wholeID.length && [mediaID isEqualToString:wholeID] ? @0 : @(-1);
}

static NSString *DAState(BOOL hasReadableMedia, NSString *reported) {
    if ([reported isEqualToString:(__bridge NSString *)kDRDeviceMediaStateNone]) return @"empty";
    if ([reported isEqualToString:(__bridge NSString *)kDRDeviceMediaStateInTransition]) return @"not-ready";
    return hasReadableMedia ? @"ready" : @"not-ready";
}
static NSString *DASessionIdentity(void) {
    static NSString *session;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ session = NSUUID.UUID.UUIDString; });
    return session;
}
static NSString *DAToken(NSString *drive, NSArray<NSString *> *mediaIDs) {
    if (!drive.length || !mediaIDs.count) return @"";
    return [NSString stringWithFormat:@"%@:%@:%@", DASessionIdentity(), drive, [[mediaIDs sortedArrayUsingSelector:@selector(compare:)] componentsJoinedByString:@","]];
}
static BOOL DATokenBelongsHere(NSString *drive, NSString *token) {
    if (!drive.length || drive.length > 20 || !token.length || token.length > 65536) return NO;
    NSCharacterSet *nonDigits = [NSCharacterSet characterSetWithCharactersInString:@"0123456789"].invertedSet;
    if ([drive rangeOfCharacterFromSet:nonDigits].location != NSNotFound) return NO;
    NSString *prefix = [NSString stringWithFormat:@"%@:%@:", DASessionIdentity(), drive];
    return [token hasPrefix:prefix] && token.length > prefix.length;
}
#ifdef DA_DEVICES_TEST
static BOOL DAMatches(NSDictionary *drive, NSString *identity, NSString *token) {
    return identity.length && token.length && [drive[@"id"] isEqualToString:identity] && [drive[@"token"] isEqualToString:token] && [drive[@"ready"] boolValue];
}
#endif

static io_service_t DAProvider(NSString *identity) {
    io_iterator_t iterator = IO_OBJECT_NULL;
    if (IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOCDBlockStorageDevice"), &iterator) != KERN_SUCCESS) return IO_OBJECT_NULL;
    io_service_t entry, found = IO_OBJECT_NULL;
    while ((entry = IOIteratorNext(iterator))) {
        if ([DAID(entry) isEqualToString:identity]) { found = entry; break; }
        IOObjectRelease(entry);
    }
    IOObjectRelease(iterator);
    return found;
}
// Repeated read guard: IOKit properties only, no DA session, mount enumeration,
// DiscRecording polling, raw data read, or subprocess.
static NSString *DACurrentToken(io_service_t provider) {
    io_string_t path;
    if (IORegistryEntryGetPath(provider, kIOServicePlane, path) != KERN_SUCCESS) return @"";
    NSMutableArray *IDs = [NSMutableArray array];
    BOOL validMedia = NO;
    io_iterator_t iterator = IO_OBJECT_NULL;
    if (IORegistryEntryCreateIterator(provider, kIOServicePlane, kIORegistryIterateRecursively, &iterator) != KERN_SUCCESS) return @"";
    io_service_t media;
    while ((media = IOIteratorNext(iterator))) {
        if (IOObjectConformsTo(media, kIOMediaClass)) {
            NSString *identity = DAID(media);
            if (identity.length) [IDs addObject:identity];
            NSDictionary *p = DAProperties(media);
            if (DARaw(DAString(p[@"BSD Name"])).length && [DANumber(p[@kIOMediaSizeKey]) longLongValue] > 0 && [DANumber(p[@kIOMediaPreferredBlockSizeKey]) intValue] > 0) validMedia = YES;
        }
        IOObjectRelease(media);
    }
    IOObjectRelease(iterator);
    if (!validMedia || IORegistryEntryGetPath(provider, kIOServicePlane, path) != KERN_SUCCESS) return @"";
    return DAToken(DAID(provider), IDs);
}

// Parse only a complete OS-published full CD TOC. Truncated/unknown TOCs are
// never evidence of full coverage. Layout: Apple IOCDTypes.h CDTOCDescriptor.
static NSDictionary *DATOCReport(NSData *data) {
    if (![data isKindOfClass:NSData.class] || data.length < 4) return nil;
    const uint8_t *b = data.bytes;
    size_t size = ((size_t)b[0] << 8) + b[1] + 2;
    if (size < 4 || size > data.length || (size - 4) % 11 || b[2] == 0 || b[3] < b[2]) return nil;
    BOOL sessions = b[2] > 0 && b[3] > b[2];
    BOOL audio = NO;
    for (size_t offset = 4; offset + 11 <= size; offset += 11) {
        const uint8_t *d = b + offset;
        if ((d[1] >> 4) == 1 && d[3] >= 1 && d[3] <= 99 && !(d[1] & 4)) audio = YES;
    }
    NSString *note;
    if (sessions && audio) note = @"系统 TOC 显示多会话和音轨；当前文件/逻辑镜像不覆盖全部物理内容。";
    else if (sessions) note = @"系统 TOC 显示多个会话；不能把当前挂载卷当作所有历史会话已提取。";
    else if (audio) note = @"系统 TOC 显示音轨；当前文件/普通逻辑镜像不等于音轨已提取。";
    else note = @"可见 TOC 为单一数据视图；未发现额外未覆盖内容的证据，历史会话和完整物理覆盖仍未知。";
    return @{@"uncovered":@(sessions || audio), @"note":note};
}

static NSDictionary *DADrive(io_service_t provider, DASessionRef session) {
    NSString *identity = DAID(provider);
    if (!identity.length) return nil;
    NSDictionary *properties = DAProperties(provider);
    id deviceProperties = properties[@"Device Characteristics"];
    NSDictionary *characteristics = [deviceProperties isKindOfClass:NSDictionary.class] ? deviceProperties : @{};
    NSString *vendor = DAString(characteristics[@"Vendor Name"]);
    NSString *product = DAString(characteristics[@"Product Name"]);
    NSString *name = [[NSString stringWithFormat:@"%@ %@", vendor, product] stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (!name.length) name = @"Optical drive";
    NSMutableArray<NSMutableDictionary *> *media = [NSMutableArray array];
    io_iterator_t descendants = IO_OBJECT_NULL;
    if (IORegistryEntryCreateIterator(provider, kIOServicePlane, kIORegistryIterateRecursively, &descendants) == KERN_SUCCESS) {
        io_registry_entry_t child;
        while ((child = IOIteratorNext(descendants))) {
            if (IOObjectConformsTo(child, kIOMediaClass)) {
                NSDictionary *p = DAProperties(child), *d = DADescription(session, child);
                NSString *bsd = DAString(p[@"BSD Name"]);
                [media addObject:[@{@"id":DAID(child), @"raw":DARaw(bsd), @"mount":DAPath(d[(__bridge NSString *)kDADiskDescriptionVolumePathKey]),
                    @"label":DAString(d[(__bridge NSString *)kDADiskDescriptionVolumeNameKey]), @"format":DAString(d[(__bridge NSString *)kDADiskDescriptionVolumeKindKey]),
                    @"bytes":DANumber(p[@kIOMediaSizeKey]), @"sector":DANumber(p[@kIOMediaPreferredBlockSizeKey]),
                    @"whole":@([p[@kIOMediaWholeKey] boolValue]), @"_entry":@(child)} mutableCopy]];
                // Keep this reference through offset calculation below.
                continue;
            }
            IOObjectRelease(child);
        }
        IOObjectRelease(descendants);
    }
    // Prefer the whole data media, largest first; retain every additional media
    // in partitions so callers do not silently discard multi-session volumes.
    [media sortUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) {
        if ([a[@"whole"] boolValue] != [b[@"whole"] boolValue]) return [a[@"whole"] boolValue] ? NSOrderedAscending : NSOrderedDescending;
        NSComparisonResult size = [b[@"bytes"] compare:a[@"bytes"]];
        return size == NSOrderedSame ? [a[@"id"] compare:b[@"id"]] : size;
    }];
    NSDictionary *primary = media.firstObject ?: @{};
    NSMutableArray *IDs = [NSMutableArray array];
    NSString *mount = @"", *label = @"", *format = @"";
    for (NSMutableDictionary *m in media) {
        if ([m[@"id"] length]) [IDs addObject:m[@"id"]];
        io_service_t entry = (io_service_t)[m[@"_entry"] unsignedIntValue];
        m[@"offset"] = DAOffset(m[@"id"], primary[@"id"] ?: @"");
        IOObjectRelease(entry); [m removeObjectForKey:@"_entry"];
        if (!mount.length && [m[@"mount"] length]) { mount=m[@"mount"]; label=m[@"label"]; format=m[@"format"]; }
    }
    // DiscRecording only reads device status. Unknown != empty.
    NSString *reported = @"";
    io_string_t path;
    if (IORegistryEntryGetPath(provider, kIOServicePlane, path) == KERN_SUCCESS) {
        DRDeviceRef device = DRDeviceCopyDeviceForIORegistryEntryPath((__bridge CFStringRef)[NSString stringWithUTF8String:path]);
        if (device) {
            NSDictionary *status = CFBridgingRelease(DRDeviceCopyStatus(device));
            reported = DAString(status[(__bridge NSString *)kDRDeviceMediaStateKey]);
            CFRelease(device);
        }
    } else return nil; // Detached during enumeration: never return a ready row.
    BOOL readable = [primary[@"raw"] length] && [primary[@"bytes"] longLongValue] > 0 && [primary[@"sector"] intValue] > 0;
    NSString *state = DAState(readable, reported);
    BOOL ready = [state isEqualToString:@"ready"];
    return @{@"id":identity, @"name":name, @"empty":@([state isEqualToString:@"empty"]), @"ready":@(ready), @"state":state,
        @"token":ready ? DAToken(identity, IDs) : @"", @"mount":mount, @"label":label, @"format":format,
        @"raw":primary[@"raw"] ?: @"", @"bytes":primary[@"bytes"] ?: @0, @"sector":primary[@"sector"] ?: @0,
        @"coverage":readable ? @"published-logical-media-only" : @"none", @"partitions":media};
}

static BOOL DAHasPhysicalExternalProvider(io_service_t media) {
    io_registry_entry_t current = media;
    IOObjectRetain(current);
    BOOL found = NO;
    for (unsigned depth = 0; current && depth < 128; ++depth) {
        if (DAOptical(current)) break;
        if (IOObjectConformsTo(current, "IOBlockStorageDevice")) { found = YES; break; }
        io_registry_entry_t parent = IO_OBJECT_NULL;
        IORegistryEntryGetParentEntry(current, kIOServicePlane, &parent);
        IOObjectRelease(current); current = parent;
    }
    if (current) IOObjectRelease(current);
    return found;
}
static NSArray *DATargets(DASessionRef session) {
    NSMutableArray *targets = [NSMutableArray array];
    if (!session) return targets;
    NSArray *volumes = [NSFileManager.defaultManager mountedVolumeURLsIncludingResourceValuesForKeys:@[NSURLVolumeIsReadOnlyKey, NSURLVolumeIsLocalKey] options:0];
    for (NSURL *url in volumes) {
        NSDictionary *values = [url resourceValuesForKeys:@[NSURLVolumeIsReadOnlyKey, NSURLVolumeIsLocalKey] error:NULL];
        if (!values || ![values[NSURLVolumeIsLocalKey] boolValue] || ![values[NSURLVolumeIsReadOnlyKey] isEqual:@NO] || access(url.fileSystemRepresentation, W_OK) != 0) continue;
        DADiskRef disk = DADiskCreateFromVolumePath(kCFAllocatorDefault, session, (__bridge CFURLRef)url);
        if (!disk) continue;
        NSDictionary *d = CFBridgingRelease(DADiskCopyDescription(disk));
        io_service_t m = DADiskCopyIOMedia(disk);
        CFRelease(disk);
        // Missing internal flag is unknown and therefore excluded. Exclude disk
        // images/network volumes: a real block storage provider is required.
        BOOL acceptable = [d[(__bridge NSString *)kDADiskDescriptionDeviceInternalKey] isEqual:@NO] && m && DAHasPhysicalExternalProvider(m);
        NSString *identity = m ? DAID(m) : @"";
        if (m) IOObjectRelease(m);
        if (acceptable && identity.length) [targets addObject:@{@"name":DAString(d[(__bridge NSString *)kDADiskDescriptionVolumeNameKey]), @"path":url.path ?: @"", @"id":identity}];
    }
    return [targets sortedArrayUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) { return [a[@"path"] compare:b[@"path"]]; }];
}
char *da_devices(void) {
    @autoreleasepool {
        DASessionRef session = DASessionCreate(kCFAllocatorDefault);
        NSMutableArray *drives = [NSMutableArray array];
        io_iterator_t iterator = IO_OBJECT_NULL;
        kern_return_t result = IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOCDBlockStorageDevice"), &iterator);
        if (result != KERN_SUCCESS) { if (session) CFRelease(session); return NULL; }
        io_service_t provider;
        while ((provider = IOIteratorNext(iterator))) {
            NSDictionary *drive = DADrive(provider, session);
            if (drive) [drives addObject:drive];
            IOObjectRelease(provider);
        }
        IOObjectRelease(iterator);
        [drives sortUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) { return [a[@"id"] compare:b[@"id"]]; }];
        NSDictionary *snapshot = @{@"drives":drives, @"targets":DATargets(session)};
        if (session) CFRelease(session);
        NSData *data = [NSJSONSerialization dataWithJSONObject:snapshot options:0 error:NULL];
        if (!data) return NULL;
        char *out = malloc(data.length + 1);
        if (!out) return NULL;
        memcpy(out, data.bytes, data.length); out[data.length] = '\0'; return out;
    }
}
int da_verify(const char *drive, const char *token) {
    if (!drive || !token || !*drive || !*token) return 0;
    if (strnlen(drive, 21) > 20 || strnlen(token, 65537) > 65536) return 0;
    @autoreleasepool {
        NSString *identity = [NSString stringWithUTF8String:drive], *expected = [NSString stringWithUTF8String:token];
        if (!identity || !expected || !DATokenBelongsHere(identity, expected)) return 0;
        io_service_t provider = DAProvider(identity);
        if (!provider) return 0;
        NSString *current = DACurrentToken(provider);
        IOObjectRelease(provider);
        return current.length && [current isEqualToString:expected] ? 1 : 0;
    }
}
char *da_tracks(const char *drive, const char *token) {
    @autoreleasepool {
        NSString *note = @"仅可见数据视图；未取得额外未覆盖内容的可靠证据，历史会话和完整物理覆盖未知。";
        BOOL uncovered = NO;
        if (!da_verify(drive, token)) note = @"介质已改变、断连或未就绪；轨道覆盖未知，请重新选择源盘。";
        else {
            io_service_t provider = DAProvider([NSString stringWithUTF8String:drive]);
            if (provider) {
                io_iterator_t iterator = IO_OBJECT_NULL;
                if (IORegistryEntryCreateIterator(provider, kIOServicePlane, kIORegistryIterateRecursively, &iterator) == KERN_SUCCESS) {
                    io_service_t media;
                    while ((media = IOIteratorNext(iterator))) {
                        if (IOObjectConformsTo(media, kIOMediaClass)) {
                            NSDictionary *candidate = DATOCReport(DAProperties(media)[@"TOC"]);
                            if (candidate && (!uncovered || [candidate[@"uncovered"] boolValue])) {
                                note = candidate[@"note"];
                                uncovered = [candidate[@"uncovered"] boolValue];
                            }
                        }
                        IOObjectRelease(media);
                    }
                    IOObjectRelease(iterator);
                }
                IOObjectRelease(provider);
            }
            if (!da_verify(drive, token)) { note = @"清点期间介质发生变化，轨道覆盖未知。"; uncovered = NO; }
        }
        // Unknown coverage alone is not evidence that a known part was missed.
        NSData *data = [NSJSONSerialization dataWithJSONObject:@{@"uncovered":@(uncovered), @"note":note} options:0 error:NULL];
        if (!data) return NULL;
        char *result = malloc(data.length + 1);
        if (!result) return NULL;
        memcpy(result, data.bytes, data.length); result[data.length] = '\0'; return result;
    }
}
void da_free(void *pointer) { free(pointer); }

// Let the real SDK select BOTH fdopendir/readdir symbol aliases and struct
// dirent layout. A managed hard-coded layout/suffix can mismatch Intel Darwin.
char *da_list_directory(int fd) {
    @autoreleasepool {
        NSMutableArray<NSString *> *names = [NSMutableArray array];
        NSMutableArray<NSString *> *errors = [NSMutableArray array];
        NSString *cleanupNote = @"";
        int duplicate = dup(fd);
        if (duplicate < 0) [errors addObject:[NSString stringWithFormat:@"无法复制目录读取句柄（errno %d）。", errno]];
        else {
            DIR *directory = fdopendir(duplicate);
            if (!directory) {
                int failure = errno;
                if (close(duplicate) != 0) cleanupNote = [NSString stringWithFormat:@"目录副本句柄退出清理异常（errno %d）；不改变已读取条目状态。", errno];
                [errors addObject:[NSString stringWithFormat:@"无法遍历已打开目录（errno %d）。", failure]];
            } else {
                unsigned invalid = 0;
                for (;;) {
                    errno = 0;
                    struct dirent *entry = readdir(directory);
                    if (!entry) {
                        int failure = errno;
                        if (failure) [errors addObject:[NSString stringWithFormat:@"目录遍历中断（errno %d）。", failure]];
                        break;
                    }
                    size_t length = strnlen(entry->d_name, sizeof(entry->d_name));
                    if (!length || length == sizeof(entry->d_name) || length != entry->d_namlen ||
                        entry->d_reclen < offsetof(struct dirent, d_name) + length + 1) { ++invalid; continue; }
                    NSString *name = [[NSString alloc] initWithBytes:entry->d_name length:length encoding:NSUTF8StringEncoding];
                    if (!name || [name rangeOfString:@"/"].location != NSNotFound) { ++invalid; continue; }
                    if ([name isEqualToString:@"."] || [name isEqualToString:@".."]) continue;
                    [names addObject:name];
                }
                if (invalid) [errors addObject:[NSString stringWithFormat:@"%u 个目录条目的边界或UTF-8名称无法无损表示；已保留其余可读同级项。", invalid]];
                if (closedir(directory) != 0) cleanupNote = [NSString stringWithFormat:@"目录读取句柄退出清理异常（errno %d）；不改变已读取条目状态。", errno];
            }
        }
        [names sortUsingSelector:@selector(compare:)];
        NSData *data = [NSJSONSerialization dataWithJSONObject:@{@"names":names, @"error":[errors componentsJoinedByString:@" "], @"note":cleanupNote} options:0 error:NULL];
        if (!data) return NULL;
        char *result = malloc(data.length + 1);
        if (!result) return NULL;
        memcpy(result, data.bytes, data.length); result[data.length] = '\0'; return result;
    }
}

#ifdef DA_DEVICES_TEST
#include <assert.h>
int main(void) {
    @autoreleasepool {
        assert([DARaw(@"disk12s3") isEqualToString:@"/dev/rdisk12s3"]);
        assert(!DARaw(@"disk2;touch /tmp/x").length);
        assert(!DARaw(@"../disk1").length);
        assert([DAOffset(@"301", @"301") longLongValue] == 0);
        assert([DAOffset(@"302", @"301") longLongValue] == -1); // Preserve another volume; never invent its offset.
        assert([DAOffset(@"", @"") longLongValue] == -1);
        assert([DAState(NO, @"") isEqualToString:@"not-ready"]);
        assert([DAState(NO, (__bridge NSString *)kDRDeviceMediaStateNone) isEqualToString:@"empty"]);
        assert([DAState(YES, (__bridge NSString *)kDRDeviceMediaStateInTransition) isEqualToString:@"not-ready"]);
        assert([DAState(YES, @"") isEqualToString:@"ready"]);
        NSString *a = DAToken(@"101", @[@"301", @"302"]);
        assert([a isEqualToString:DAToken(@"101", @[@"302", @"301"])]);
        assert(![a isEqualToString:DAToken(@"101", @[@"401", @"402"])]); // same label, new disc
        assert(![a isEqualToString:DAToken(@"102", @[@"301", @"302"])]); // reconnect/other drive
        assert(!DAToken(@"101", @[]).length);
        assert(DATokenBelongsHere(@"101", a));
        assert(!DATokenBelongsHere(@"102", a));
        assert(!DATokenBelongsHere(@"101", @"OTHER-PROCESS:101:301,302"));
        assert(!da_verify(NULL, NULL));
        assert(!da_verify("", ""));
        assert(!da_verify("101", "OTHER-PROCESS:101:301,302")); // rejected before IOKit
        assert(!da_verify("\xff", "invalid"));
        NSDictionary *d = @{@"id":@"101", @"token":a, @"ready":@YES, @"label":@"DUPLICATE"};
        assert(DAMatches(d, @"101", a));
        assert(!DAMatches(d, @"102", a));
        assert(!DAMatches(d, @"101", @"DUPLICATE"));
        assert(!DAMatches(@{@"id":@"101", @"token":a, @"ready":@NO}, @"101", a));
        const uint8_t toc[] = {0, 13, 1, 2, 1, 0x10, 0, 1, 0, 0, 0, 0, 0, 2, 0};
        NSDictionary *audioReport = DATOCReport([NSData dataWithBytes:toc length:sizeof(toc)]);
        assert([audioReport[@"note"] containsString:@"多会话和音轨"]);
        assert([audioReport[@"uncovered"] boolValue]);
        assert(!DATOCReport([NSData dataWithBytes:toc length:5]));
        uint8_t dataTOC[sizeof(toc)]; memcpy(dataTOC, toc, sizeof(toc));
        dataTOC[3] = 1; dataTOC[5] = 0x14;
        NSDictionary *dataReport = DATOCReport([NSData dataWithBytes:dataTOC length:sizeof(dataTOC)]);
        assert(dataReport && ![dataReport[@"uncovered"] boolValue]);
        assert([dataReport[@"note"] containsString:@"完整物理覆盖仍未知"]);
        dataTOC[1] = 12; // Partial descriptor is invalid, never negative evidence.
        assert(!DATOCReport([NSData dataWithBytes:dataTOC length:sizeof(dataTOC)]));
        char *directoryError = da_list_directory(-1);
        assert(directoryError);
        NSDictionary *directoryReport = [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:directoryError length:strlen(directoryError)] options:0 error:NULL];
        assert([directoryReport[@"names"] count] == 0 && [directoryReport[@"error"] length] > 0);
        da_free(directoryError);
        puts("DiscDevices synthetic policy tests passed (no hardware accessed).");
    }
    return 0;
}
#endif
